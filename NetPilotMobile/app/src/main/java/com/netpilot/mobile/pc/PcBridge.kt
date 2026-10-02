package com.netpilot.mobile.pc

import com.netpilot.mobile.core.AppGraph
import com.netpilot.mobile.data.Store
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL

/**
 * Client half of the "Mobile VPN → PC" bridge.
 *
 * Speaks the exact protocol implemented by the Windows side
 * (`NetPilot/Services/MobileVpnApi.cs`, API v1, TCP port 8787):
 *
 *   GET    /api/v1/ping       discovery, no auth
 *   POST   /api/v1/pair       {"code": "..."} → {"token": "..."}
 *   GET    /api/v1/status     Bearer <token>
 *   POST   /api/v1/hello      {"name","model","android"}
 *   POST   /api/v1/report     {"vpnActive": bool, "proxy": "host:port"}
 *   POST   /api/v1/backup | /restore | /share | /stop
 *   DELETE /api/v1/pair
 *
 * Every call runs on [Dispatchers.IO] with a hard timeout, so a dead PC can never
 * freeze the UI.
 */
object PcBridge {

    const val DEFAULT_PORT = 8787

    data class PcState(
        val host: String = "",
        val port: Int = DEFAULT_PORT,
        val token: String = "",
        val paired: Boolean = false,
        val deviceName: String = "",
        val reachable: Boolean = false,
        val pcVpnActive: Boolean = false,
        val pcConnected: Boolean = false,
        val sharing: Boolean = false,
        val mobileVpn: Boolean = false,
        val rateDown: Long = 0L,
        val rateUp: Long = 0L,
        val hasBackup: Boolean = false,
        val links: String = "",
        val lastError: String = "",
        val busy: Boolean = false,
        val lastReportAt: Long = 0L
    )

    private val _state = MutableStateFlow(load())
    val state: StateFlow<PcState> = _state.asStateFlow()

    private var reporter: kotlinx.coroutines.Job? = null

    /** Endpoint we last told the desktop to point WinHTTP at ("" = not sharing). */
    @Volatile
    private var sharedEndpoint: String = ""

    private fun load(): PcState {
        val s = AppGraph.store
        return PcState(
            host = s.getString(Store.D_PC, "host", ""),
            port = s.getInt(Store.D_PC, "port", DEFAULT_PORT).coerceIn(1, 65535),
            token = s.getString(Store.D_PC, "token", ""),
            deviceName = s.getString(Store.D_PC, "name", "")
        ).let { if (it.token.isNotBlank()) it.copy(paired = true) else it }
    }

    private fun save() {
        val s = _state.value
        AppGraph.store.edit(Store.D_PC) { d ->
            d.put("host", s.host)
            d.put("port", s.port)
            d.put("token", s.token)
            d.put("name", s.deviceName)
        }
    }

    private fun update(block: (PcState) -> PcState) {
        // The reporter loop and a UI action both call this; a plain read-modify-write let
        // them interleave and lose one of the two field sets. update{} retries the block.
        _state.update(block)
        save()
    }

    /** Stores the address as typed, canonicalised by [normalizeTarget] (see Target.kt). */
    fun setTarget(host: String, port: Int) = update {
        val (h, p) = normalizeTarget(host, port)
        it.copy(host = h, port = p)
    }

    // ------------------------------------------------------------------ calls

    /** 1 · discover the PC. Returns true when a NetPilot bridge answered. */
    suspend fun ping(): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        if (s.host.isBlank()) return@withContext false
        update { it.copy(busy = true, lastError = "") }
        var res = call(s.host, s.port, "GET", "/api/v1/ping", null, null)
        var alt = ""
        if (res.code == -1 && res.body.contains("UnknownHost")) {
            // The typed address is a name this phone cannot resolve (Windows advertises a
            // placeholder like http://pc:8787). Instead of dead-ending on an error the
            // user cannot act on, look for the bridge on this LAN — which is exactly what
            // that address was meant to point at.
            val found = discover()
            if (found != null) {
                res = call(found, s.port, "GET", "/api/v1/ping", null, null)
            } else {
                alt = _state.value.lastError
            }
        }
        val ok = res.code == 200 && res.body.contains("NetPilot")
        update {
            it.copy(
                reachable = ok,
                busy = false,
                lastError = if (ok) "" else alt.ifBlank { errorOf(res) }
            )
        }
        ok
    }

    /**
     * 1b · find the PC without anybody typing an address: scan this phone's own /24 for
     * TCP [DEFAULT_PORT], then confirm the answering host really is a NetPilot bridge.
     * Returns the host, or null when nothing on the network identified itself.
     */
    suspend fun discover(): String? = withContext(Dispatchers.IO) {
        update { it.copy(busy = true, lastError = "") }
        val port = _state.value.port
        val open = LanScan.probe(LanScan.candidates(), port)
        var found: String? = null
        for (host in open) {
            val res = call(host, port, "GET", "/api/v1/ping", null, null)
            if (res.code == 200 && res.body.contains("NetPilot")) {
                found = host
                break
            }
        }
        if (found != null) {
            setTarget(found, port)
            update { it.copy(busy = false, reachable = true, lastError = "") }
            com.netpilot.mobile.data.Repo.log("evt_pc_found", found)
        } else {
            update {
                it.copy(busy = false, reachable = false, lastError = notFoundText(open))
            }
        }
        found
    }

    private fun notFoundText(open: List<String>): String =
        // A port that answered but is not NetPilot is a different problem from silence.
        if (open.isEmpty()) com.netpilot.mobile.data.Strings.raw("mv_not_found")
        else com.netpilot.mobile.data.Strings.raw("mv_not_netpilot")

    /** 2 · exchange the on-screen code for a bearer token. */
    suspend fun pair(code: String): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        if (s.host.isBlank()) {
            update { it.copy(lastError = "host") }
            return@withContext false
        }
        update { it.copy(busy = true, lastError = "") }
        val body = JSONObject().put("code", code.trim()).toString()
        val res = call(s.host, s.port, "POST", "/api/v1/pair", body, null)
        val token = runCatching {
            if (res.code == 200) JSONObject(res.body).optString("token") else ""
        }.getOrDefault("")

        if (token.isBlank()) {
            update { it.copy(busy = false, paired = false, lastError = errorOf(res)) }
            false
        } else {
            update {
                it.copy(busy = false, paired = true, token = token, reachable = true, lastError = "")
            }
            hello()
            // The desktop only shows "phone linked" once something has been reported.
            report("")
            true
        }
    }

    /** Introduce the phone (name / model / Android version). Best effort. */
    suspend fun hello(): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        val body = JSONObject()
            .put("name", s.deviceName.ifBlank { deviceName() })
            .put("model", "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}")
            .put("android", "Android ${android.os.Build.VERSION.RELEASE}")
            .toString()
        val res = call(s.host, s.port, "POST", "/api/v1/hello", body, s.token)
        res.code == 200
    }

    /** Send the current phone state (VPN on/off + the proxy we advertise). */
    suspend fun report(proxy: String): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        if (!s.paired) return@withContext false
        val body = JSONObject()
            .put("vpnActive", com.netpilot.mobile.vpn.VpnState.active)
            .put("proxy", proxy)
            .put("ssid", com.netpilot.mobile.net.Monitor.link.value.typeName)
            .toString()
        val res = call(s.host, s.port, "POST", "/api/v1/report", body, s.token)
        val ok = res.code == 200
        if (ok) update { it.copy(lastReportAt = System.currentTimeMillis(), lastError = "") }
        ok
    }

    suspend fun status(): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        if (!s.paired) return@withContext false
        val res = call(s.host, s.port, "GET", "/api/v1/status", null, s.token)
        if (res.code != 200) {
            update { it.copy(reachable = false, lastError = errorOf(res)) }
            return@withContext false
        }
        runCatching {
            val o = JSONObject(res.body)
            val links = o.optJSONArray("links")
            val linkText = buildString {
                for (i in 0 until (links?.length() ?: 0)) {
                    val l = links?.optJSONObject(i) ?: continue
                    if (!l.optBoolean("up")) continue
                    if (isNotEmpty()) append(" · ")
                    append(l.optString("name"))
                    val ip = l.optString("ipv4")
                    if (ip.isNotBlank()) append(" ").append(ip)
                }
            }
            update {
                it.copy(
                    reachable = true,
                    pcVpnActive = o.optBoolean("pcVpnActive"),
                    pcConnected = o.optBoolean("pcConnected"),
                    sharing = o.optBoolean("sharing"),
                    mobileVpn = o.opt("mobileVpn") == true,
                    rateDown = o.optLong("rateDown"),
                    rateUp = o.optLong("rateUp"),
                    hasBackup = o.optBoolean("hasBackup"),
                    links = linkText,
                    lastError = ""
                )
            }
        }.onFailure { e ->
            update { it.copy(lastError = transport(e.javaClass.simpleName)) }
        }
        true
    }

    suspend fun backup(): Boolean = simpleAction("/api/v1/backup")
    suspend fun restore(): Boolean = simpleAction("/api/v1/restore")

    /** Ask the PC to put its metrics/proxy back — i.e. stop riding this phone. */
    suspend fun stopShare(): Boolean = withContext(Dispatchers.IO) {
        val ok = simpleAction("/api/v1/stop")
        if (ok) {
            sharedEndpoint = ""
            com.netpilot.mobile.data.Repo.log("evt_pc_stop")
        }
        ok
    }

    /**
     * Ask the PC to start sharing through this phone.
     *
     * This is the half the desktop cannot do on its own: it only stores the proxy address
     * a `/report` carries, so without this call WinHTTP is never pointed at the phone and
     * nothing happens on the PC. The address is re-sent in the body as well, which makes
     * the call self-contained even if a report was lost.
     */
    suspend fun share(proxy: String): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        if (!s.paired) return@withContext false
        // The desktop reads PeerProxy from /report, so make sure it has it before /share.
        if (proxy.isNotBlank()) report(proxy)
        val body = JSONObject()
            .put("method", if (proxy.isBlank()) "route" else "proxy")
            .put("proxy", proxy)
            .toString()
        val res = call(s.host, s.port, "POST", "/api/v1/share", body, s.token)
        val ok = res.code == 200
        update { it.copy(sharing = ok, lastError = if (ok) "" else errorOf(res)) }
        if (ok) {
            sharedEndpoint = proxy
            com.netpilot.mobile.data.Repo.log("evt_pc_share")
            status()
        }
        ok
    }

    suspend fun unpair(): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        if (s.host.isNotBlank() && s.paired) {
            call(s.host, s.port, "DELETE", "/api/v1/pair", null, s.token)
        }
        update { it.copy(paired = false, token = "", sharing = false, reachable = false) }
        com.netpilot.mobile.data.Repo.log("evt_pc_unpair")
        true
    }

    private suspend fun simpleAction(path: String): Boolean = withContext(Dispatchers.IO) {
        val s = _state.value
        if (!s.paired) return@withContext false
        update { it.copy(busy = true) }
        val res = call(s.host, s.port, "POST", path, "{}", s.token)
        val ok = res.code == 200
        update { it.copy(busy = false, lastError = if (ok) "" else errorOf(res)) }
        if (ok) status()
        ok
    }

    // ------------------------------------------------------------------ reporting loop

    /**
     * Periodic `/report` while paired — the PC treats a >5 min gap as "phone gone".
     *
     * The proxy endpoint is read on every tick instead of being frozen at the call, so
     * starting or stopping the LAN proxy does not require restarting the loop. This is
     * owned by [com.netpilot.mobile.App], not by the screen, so leaving the Mobile VPN
     * page does not silently drop the link.
     */
    fun startReporting() {
        if (reporter?.isActive == true) return
        reporter = kotlinx.coroutines.CoroutineScope(Dispatchers.IO).launch {
            while (isActive) {
                val s = _state.value
                if (s.paired) {
                    val endpoint = if (ProxyServer.isRunning) ProxyServer.endpoint() else ""
                    report(endpoint)
                    status()
                    // Reconcile after the status read: the desktop only ever does what it
                    // was last asked, so a proxy that is on has to be (re)applied whenever
                    // its address moved — Wi-Fi roam, DHCP renewal — and one that went off
                    // has to be taken off the PC again. status() runs first so a failure
                    // here is still the message the screen shows.
                    val now = _state.value
                    if (endpoint.isNotBlank()) {
                        if (endpoint != sharedEndpoint) share(endpoint)
                    } else if (now.sharing) {
                        stopShare()
                    }
                }
                delay(30_000L)
            }
        }
    }

    fun stopReporting() {
        reporter?.cancel()
        reporter = null
    }

    // ------------------------------------------------------------------ transport

    private data class Res(val code: Int, val body: String)

    /**
     * Turns a failure into a sentence the user can act on.
     *
     * The desktop sends machine keys (`mv_no_proxy`, `mv_blocked_vpn`, …) in `detail`;
     * those are translated here when this app happens to know them. Transport failures
     * arrive as the exception's class name and are mapped to the usual three causes.
     */
    private fun errorOf(r: Res): String = when {
        r.code == -1 -> transport(r.body)
        r.code == 401 -> com.netpilot.mobile.data.Strings.raw("mv_err_auth")
        r.code == 403 -> com.netpilot.mobile.data.Strings.raw("mv_err_code")
        r.code == 429 -> com.netpilot.mobile.data.Strings.raw("mv_err_busy")
        r.code <= 0 -> com.netpilot.mobile.data.Strings.raw("mv_error")
        else -> detail(r.body) ?: "HTTP ${r.code}"
    }

    /**
     * Turns a desktop failure payload into a sentence.
     *
     * The Windows side answers `{key, detail}`: `key` is the localization key
     * (`mv_blocked_vpn`, `mv_no_proxy`, …) and `detail` carries extra text — for the
     * generic `mv_error` it *is* the actual reason (a PowerShell message), so that one
     * wins over the label. Reading only `detail` used to collapse every refusal into
     * "HTTP 409".
     */
    private fun detail(body: String): String? = runCatching {
        val o = JSONObject(body)
        val key = o.optString("key")
        val text = o.optString("detail").ifBlank { o.optString("error") }
        when {
            key.isBlank() && text.isBlank() -> null
            key == "mv_error" && text.isNotBlank() -> text
            key.isNotBlank() && com.netpilot.mobile.data.Strings.has(key) ->
                com.netpilot.mobile.data.Strings.raw(key)
            text.isNotBlank() && com.netpilot.mobile.data.Strings.has(text) ->
                com.netpilot.mobile.data.Strings.raw(text)
            text.isNotBlank() -> text
            else -> key
        }
    }.getOrNull()

    private fun transport(what: String): String = when {
        what.isBlank() -> com.netpilot.mobile.data.Strings.raw("mv_error")
        what.contains("ConnectException") || what.contains("refused") ->
            com.netpilot.mobile.data.Strings.raw("mv_hint_refused")
        what.contains("UnknownHost") ->
            com.netpilot.mobile.data.Strings.raw("mv_hint_unresolved")
        what.contains("timeout", ignoreCase = true) ->
            com.netpilot.mobile.data.Strings.raw("mv_hint_timeout")
        else -> com.netpilot.mobile.data.Strings.raw("mv_error")
    }

    private fun call(
        host: String,
        port: Int,
        method: String,
        path: String,
        body: String?,
        token: String?
    ): Res {
        val (h, p) = normalizeTarget(host, port)
        if (h.isBlank()) return Res(-1, "")
        return try {
            val url = URL("http://$h:$p$path")
            val conn = (url.openConnection() as HttpURLConnection).apply {
                requestMethod = method
                // /status answers from a full adapter + peer probe, which on a cold or
                // heavily loaded PC takes several seconds. Timing out at 3.5s made the phone
                // claim "the PC may be asleep" about a bridge that was perfectly healthy.
                val slow = path.endsWith("/status") || path.endsWith("/share")
                connectTimeout = if (slow) 8000 else 2500
                readTimeout = if (slow) 15000 else 3500
                doInput = true
                if (!token.isNullOrBlank()) {
                    setRequestProperty("Authorization", "Bearer $token")
                    setRequestProperty("X-NetPilot-Token", token)
                }
                if (body != null) {
                    doOutput = true
                    setRequestProperty("Content-Type", "application/json")
                }
            }
            if (body != null) {
                OutputStreamWriter(conn.outputStream, Charsets.UTF_8).use { w ->
                    w.write(body); w.flush()
                }
            }
            val code = conn.responseCode
            val stream = if (code in 200..399) conn.inputStream else conn.errorStream
            val text = stream?.let { s ->
                BufferedReader(InputStreamReader(s, Charsets.UTF_8)).use { it.readText() }
            } ?: ""
            conn.disconnect()
            Res(code, text)
        } catch (t: Throwable) {
            Res(-1, t.javaClass.simpleName)
        }
    }

    private fun deviceName(): String =
        runCatching { android.os.Build.MODEL }.getOrDefault("Android")

    /** A quick reachability probe used by the UI before any HTTP call. */
    suspend fun probePort(host: String, port: Int): Boolean = withContext(Dispatchers.IO) {
        if (host.isBlank()) return@withContext false
        try {
            Socket().use { s ->
                s.connect(InetSocketAddress(host, port), 1200)
                true
            }
        } catch (t: Throwable) {
            false
        }
    }
}
