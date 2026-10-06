package com.netpilot.mobile.vpn

import android.app.Notification
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import com.netpilot.mobile.core.log.NpLog
import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.data.Repo
import com.netpilot.mobile.net.DnsForwarder
import com.netpilot.mobile.service.QuickDns
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.ByteArrayOutputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import kotlin.random.Random

/**
 * Local VPN tunnel.
 *
 * The tunnel routes *only* the DNS resolver address (10.111.222.53) into a TUN device,
 * so the app can answer every DNS query the system makes — that is how "Apply DNS" works
 * on Android without root, and it is also the measurement point for real response times.
 * Nothing else is routed, so no other app traffic is ever touched or dropped.
 *
 * The same object later hosts the per-app firewall/limiter; the DNS path stays active
 * in both modes (see [VpnState.Mode]).
 */
class NetPilotVpnService : VpnService() {

    companion object {
        const val ACTION_APPLY = "com.netpilot.mobile.vpn.APPLY"
        const val ACTION_FIREWALL = "com.netpilot.mobile.vpn.FIREWALL"
        const val ACTION_RESTORE = "com.netpilot.mobile.vpn.RESTORE"
        const val ACTION_STOP = "com.netpilot.mobile.vpn.STOP"
        const val ACTION_FLUSH = "com.netpilot.mobile.vpn.FLUSH"
        const val EXTRA_DNS_ID = "dnsId"

        /** Sweep the DNS-over-TCP session map at most this often (packets, not time). */
        private const val SWEEP_EVERY_PACKETS = 64
        private const val TCP_SESSION_IDLE_MS = 120_000L
        private const val TCP_SESSION_CLOSE_MS = 5_000L

        /**
         * The address the system is told to resolve through — the single /32 routed into
         * the TUN. It has to be a *syntactically valid* IPv4 literal: Builder hands both
         * addDnsServer() and addRoute() to InetAddress.parseNumericAddress(), which throws
         * on anything else. The old value here had an octet of 333, so every "Apply DNS"
         * died on the Builder before establish() was ever reached.
         */
        internal const val FAKE_DNS = "10.111.222.53"
        internal const val LOCAL_ADDR = "10.111.222.1"
        private const val NOTIF_ID = 4711

        /** Only DNS is routed into the tunnel in DNS mode, so an answer is all it must fit. */
        /**
         * MTU of the tunnel interface.
         *
         * It used to be 4096, which nothing on a real network can carry: the phone writes
         * packets up to that size into the tunnel, they get IP-fragmented on the first hop,
         * and reassembly is where the throughput goes - users saw the tunnel work and crawl.
         * 1400 is the value WireGuard and the other mainstream Android tunnels use precisely
         * because it survives Wi-Fi, cellular and a second encapsulation (a VPN inside the
         * tunnel) without fragmenting.
         */
        private const val TUN_MTU = 1400

        /**
         * Per-app attribution is `ConnectivityManager.getConnectionOwnerUid`, which exists
         * since Android 10. Without it there is no honest way to tell a blocked app apart
         * from an allowed one, so the engine refuses to start instead of guessing.
         */
        const val MIN_FIREWALL_SDK = 29

        /** DNS id and mode waiting for the user to accept the VPN consent dialog. */
        @Volatile
        var pendingDnsId: String = ""
            private set

        @Volatile
        private var pendingFirewall = false

        /**
         * Wired to MainActivity's ActivityResultLauncher.
         *
         * The system consent activity has to be started *for a result*. Sent with
         * FLAG_ACTIVITY_NEW_TASK (plain startActivity) it carries no calling package to
         * validate against, so ConfirmDialog finishes in onCreate() before it ever draws:
         * logcat shows the activity starting, no window appears, no result comes back and
         * "Apply" silently does nothing.
         */
        @Volatile
        var consentLauncher: ((Intent) -> Unit)? = null

        fun applyDns(context: Context, dnsId: String) =
            launch(context, dnsId, firewall = false)

        /** Starts FIREWALL mode: full tunnel + per-app rules (no-op below Android 10). */
        fun startFirewall(context: Context, dnsId: String) {
            if (Build.VERSION.SDK_INT < MIN_FIREWALL_SDK) {
                Repo.log("evt_fw_unsupported")
                return
            }
            launch(context, dnsId, firewall = true)
        }

        /** Leaves firewall mode and falls back to plain DNS mode when one is selected. */
        fun stopFirewall(context: Context, dnsId: String) {
            if (dnsId.isNotEmpty()) applyDns(context, dnsId) else stop(context)
        }

        private fun launch(context: Context, dnsId: String, firewall: Boolean) {
            val consent = prepare(context)
            if (consent != null) {
                pendingDnsId = dnsId
                pendingFirewall = firewall
                runCatching {
                    val launcher = consentLauncher
                    if (launcher != null) {
                        launcher(consent)
                    } else {
                        // Only non-activity contexts may fall back to their own task.
                        consent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        context.startActivity(consent)
                    }
                }
                return
            }
            pendingDnsId = ""
            pendingFirewall = false
            start(
                context, Intent(context, NetPilotVpnService::class.java)
                    .setAction(if (firewall) ACTION_FIREWALL else ACTION_APPLY)
                    .putExtra(EXTRA_DNS_ID, dnsId)
            )
        }

        fun restoreSystemDns(context: Context) {
            pendingDnsId = ""
            pendingFirewall = false
            start(context, Intent(context, NetPilotVpnService::class.java).setAction(ACTION_RESTORE))
        }

        fun stop(context: Context) {
            pendingDnsId = ""
            pendingFirewall = false
            start(context, Intent(context, NetPilotVpnService::class.java).setAction(ACTION_STOP))
        }

        fun flushCache(context: Context) {
            DnsForwarder.clearCache()
            start(context, Intent(context, NetPilotVpnService::class.java).setAction(ACTION_FLUSH))
        }

        /**
         * Replays an apply that was queued behind the system consent dialog.
         *
         * What the dialog came back with is read straight from prepare(): granted means the
         * queued apply can finally run, anything else means the user declined. Declining used
         * to go through launch() again, which fired the very dialog that had just been
         * dismissed — an endless consent loop with no way out but accepting.
         */
        fun resumePending(context: Context) {
            val id = pendingDnsId
            if (id.isEmpty()) return
            if (prepare(context) != null) {
                pendingDnsId = ""
                pendingFirewall = false
                return
            }
            pendingDnsId = ""
            val firewall = pendingFirewall
            pendingFirewall = false
            start(
                context, Intent(context, NetPilotVpnService::class.java)
                    .setAction(if (firewall) ACTION_FIREWALL else ACTION_APPLY)
                    .putExtra(EXTRA_DNS_ID, id)
            )
        }

        private fun start(context: Context, intent: Intent) {
            runCatching {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(intent)
                } else {
                    context.startService(intent)
                }
            }
        }
    }

    private var tun: ParcelFileDescriptor? = null
    private var reader: Thread? = null

    /** TUN output shared with the relay's emit callback (set before the read loop starts). */
    @Volatile
    private var tunOut: FileOutputStream? = null

    @Volatile
    private var firewallMode = false

    /** Present only in FIREWALL mode; owns every non-DNS flow. */
    private var relay: Relay? = null

    @Volatile
    private var running = false

    /** Bumped for every reader thread; a thread that is no longer the current one is stale. */
    private val readerGen = java.util.concurrent.atomic.AtomicInteger(0)

    private val writeLock = Any()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /** Bounds concurrent forwards so a burst cannot exhaust file descriptors. */
    private val gate = Semaphore(4)
    private val pool = Executors.newFixedThreadPool(4) { r ->
        Thread(r, "np-dns-fwd").apply { isDaemon = true }
    }
    private val tcpSessions = ConcurrentHashMap<String, TcpSession>()
    private var ipId = Random.nextInt(0x10000)

    // ------------------------------------------------------------------ lifecycle

    override fun onCreate() {
        super.onCreate()
        // A failed protect() is not a small thing and it must not be swallowed.
        //
        // protect() excludes a socket from the tunnel. If it fails, the socket stays inside the
        // tunnel, so the desktop's proxy traffic is routed back through the phone's own tunnel -
        // the exact loop that made the PC appear connected and carry no data, with nothing at
        // all to explain it. Every one of these hooks used to be a bare runCatching whose
        // failure vanished, on the one code path where a silent failure is a mystery symptom.
        DnsForwarder.onUdpSocket = { s -> protectOrLog("dns udp", { protect(s) }) }
        DnsForwarder.onTcpSocket = { s -> protectOrLog("dns tcp", { protect(s) }) }
        // The LAN proxy the desktop dials has to reach the internet through this tunnel,
        // not around it. Without protect() its sockets are excluded along with the rest of
        // the package, and the PC ends up on the phone's real IP instead of the VPN's.
        com.netpilot.mobile.pc.ProxyServer.protectSocket =
            { s -> protectOrLog("proxy tcp", { protect(s) }) }
        com.netpilot.mobile.pc.ProxyServer.protectDatagram =
            { d -> protectOrLog("proxy udp", { protect(d) }) }
    }

    /** protect(), with the failure recorded. Returns false rather than throwing, so the callers
     *  keep the "was this socket excluded?" contract they already had. */
    private fun protectOrLog(what: String, block: () -> Boolean): Boolean =
        try {
            block()
        } catch (t: Throwable) {
            NpLog.warn(
                "vpn",
                "protect() failed for $what, so this socket is still inside the tunnel and its " +
                    "traffic will loop back through it - the desktop will look connected and " +
                    "carry nothing"
            )
            NpLog.warn("vpn", "protect() failed for $what", t)
            false
        }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Every entry point here is started with startForegroundService(), and Android 12+
        // turns a missing startForeground() into ForegroundServiceDidNotStartInTimeException
        // — a crash. Two paths never reach the tunnel (a cache flush with nothing running,
        // and "another VPN already owns the slot"), so the notification goes up first and
        // is taken down again by whoever ends the session.
        startAsForeground(VpnState.dnsName.value.takeIf { it.isNotEmpty() })

        when (intent?.action) {
            ACTION_APPLY ->
                startTunnel(intent.getStringExtra(EXTRA_DNS_ID) ?: "", firewall = false)
            ACTION_FIREWALL ->
                startTunnel(intent.getStringExtra(EXTRA_DNS_ID) ?: "", firewall = true)
            ACTION_RESTORE, ACTION_STOP -> {
                Repo.log("evt_dns_restored")
                stopTunnel()
            }
            ACTION_FLUSH -> {
                DnsForwarder.clearCache()
                if (!running) endSession()
            }
            else -> endSession()
        }
        return START_NOT_STICKY
    }

    /** Stops the service and removes the notification we were required to post first. */
    private fun endSession() {
        runCatching { stopForeground(STOP_FOREGROUND_REMOVE) }
        stopSelf()
    }

    private fun startTunnel(dnsId: String, firewall: Boolean) {
        val entry = AppGraph.dnsCatalog.find(dnsId)
            ?: AppGraph.dnsCatalog.presets.firstOrNull()
        if (entry == null) {
            VpnState.onError("no resolver")
            endSession()
            return
        }

        // Restart cleanly if a tunnel is already up.
        if (running) stopTunnelInternal(keepForeground = true)

        // Remember what the device was resolving through *before* we take over, so a resolver
        // that turns out to be unreachable cannot leave the phone with no name resolution at
        // all. See DnsForwarder.fallbackServers.
        DnsForwarder.fallbackServers = systemResolvers()

        startAsForeground(if (firewall) "NetPilot · ${entry.name}" else entry.name)

        try {
            val builder = Builder()
                .setSession("NetPilot")
                .setMtu(TUN_MTU)
                .addAddress(LOCAL_ADDR, 32)
                .addDnsServer(FAKE_DNS)
                .setBlocking(true)

            if (firewall) {
                // Full tunnel: everything the rules talk about has to reach us. IPv6 is
                // routed in too and dropped, otherwise a blocked app could simply walk
                // around the rule on a v6-capable network.
                builder.addRoute("0.0.0.0", 0)
                builder.addRoute("::", 0)
            } else {
                builder.addRoute(FAKE_DNS, 32)
            }

            // Our own sockets never enter our own tunnel — that is the anti-loop rule, and
            // it is also what lets the relay's upstream connections reach the internet.
            runCatching { builder.addDisallowedApplication(packageName) }

            val fd = builder.establish()
            if (fd == null) {
                VpnState.onError("establish() returned null (another VPN is active?)")
                stopTunnel()
                return
            }
            tun = fd
            firewallMode = firewall
            running = true

            // Re-armed on every tunnel, not only in onCreate(): the service outlives a single
            // tunnel, so a hook installed once would be missing after the first stop/start.
            // With no tunnel up, protect() simply returns false and the socket leaves the
            // normal way - the desktop keeps working, it just stops riding the VPN.
            com.netpilot.mobile.pc.ProxyServer.protectSocket =
                { s -> runCatching { protect(s) }.getOrDefault(false) }
            com.netpilot.mobile.pc.ProxyServer.protectDatagram =
                { d -> runCatching { protect(d) }.getOrDefault(false) }

            DnsForwarder.clearCache()
            AppGraph.dnsCatalog.markSelected(entry.id)
            VpnState.onTunnelUp(
                if (firewall) VpnState.Mode.FIREWALL else VpnState.Mode.DNS,
                entry.id, entry.name
            )
            Repo.log(if (firewall) "evt_fw_start" else "evt_dns_changed", entry.name)
            QuickDns.update(this, entry.name)
            MonitorTelemetry.announce()

            if (firewall) startRelay()
            if (firewall) startRelayWatchdog()

            reader = Thread({ readLoop(fd) }, "np-tun").apply {
                isDaemon = true
                start()
            }
        } catch (t: Throwable) {
            VpnState.onError(t.message ?: "vpn error")
            stopTunnel()
        }
    }

    /** Builds the forwarding engine that carries every non-DNS packet in firewall mode. */
    private fun startRelay() {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
        val pm = packageManager

        val packages = HashMap<Int, String>()
        runCatching {
            @Suppress("DEPRECATION")
            for (pi in pm.getInstalledPackages(0)) {
                val uid = pi.applicationInfo?.uid ?: continue
                packages.putIfAbsent(uid, pi.packageName)
            }
        }

        val firewall = Firewall(
            ruleSource = { Repo.effectiveRules() },
            ownerUid = { proto, srcIp, srcPort, dstIp, dstPort ->
                if (Build.VERSION.SDK_INT >= MIN_FIREWALL_SDK && cm != null) {
                    runCatching {
                        cm.getConnectionOwnerUid(
                            proto,
                            InetSocketAddress(inetAddress(srcIp), srcPort),
                            InetSocketAddress(inetAddress(dstIp), dstPort)
                        )
                    }.getOrDefault(Firewall.INVALID_UID)
                } else Firewall.INVALID_UID
            },
            pkgOf = { uid ->
                // The map above is a snapshot from service start: a package installed (or
                // re-enabled) after that would resolve to null and then be shaped as
                // unlimited, so fall back to asking the platform for the live mapping.
                packages[uid]
                    ?: runCatching { pm.getPackagesForUid(uid)?.firstOrNull() }.getOrNull()
            }
        )

        val r = Relay(
            emit = { packet -> tunOut?.let { write(it, packet) } },
            // Logged, not swallowed: see the note on the other protect hooks. These two are
            // the relay's own sockets, so a failure here stalls forwarding itself.
            protectSocket = { socket -> protectOrLog("relay tcp", { protect(socket) }) },
            protectDatagram = { socket -> protectOrLog("relay udp", { protect(socket) }) },
            firewall = firewall,
            onDnsUdp = { q, srcIp, srcPort, dstIp, dstPort ->
                onDnsUdp(q, srcIp, srcPort, dstIp, dstPort)
            },
            onDnsTcp = { p, ihl, srcIp, dstIp -> onDnsTcp(p, ihl, srcIp, dstIp) }
        )
        FirewallStats.reset()
        FirewallStats.active = true
        r.start()
        relay = r
        NpLog.info(
            "vpn",
            "forwarding engine started: ${packages.size} packages mapped, firewall " +
                (if (Build.VERSION.SDK_INT >= MIN_FIREWALL_SDK) "available" else "unavailable on this Android version")
        )
    }

    /**
     * Fails open if the forwarding engine ever stops.
     *
     * The reader keeps draining the TUN, so a dead relay does not crash the service - it just
     * hands every packet to nobody. The routes still point at us, which is a silent black
     * hole: apps hang and the phone looks offline while the VPN icon says everything is fine.
     * Watching the worker thread turns that into an honest "the tunnel stopped", and the
     * system immediately falls back to the real network.
     *
     * The watch is bound to the engine it was started for: applying a DNS or a profile
     * replaces the relay, and the closing of the previous one must not be mistaken for the
     * failure of its successor.
     */
    private fun startRelayWatchdog() {
        val mine = relay ?: return
        scope.launch {
            while (running && isActive) {
                delay(2_000)
                if (!running) break
                if (relay !== mine) return@launch     // a newer engine took over
                if (!mine.isAlive()) {
                    VpnState.onError("relay stopped unexpectedly")
                    Repo.log("evt_relay_lost")
                    stopTunnel()
                    return@launch
                }
            }
        }
    }

    /**
     * Resolvers of the real (non-VPN) networks, captured while they are still visible.
     *
     * `ConnectivityManager.getAllNetworks` is the current API and is not deprecated; `allNetworks`
     * was its Kotlin property form. The underlying concern the review raised - a deprecation
     * without a version guard - does not apply here, because nothing about this call is version
     * dependent. It is called from a service that only exists on API 21+.
     *
     * What *is* worth guarding is the fact that this runs immediately after the VPN interface is
     * established. At that moment the new tunnel is in the list, which is why the TRANSPORT_VPN
     * filter is not optional: without it the phone would read back its own fake resolver as a
     * system one and forward every query to itself.
     *
     * The runCatching is not defensive noise either - `getNetworkCapabilities` returns null for a
     * network that disappeared between listing and querying, which happens routinely during a
     * Wi-Fi roam, and that is a skip rather than an error.
     */
    private fun systemResolvers(): List<String> {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return emptyList()
        val out = LinkedHashSet<String>()
        runCatching {
            for (n in cm.allNetworks) {
                val caps = cm.getNetworkCapabilities(n) ?: continue
                if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue
                cm.getLinkProperties(n)?.dnsServers?.forEach { out.add(it.hostAddress) }
            }
        }
        // Belt and braces: the filter above is the real check, but the address is also removed by
        // value. If a future Android adds a VPN transport that TRANSPORT_VPN does not cover, this
        // is what stops the tunnel from resolving through itself - a failure that looks like "DNS
        // is broken" rather than like a loop.
        out.remove(FAKE_DNS)
        out.remove("127.0.0.53")          // the emulator's stub resolver
        return out.toList()
    }

    private fun inetAddress(ip: Int): InetAddress = InetAddress.getByAddress(        byteArrayOf(
            (ip ushr 24).toByte(), (ip ushr 16).toByte(), (ip ushr 8).toByte(), ip.toByte()
        )
    )

    private fun stopTunnel() {
        stopTunnelInternal(keepForeground = false)
        if (!running) stopSelf()
    }

    private fun stopTunnelInternal(keepForeground: Boolean) {
        running = false
        firewallMode = false
        relay?.let { runCatching { it.close() } }
        relay = null
        reader?.interrupt()
        reader = null
        // Retire the reader that is being torn down: it is allowed to notice its own exit,
        // but not to act on it - by the time it wakes up a replacement tunnel may already be
        // running, and killing that one would be a fail-open aimed at the wrong process.
        readerGen.incrementAndGet()
        tcpSessions.clear()
        tunOut = null
        runCatching { tun?.close() }
        tun = null
        DnsForwarder.fallbackServers = emptyList()
        val wasActive = VpnState.active
        VpnState.onTunnelDown()
        FirewallStats.reset()
        QuickDns.clear()
        if (wasActive) MonitorTelemetry.announce()
        if (!keepForeground) {
            runCatching { stopForeground(STOP_FOREGROUND_REMOVE) }
        }
    }

    override fun onDestroy() {
        running = false
        relay?.let { runCatching { it.close() } }
        relay = null
        // The reader has to be interrupted here as well as in stopTunnelInternal. Android can
        // destroy a started service without the tunnel ever having been stopped, and the reader
        // blocks on a read of the TUN descriptor - so without this it stays parked on that read
        // for the life of the process, holding the old generation's state.
        reader?.interrupt()
        reader = null
        readerGen.incrementAndGet()
        tcpSessions.clear()

        runCatching { tun?.close() }
        tun = null
        tunOut = null
        // The pool and the scope belong to this service instance, and a service instance is
        // created fresh each time the app starts one. Their workers hold the executor alive, so
        // without a shutdown a recreated service stacked another set on top - and four threads
        // per foreground session is not a cost worth paying repeatedly on a phone.
        runCatching { pool.shutdownNow() }
        scope.cancel()
        VpnState.onTunnelDown()
        FirewallStats.reset()
        QuickDns.clear()
        super.onDestroy()
    }

    // ------------------------------------------------------------------ notification

    /**
     * Publishes the ongoing notification, which Android requires within seconds of the start.
     *
     * The foreground service *type* matters more than it looks. This service holds a real VPN
     * tunnel, and Android 14 requires the type passed here to be one the manifest declares for
     * this service, or the call throws `ForegroundServiceStartNotAllowedException` and the
     * process dies.
     *
     * It was declared as `specialUse`, which is the "I have some other reason" bucket and is
     * meant for things like a file transfer or a device-ownership app. A VPN has its own type -
     * `systemExempted` - granted because the user explicitly consented to a VPN via the system
     * dialog. Claiming `specialUse` here was a declaration the platform does not grant a VPN, and
     * on some OEM builds the mismatched type is refused outright.
     *
     * The fallback below exists because the pre-34 overload has no type parameter at all, so it
     * is the only call available on API 26..33 and is the correct one there.
     */
    private fun startAsForeground(dnsName: String?) {
        val notif = QuickDns.build(this, dnsName)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            try {
                startForeground(
                    NOTIF_ID, notif,
                    if (Build.VERSION.SDK_INT >= 35) {
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED
                    } else {
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
                    }
                )
                return
            } catch (t: Throwable) {
                // Not fatal on its own: the untyped overload below is enough to keep the process
                // alive, and the notification still appears. Logged because a silent fallback here
                // is how a service ends up running with the wrong type and being killed later.
                NpLog.warn(
                    "vpn",
                    "the typed foreground start was refused, falling back to an untyped one",
                    t
                )
            }
        }

        runCatching { startForeground(NOTIF_ID, notif) }
            .onFailure {
                // Nothing left to try. A VPN service that cannot show its notification cannot
                // legally stay in the foreground, so the tunnel has to stop rather than run
                // invisibly - and that must be visible in the UI, not just in the log.
                NpLog.error(
                    "vpn",
                    "could not enter the foreground at all, so the tunnel cannot run: " +
                        "Android requires a visible ongoing notification for a foreground service",
                    it
                )
                stopSelf()
            }
    }

    // ------------------------------------------------------------------ packet loop

    private fun readLoop(fd: ParcelFileDescriptor) {
        val input = FileInputStream(fd.fileDescriptor)
        val output = FileOutputStream(fd.fileDescriptor)
        // A restart (applying a DNS, a profile, a schedule) stops the old reader and starts a
        // new one. The old thread is still winding down when that happens, and it would find
        // `running == true` again and blame the fresh tunnel for its own exit. The generation
        // tells the two apart.
        val gen = readerGen.incrementAndGet()
        tunOut = output

        // Every DNS query is handed to a small pool: the reader keeps draining the TUN so a slow
        // resolver can never stall unrelated lookups.
        //
        // The whole loop is in a try/finally because both streams wrap the TUN file descriptor,
        // and this function had no finally: any path out of it that was not the explicit break -
        // an unexpected throw, an Error from a bad allocation, a future edit adding an early
        // return - leaked two stream objects and their descriptors. A leak here is invisible until
        // the process runs out, and a VPN service that restarts on every settings change is
        // exactly the shape of thing that reaches that point.
        try {
            val buf = ByteArray(TUN_MTU)
            var sinceSweep = 0

            while (running && !Thread.currentThread().isInterrupted) {
                val n = try {
                    input.read(buf)
                } catch (t: Throwable) {
                    break
                }
                if (n <= 0) break
                val packet = buf.copyOf(n)
                try {
                    handlePacket(packet, output)
                } catch (t: Throwable) {
                    // Never let a malformed packet kill the tunnel.
                }
                if (++sinceSweep >= SWEEP_EVERY_PACKETS) {
                    sinceSweep = 0
                    sweepTcpSessions(System.currentTimeMillis())
                }
            }
        } catch (t: Throwable) {
            NpLog.error("vpn", "the tunnel reader stopped on an unexpected error", t)
        } finally {
            // Both handles, unconditionally. The output stream is the one the relay writes
            // through, so leaving it open would keep the descriptor alive after the tunnel is
            // gone and let a write land on a recycled descriptor.
            runCatching { input.close() }
            runCatching { output.close() }

            // Only a reader that is still the current one may clear the shared sink: a retiring
            // thread used to null the field after its successor had already published its stream,
            // which silently dropped every packet the relay tried to write.
            if (readerGen.get() == gen) tunOut = null

            // The reader stopped on its own. While the interface is still established the system
            // keeps routing every packet to it, and with nothing reading the queue that is a
            // silent black hole - the phone would look offline until the user noticed. Fail open
            // instead: drop the tunnel and the real network takes over.
            if (readerGen.get() == gen && running) {
                VpnState.onError("tunnel stopped unexpectedly")
                Repo.log("evt_tun_lost")
                stopTunnel()
            }
        }
    }

    private fun handlePacket(p: ByteArray, out: FileOutputStream) {
        if (p.size < 28) return
        val versionIhl = p[0].toInt() and 0xFF
        if (versionIhl shr 4 != 4) {
            // Non-IPv4 (i.e. the IPv6 route we pulled into the tunnel on purpose).
            if (firewallMode) FirewallStats.ipv6Dropped++
            return
        }
        val ihl = (versionIhl and 0x0F) * 4
        if (p.size < ihl + 8) return
        val protocol = p[9].toInt() and 0xFF

        val dstIp = intOf(p, 16)
        val srcIp = intOf(p, 12)

        if (firewallMode) {
            // Everything except DNS is carried by the relay, which applies the rules.
            relay?.onPacket(p, ihl, protocol, srcIp, dstIp)
            return
        }

        when (protocol) {
            17 -> {
                // DNS mode: nothing but port 53 is ever routed into the tunnel.
                val h = Packet.parseUdp(p, ihl) ?: return
                if (h.dstPort != 53) return
                onDnsUdp(
                    p.copyOfRange(h.payloadOff, h.payloadEnd),
                    srcIp, h.srcPort, dstIp, h.dstPort
                )
            }
            6 -> onDnsTcp(p, ihl, srcIp, dstIp)
        }
    }

    // ------------------------------------------------------------------ UDP

    /**
     * Answers one DNS query the tunnel intercepted. In firewall mode the relay hands the
     * payload over here after it has decided the querying app is allowed to speak.
     */
    private fun onDnsUdp(
        query: ByteArray,
        srcIp: Int, srcPort: Int,
        dstIp: Int, dstPort: Int
    ) {
        if (query.size < 12) return

        val server = resolverAddress()
        val start = System.nanoTime()
        scope.launch {
            gate.withPermit {
                val (resp, cacheHit) = DnsForwarder.forward(query, server)
                val ms = ((System.nanoTime() - start) / 1_000_000L).toInt()
                if (resp != null) {
                    VpnState.onQuery(ms, ok = true, cacheHit = cacheHit)
                    val packet = Packet.buildUdp(
                        payload = resp,
                        srcIp = dstIp, srcPort = dstPort,
                        dstIp = srcIp, dstPort = srcPort,
                        ipId = nextIpId()
                    )
                    tunOut?.let { write(it, packet) }
                } else {
                    VpnState.onQuery(-1, ok = false)
                }
            }
        }
    }

    private fun nextIpId(): Int = synchronized(this) {
        ipId = (ipId + 1) and 0xFFFF
        ipId
    }

    // ------------------------------------------------------------------ TCP (DNS over TCP)

    /**
     * Minimal DNS-over-TCP server. In firewall mode the relay routes port 53 here after it
     * has approved the querying application; in DNS mode the packet arrives directly.
     */
    private fun onDnsTcp(p: ByteArray, ihl: Int, srcIp: Int, dstIp: Int) {
        if (p.size < ihl + 20) return
        val srcPort = portOf(p, ihl)
        val dstPort = portOf(p, ihl + 2)
        if (dstPort != 53) return
        val out = tunOut ?: return

        val key = "$srcIp:$srcPort"
        val seq = uint32(p, ihl + 4)
        val ack = uint32(p, ihl + 8)
        val dataOffset = ((p[ihl + 12].toInt() and 0xFF) shr 4) * 4
        val flags = p[ihl + 13].toInt() and 0xFF
        val payload = if (dataOffset in 20..(p.size - ihl)) {
            p.copyOfRange(ihl + dataOffset, p.size)
        } else ByteArray(0)

        val syn = flags and 0x02 != 0
        val rst = flags and 0x04 != 0
        val fin = flags and 0x01 != 0
        val ackFlag = flags and 0x10 != 0
        val psh = flags and 0x08 != 0

        if (rst) {
            tcpSessions.remove(key)
            return
        }

        val session = tcpSessions.getOrPut(key) {
            if (!syn) {
                // Stray segment for an unknown connection: refuse it.
                sendTcp(out, srcIp, srcPort, dstIp, dstPort, ack, seq + 1, 0x14, ByteArray(0))
                return
            }
            TcpSession(
                clientIp = srcIp, clientPort = srcPort,
                serverIp = dstIp, serverPort = dstPort,
                ourSeq = Random.nextLong(0, 0x7FFFFFFFL),
                clientSeqNext = seq + 1,
                aliveAt = System.currentTimeMillis()
            ).also {
                sendTcp(
                    out, srcIp, srcPort, dstIp, dstPort,
                    seq = it.ourSeq, ack = it.clientSeqNext,
                    flags = 0x12, data = ByteArray(0)   // SYN | ACK
                )
                it.ourSeq = (it.ourSeq + 1) and 0xFFFFFFFFL
            }
        }

        session.aliveAt = System.currentTimeMillis()

        if (syn && ackFlag) return                     // already handled above
        if (syn) return

        // Duplicate / out-of-order data → just re-acknowledge what we already have.
        val payloadSeq = seq
        if (payload.isNotEmpty() && payloadSeq != session.clientSeqNext) {
            if (payloadSeq < session.clientSeqNext) {
                sendTcp(
                    out, srcIp, srcPort, dstIp, dstPort,
                    seq = session.ourSeq, ack = session.clientSeqNext,
                    flags = 0x10, data = ByteArray(0)
                )
            }
            return
        }

        if (payload.isNotEmpty()) {
            session.clientSeqNext = (session.clientSeqNext + payload.size) and 0xFFFFFFFFL
            session.input.write(payload)
            // Immediate ACK so the client stops retransmitting while we forward.
            sendTcp(
                out, srcIp, srcPort, dstIp, dstPort,
                seq = session.ourSeq, ack = session.clientSeqNext,
                flags = 0x10, data = ByteArray(0)
            )
            // Drain every complete message, not just the first.
            //
            // DNS over TCP is allowed to pipeline: a client may put two queries in one segment,
            // and the length-prefixed framing exists precisely so a reader can tell where one
            // ends and the next begins. Reading one message and returning left the rest sitting
            // in the buffer until another segment happened to arrive - and if none did, they
            // were never answered at all. The client waited on a resolver that had already
            // ACKed the bytes, which is the worst shape of bug to diagnose: nothing failed, one
            // query simply never came back.
            //
            // This is the bug the dead `ackFlag || psh` expression was sitting on top of.
            while (true) {
                val message = session.takeMessage() ?: break
                val server = resolverAddress()
                pool.execute {
                    val (resp, _) = DnsForwarder.forwardTcp(message, server)
                    if (resp != null) {
                        val framed = frame(resp)
                        synchronized(writeLock) {
                            sendTcp(
                                out, srcIp, srcPort, dstIp, dstPort,
                                seq = session.ourSeq, ack = session.clientSeqNext,
                                flags = 0x18, data = framed   // PSH | ACK
                            )
                            session.ourSeq = (session.ourSeq + framed.size) and 0xFFFFFFFFL
                        }
                        VpnState.onQuery(0, ok = true)
                    } else {
                        VpnState.onQuery(-1, ok = false)
                    }
                }
            }
        }

        if (fin) {
            session.clientSeqNext = (session.clientSeqNext + 1) and 0xFFFFFFFFL
            sendTcp(
                out, srcIp, srcPort, dstIp, dstPort,
                seq = session.ourSeq, ack = session.clientSeqNext,
                flags = 0x11, data = ByteArray(0)      // FIN | ACK
            )
            session.ourSeq = (session.ourSeq + 1) and 0xFFFFFFFFL
            session.closing = true
            if (payload.isEmpty() && !ackFlag) return
            tcpSessions.remove(key)
        }
    }

    /**
     * Drops TCP sessions that were never closed.
     *
     * A client that dies mid-query (a killed resolver, a laptop that went to sleep) never
     * sends FIN or RST, so its session - and the request bytes buffered in it - stayed in the
     * map for as long as the tunnel was up. A long-running tunnel therefore grew without
     * bound. The reader loop calls this on the same cadence as everything else it sweeps.
     */
    private fun sweepTcpSessions(now: Long) {
        if (tcpSessions.isEmpty()) return
        val stale = tcpSessions.entries.filter {
            val s = it.value
            (s.closing && now - s.aliveAt > TCP_SESSION_CLOSE_MS) ||
                now - s.aliveAt > TCP_SESSION_IDLE_MS
        }
        stale.forEach { tcpSessions.remove(it.key, it.value) }
    }

    private class TcpSession(
        val clientIp: Int,
        val clientPort: Int,
        val serverIp: Int,
        val serverPort: Int,
        var ourSeq: Long,
        var clientSeqNext: Long,
        var aliveAt: Long
    ) {
        val input = ByteArrayOutputStream()
        var closing = false

        /** Returns a complete length-prefixed DNS message when enough bytes arrived. */
        fun takeMessage(): ByteArray? {
            val bytes = input.toByteArray()
            if (bytes.size < 2) return null
            val len = ((bytes[0].toInt() and 0xFF) shl 8) or (bytes[1].toInt() and 0xFF)
            if (bytes.size < len + 2) return null
            val msg = bytes.copyOfRange(2, len + 2)
            input.reset()
            if (bytes.size > len + 2) {
                input.write(bytes, len + 2, bytes.size - len - 2)
            }
            return msg
        }
    }

    private fun frame(msg: ByteArray): ByteArray {
        val out = ByteArray(2 + msg.size)
        out[0] = ((msg.size shr 8) and 0xFF).toByte()
        out[1] = (msg.size and 0xFF).toByte()
        System.arraycopy(msg, 0, out, 2, msg.size)
        return out
    }

    private fun sendTcp(
        out: FileOutputStream,
        toIp: Int, toPort: Int,
        fromIp: Int, fromPort: Int,
        seq: Long, ack: Long,
        flags: Int, data: ByteArray
    ) {
        val total = 20 + 20 + data.size
        val p = ByteArray(total)
        p[0] = 0x45
        p[2] = ((total shr 8) and 0xFF).toByte()
        p[3] = (total and 0xFF).toByte()
        val id = synchronized(this) { ipId = (ipId + 1) and 0xFFFF; ipId }
        p[4] = ((id shr 8) and 0xFF).toByte()
        p[5] = (id and 0xFF).toByte()
        p[6] = 0x40.toByte()
        p[8] = 64
        p[9] = 6
        putInt(p, 12, fromIp)
        putInt(p, 16, toIp)
        putShort(p, 10, ipChecksum(p, 0, 20))

        putShort(p, 20, fromPort)
        putShort(p, 22, toPort)
        putInt32(p, 24, seq)
        putInt32(p, 28, ack)
        p[32] = 0x50                     // data offset = 5 words
        p[33] = (flags and 0xFF).toByte()
        putShort(p, 34, 0xFFFF)          // window
        putShort(p, 36, 0)               // checksum (filled below)
        if (data.isNotEmpty()) {
            System.arraycopy(data, 0, p, 40, data.size)
        }
        putShort(p, 36, tcpChecksum(p, 20, 20 + data.size, fromIp, toIp))
        write(out, p)
    }

    // ------------------------------------------------------------------ helpers

    private fun resolverAddress(): String {
        val id = VpnState.dnsId.value
        val entry = AppGraph.dnsCatalog.find(id)
            ?: AppGraph.dnsCatalog.presets.firstOrNull()
        return entry?.primary ?: "1.1.1.1"
    }

    private fun write(out: FileOutputStream, packet: ByteArray) {
        synchronized(writeLock) {
            try {
                out.write(packet)
                out.flush()
            } catch (t: Throwable) {
                // TUN closed while a reply was in flight — ignore.
            }
        }
    }

    private fun intOf(p: ByteArray, off: Int): Int =
        ((p[off].toInt() and 0xFF) shl 24) or
                ((p[off + 1].toInt() and 0xFF) shl 16) or
                ((p[off + 2].toInt() and 0xFF) shl 8) or
                (p[off + 3].toInt() and 0xFF)

    private fun portOf(p: ByteArray, off: Int): Int =
        ((p[off].toInt() and 0xFF) shl 8) or (p[off + 1].toInt() and 0xFF)

    private fun uint32(p: ByteArray, off: Int): Long =
        ((p[off].toLong() and 0xFF) shl 24) or
                ((p[off + 1].toLong() and 0xFF) shl 16) or
                ((p[off + 2].toLong() and 0xFF) shl 8) or
                (p[off + 3].toLong() and 0xFF)

    private fun putInt(p: ByteArray, off: Int, v: Int) {
        p[off] = ((v ushr 24) and 0xFF).toByte()
        p[off + 1] = ((v ushr 16) and 0xFF).toByte()
        p[off + 2] = ((v ushr 8) and 0xFF).toByte()
        p[off + 3] = (v and 0xFF).toByte()
    }

    private fun putInt32(p: ByteArray, off: Int, v: Long) {
        p[off] = ((v ushr 24) and 0xFF).toByte()
        p[off + 1] = ((v ushr 16) and 0xFF).toByte()
        p[off + 2] = ((v ushr 8) and 0xFF).toByte()
        p[off + 3] = (v and 0xFF).toByte()
    }

    private fun putShort(p: ByteArray, off: Int, v: Int) {
        p[off] = ((v ushr 8) and 0xFF).toByte()
        p[off + 1] = (v and 0xFF).toByte()
    }

    private fun ipChecksum(data: ByteArray, off: Int, len: Int): Int {
        var sum = 0L
        var i = off
        val end = off + len
        while (i + 1 < end) {
            sum += ((data[i].toInt() and 0xFF) shl 8) or (data[i + 1].toInt() and 0xFF)
            i += 2
        }
        if (i < end) sum += (data[i].toInt() and 0xFF) shl 8
        while (sum ushr 16 != 0L) sum = (sum and 0xFFFF) + (sum ushr 16)
        return sum.inv().toInt() and 0xFFFF
    }

    private fun tcpChecksum(p: ByteArray, tcpOff: Int, tcpLen: Int, srcIp: Int, dstIp: Int): Int {
        var sum = 0L
        // pseudo header
        sum += (srcIp ushr 16) and 0xFFFF
        sum += srcIp and 0xFFFF
        sum += (dstIp ushr 16) and 0xFFFF
        sum += dstIp and 0xFFFF
        sum += 6
        sum += tcpLen
        var i = tcpOff
        val end = tcpOff + tcpLen
        while (i + 1 < end) {
            sum += ((p[i].toInt() and 0xFF) shl 8) or (p[i + 1].toInt() and 0xFF)
            i += 2
        }
        if (i < end) sum += (p[i].toInt() and 0xFF) shl 8
        while (sum ushr 16 != 0L) sum = (sum and 0xFFFF) + (sum ushr 16)
        val c = sum.inv().toInt() and 0xFFFF
        return if (c == 0) 0xFFFF else c
    }
}

/** Lets the service nudge the monitor after the tunnel goes up/down. */
private object MonitorTelemetry {
    fun announce() {
        com.netpilot.mobile.net.Monitor.refreshLink()
    }
}
