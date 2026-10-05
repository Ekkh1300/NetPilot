package com.netpilot.mobile.vpn

import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.channels.ClosedSelectorException
import java.nio.channels.DatagramChannel
import java.nio.channels.SelectionKey
import java.nio.channels.Selector
import java.nio.channels.SocketChannel
import kotlin.math.min
import kotlin.random.Random

/**
 * The forwarding half of FIREWALL mode.
 *
 * Android's `VpnService` gives us packets but no kernel path back out, so anything that is
 * not DNS has to be carried by hand: TCP is terminated here and relayed over a protected
 * [SocketChannel], UDP is NAT-ed through a per-flow [DatagramChannel]. On top of that the
 * relay applies the rules from [Firewall]:
 *
 *  - **block** — a refused SYN is answered with RST (the app sees "connection refused"
 *    instead of hanging), later flows are simply dropped,
 *  - **limit** — a token bucket paces the segments written back to the client (down) and
 *    the bytes handed to the server (up); UDP has no back-pressure, so datagrams the
 *    budget cannot cover are dropped instead,
 *  - **allow** — relayed with no pacing at all.
 *
 * One selector thread owns every channel; the tunnel's reader thread only feeds packets in
 * through [onPacket]. Both take [lock] when touching flow state, so packet handling is
 * strictly serialised — and, because every buffer is bounded, a slow peer applies
 * back-pressure instead of growing the heap.
 *
 * Host-testable: no Android types appear in the signature beyond the injected callbacks.
 */
internal class Relay(
    private val emit: (ByteArray) -> Unit,
    private val protectSocket: (Socket) -> Boolean,
    private val protectDatagram: (DatagramSocket) -> Boolean,
    private val firewall: Firewall,
    private val onDnsUdp: (query: ByteArray, srcIp: Int, srcPort: Int, dstIp: Int, dstPort: Int) -> Unit,
    private val onDnsTcp: (packet: ByteArray, ihl: Int, srcIp: Int, dstIp: Int) -> Unit
) {

    // ------------------------------------------------------------------ state

    private val lock = Any()
    private var selector: Selector? = null
    private var worker: Thread? = null

    @Volatile
    private var running = false

    private val tcpFlows = HashMap<String, TcpFlow>()
    private val udpFlows = HashMap<String, UdpFlow>()

    /** Local socket -> decision, so the platform is asked once per flow, not per packet. */
    private val decisions = HashMap<String, DecisionEntry>()

    private var ipId = Random.nextInt(0x10000)
    private var lastRuleRefresh = 0L
    private var lastTick = System.currentTimeMillis()

    private data class DecisionEntry(val decision: Decision, val at: Long, val first: Boolean)

    // ------------------------------------------------------------------ lifecycle

    fun start() {
        synchronized(lock) {
            if (running) return
            running = true
            selector = Selector.open()
        }
        worker = Thread({
            // A throw out of the selector loop would leave the tunnel established with
            // nothing reading it: every packet the system routes to us would be dropped and
            // the device would look offline. The service watches [isAlive] and tears the
            // tunnel down instead, which puts the phone back on the real network.
            try {
                loop()
            } catch (t: Throwable) {
                running = false
            }
        }, "np-relay").apply {
            isDaemon = true
            priority = Thread.NORM_PRIORITY - 1
            start()
        }
    }

    /** False once the forwarding loop is gone - the tunnel has to be closed by its owner. */
    fun isAlive(): Boolean = worker?.isAlive == true

    fun close() {
        running = false
        runCatching { selector?.wakeup() }
        worker?.let { runCatching { it.join(1000) } }
        worker = null
        synchronized(lock) {
            tcpFlows.values.forEach { it.closeQuietly() }
            tcpFlows.clear()
            udpFlows.values.forEach { it.closeQuietly() }
            udpFlows.clear()
            decisions.clear()
            runCatching { selector?.close() }
            selector = null
        }
        FirewallStats.active = false
    }

    // ------------------------------------------------------------------ entry point

    /**
     * One IPv4 packet from the tunnel.
     * @param ihl IP header length in bytes (the caller already validated version/length).
     */
    fun onPacket(p: ByteArray, ihl: Int, protocol: Int, srcIp: Int, dstIp: Int) {
        when (protocol) {
            Packet.TCP -> onTcp(p, ihl, srcIp, dstIp)
            Packet.UDP -> onUdp(p, ihl, srcIp, dstIp)
            else -> {
                // ICMP and friends cannot be relayed without a raw socket (which Android
                // only grants to the system). Counted so the UI never pretends otherwise.
                FirewallStats.icmpDropped++
            }
        }
    }

    // ------------------------------------------------------------------ decisions

    /**
     * Cached attribution. The key is the *local* endpoint: one socket belongs to exactly
     * one application, so a second packet from the same socket never pays for another
     * platform round-trip.
     */
    private fun decide(
        proto: Int, srcIp: Int, srcPort: Int, dstIp: Int, dstPort: Int
    ): Decision {
        val key = "$proto/$srcIp/$srcPort"
        synchronized(lock) {
            val now = System.currentTimeMillis()
            val cached = decisions[key]
            if (cached != null && now - cached.at < DECISION_TTL_MS) return cached.decision

            val out = firewall.decide(proto, srcIp, srcPort, dstIp, dstPort)
            decisions[key] = DecisionEntry(out.decision, now, first = cached == null)
            if (cached == null && !out.identified) FirewallStats.unidentifiedFlows++
            if (decisions.size > MAX_DECISIONS) pruneDecisions(now)
            return out.decision
        }
    }

    private fun pruneDecisions(now: Long) {
        val it = decisions.entries.iterator()
        while (it.hasNext()) {
            val e = it.next()
            if (now - e.value.at > DECISION_TTL_MS) it.remove()
        }
        // Still too big? Drop the oldest quarter outright.
        if (decisions.size > MAX_DECISIONS) {
            decisions.entries
                .sortedBy { it.value.at }
                .take(decisions.size / 4)
                .forEach { decisions.remove(it.key) }
        }
    }

    /** Cheap re-check of a live flow — schedules can start or stop mid-connection. */
    private fun recheck(f: DecisionHolder, now: Long): Boolean {
        if (now - f.decidedAt < RULE_RECHECK_MS) return true
        f.decidedAt = now
        val d = firewall.decidePkg(f.pkg)
        f.decision = d
        return d !is Decision.Block
    }

    private interface DecisionHolder {
        var decision: Decision
        var pkg: String?
        var decidedAt: Long
    }

    // ------------------------------------------------------------------ TCP

    private fun onTcp(p: ByteArray, ihl: Int, srcIp: Int, dstIp: Int) {
        val h = Packet.parseTcp(p, ihl) ?: return
        val decision = decide(Packet.TCP, srcIp, h.srcPort, dstIp, h.dstPort)

        if (decision is Decision.Block) {
            FirewallStats.blockedPackets++
            if (h.flags and Packet.SYN != 0 && h.flags and Packet.ACK == 0) {
                val rst = Packet.buildRst(p, ihl, srcIp, dstIp, nextId())
                if (rst != null) emit(rst)
            }
            return
        }

        // DNS over TCP stays with the tunnel's resolver path (cache, benchmark, stats).
        if (h.dstPort == 53) {
            onDnsTcp(p, ihl, srcIp, dstIp)
            return
        }

        val key = "$srcIp:${h.srcPort}>$dstIp:${h.dstPort}"
        val now = System.currentTimeMillis()

        synchronized(lock) {
            val flow = tcpFlows[key]
            if (flow == null) {
                if (h.flags and Packet.SYN == 0) return          // stray segment
                if (tcpFlows.size >= MAX_TCP_FLOWS) return        // flow table full
                // The SYN is answered by the flow's own setup (it emits SYN|ACK), so it is
                // not handed to handleSegment as well — otherwise a connection that
                // completes the upstream connect quickly gets two handshake replies.
                openTcpFlow(key, srcIp, h, dstIp, decision) ?: return
                return
            }
            if (!recheck(flow, now)) {
                rejectFlow(flow)
                return
            }
            handleSegment(flow, p, ihl, h, now)
        }
    }

    private fun openTcpFlow(
        key: String,
        srcIp: Int,
        h: Packet.TcpHdr,
        dstIp: Int,
        decision: Decision
    ): TcpFlow? {
        val mss = when {
            h.mss in 68..MAX_MSS -> h.mss
            h.mss > MAX_MSS -> MAX_MSS
            else -> 536
        }
        val ourIsn = Random.nextLong(0, 0x100000000L) and 0xFFFFFFFFL
        val flow = TcpFlow(
            key = key,
            clientIp = srcIp, clientPort = h.srcPort,
            serverIp = dstIp, serverPort = h.dstPort,
            ourIsn = ourIsn,
            mss = mss,
            decision = decision,
            pkg = pkgOf(decision),
            decidedAt = System.currentTimeMillis()
        )
        flow.clientSeq = (h.seq + 1) and 0xFFFFFFFFL
        flow.clientWindow = h.window          // the peer's real buffer, not a guess
        flow.down = Down((ourIsn + 1) and 0xFFFFFFFFL)

        // SYN|ACK goes out before the upstream connect so the application starts moving at
        // once; a failed connect is reported back as RST.
        emit(
            Packet.buildSynAck(
                serverIp = dstIp, serverPort = h.dstPort,
                clientIp = srcIp, clientPort = h.srcPort,
                ourSeq = ourIsn, clientSeq = flow.clientSeq,
                mss = mss, window = Packet.MAX_WINDOW, ipId = nextId()
            )
        )

        val channel = try {
            SocketChannel.open().also { ch ->
                ch.configureBlocking(false)
                runCatching { protectSocket(ch.socket()) }
            }
        } catch (t: Throwable) {
            sendRst(flow)
            return null
        }
        flow.channel = channel

        val connected = try {
            ch_connect(channel, dstIp, h.dstPort)
        } catch (t: Throwable) {
            false
        }

        val sel = selector
        if (sel == null || !running) {
            runCatching { channel.close() }
            return null
        }
        flow.connecting = !connected
        val ops = if (connected) {
            SelectionKey.OP_READ
        } else {
            SelectionKey.OP_CONNECT
        }
        try {
            channel.register(sel, ops, flow)
        } catch (t: Throwable) {
            sendRst(flow)
            runCatching { channel.close() }
            return null
        }
        if (connected) afterConnect(flow)
        tcpFlows[key] = flow
        FirewallStats.relayedFlows++
        return flow
    }

    private fun ch_connect(channel: SocketChannel, ip: Int, port: Int): Boolean {
        val address = InetSocketAddress(InetAddress.getByAddress(intBytes(ip)), port)
        return channel.connect(address)
    }

    private fun afterConnect(f: TcpFlow) {
        f.connecting = false
        f.connectedAt = System.currentTimeMillis()
        synchronized(lock) {
            if (f.up.isNotEmpty()) enableWrite(f)
            flushDown(f)
        }
    }

    private fun handleSegment(
        f: TcpFlow,
        p: ByteArray,
        ihl: Int,
        h: Packet.TcpHdr,
        now: Long
    ) {
        f.lastActivity = now

        if (h.flags and Packet.RST != 0) {
            dropFlow(f, announce = false)
            return
        }

        if (f.connecting && h.flags and Packet.SYN != 0 && h.flags and Packet.ACK == 0) {
            // Upstream connect still in flight and the peer re-sent its SYN — repeat ours
            // instead of letting the handshake time out while the socket dials.
            emit(
                Packet.buildSynAck(
                    serverIp = f.serverIp, serverPort = f.serverPort,
                    clientIp = f.clientIp, clientPort = f.clientPort,
                    ourSeq = f.ourIsn, clientSeq = f.clientSeq,
                    mss = f.mss, window = Packet.MAX_WINDOW, ipId = nextId()
                )
            )
            return
        }

        if (h.flags and Packet.ACK != 0) {
            val before = f.down.start
            f.down.ack(h.ack)
            f.clientWindow = h.window
            f.lastAckAt = now
            if (f.down.start > before) f.down.compact()
            if (f.down.capacityLeft > 0) resumeRead(f)
        }

        val payloadOff = ihl + h.headerLen
        val len = if (payloadOff <= p.size) p.size - payloadOff else 0
        val delta = Packet.seqDelta(h.seq, f.clientSeq)

        if (len > 0) {
            if (delta == 0L) {
                if (f.upBytes + len <= MAX_UP) {
                    f.clientSeq = (f.clientSeq + len) and 0xFFFFFFFFL
                    val chunk = p.copyOfRange(payloadOff, p.size)
                    f.up.addLast(chunk)
                    f.upBytes += chunk.size
                    // Queued, not written: while the upstream socket is still dialling,
                    // finishConnect() re-arms the write interest op for us.
                    if (!f.connecting) enableWrite(f)
                }
                // Buffer full: we deliberately withhold the ACK so the peer's own
                // congestion control pushes back instead of our heap growing.
                sendAck(f)
            } else {
                // Duplicate or out of order: re-acknowledge the expected byte and let the
                // sender retransmit — cheaper than reordering here.
                sendAck(f)
            }
        }

        if (h.flags and Packet.FIN != 0) {
            val finSeq = (h.seq + len) and 0xFFFFFFFFL
            if (Packet.seqDelta(finSeq, f.clientSeq) == 0L) {
                f.clientSeq = (f.clientSeq + 1) and 0xFFFFFFFFL
                f.clientClosed = true
                sendAck(f)
                maybeShutdownUpstream(f)
            }
        }

        flushDown(f)
    }

    private fun flushDown(f: TcpFlow) {
        if (f.connecting) return

        var windowLeft = f.clientWindow - (f.down.sent - f.down.start)
        if (windowLeft <= 0) return

        while (f.down.unsent > 0) {
            val want = min(f.mss.toLong(), min(windowLeft.toLong(), f.down.unsent.toLong()))
            if (want <= 0) break
            // reserve() *takes* the bytes; peeking with available() here would refill the
            // bucket on every call and let the flow send a full burst each time.
            val n = firewall.reserve(f.pkg, up = false, want).toInt()
            if (n <= 0) break
            val seq = (f.down.base + f.down.sent) and 0xFFFFFFFFL
            val payload = f.down.slice(f.down.sent, n)
            emit(
                Packet.buildTcp(
                    srcIp = f.serverIp, srcPort = f.serverPort,
                    dstIp = f.clientIp, dstPort = f.clientPort,
                    seq = seq, ack = f.clientSeq,
                    flags = Packet.ACK or Packet.PSH,
                    window = ourWindow(f),
                    payload = payload, ipId = nextId()
                )
            )
            f.down.advance(n)
            f.down.compact()
            windowLeft -= n
        }

        if (f.serverClosed) maybeSendFin(f)
    }

    private fun maybeSendFin(f: TcpFlow) {
        if (f.finSent || f.down.unsent > 0) return
        f.finSent = true
        val seq = (f.down.base + f.down.end) and 0xFFFFFFFFL
        emit(
            Packet.buildTcp(
                srcIp = f.serverIp, srcPort = f.serverPort,
                dstIp = f.clientIp, dstPort = f.clientPort,
                seq = seq, ack = f.clientSeq,
                flags = Packet.ACK or Packet.FIN,
                window = ourWindow(f), ipId = nextId()
            )
        )
        f.closeAt = System.currentTimeMillis() + FIN_GRACE_MS
    }

    private fun sendAck(f: TcpFlow) {
        val seq = (f.down.base + f.down.sent) and 0xFFFFFFFFL
        emit(
            Packet.buildTcp(
                srcIp = f.serverIp, srcPort = f.serverPort,
                dstIp = f.clientIp, dstPort = f.clientPort,
                seq = seq, ack = f.clientSeq,
                flags = Packet.ACK, window = ourWindow(f), ipId = nextId()
            )
        )
    }

    private fun sendRst(f: TcpFlow) {
        emit(
            Packet.buildTcp(
                srcIp = f.serverIp, srcPort = f.serverPort,
                dstIp = f.clientIp, dstPort = f.clientPort,
                seq = (f.down.base + f.down.end) and 0xFFFFFFFFL,
                ack = f.clientSeq,
                flags = Packet.RST or Packet.ACK, window = 0, ipId = nextId()
            )
        )
    }

    private fun ourWindow(f: TcpFlow): Int =
        min(Packet.MAX_WINDOW.toLong(), f.down.capacityLeft.toLong()).coerceAtLeast(0L).toInt()

    private fun rejectFlow(f: TcpFlow) {
        sendRst(f)
        dropFlow(f, announce = false)
    }

    private fun dropFlow(f: TcpFlow, announce: Boolean) {
        tcpFlows.remove(f.key)
        f.closeQuietly()
        if (announce) FirewallStats.blockedPackets++
    }

    private fun maybeShutdownUpstream(f: TcpFlow) {
        if (f.upBytes == 0) {
            runCatching { f.channel?.shutdownOutput() }
        }
    }

    // ------------------------------------------------------------------ selector thread

    private fun loop() {
        val sel = selector ?: return
        var busy = false
        while (running) {
            val timeout = if (busy) TICK_MS else IDLE_TICK_MS
            busy = false
            try {
                sel.select(timeout)
            } catch (e: ClosedSelectorException) {
                break
            } catch (t: Throwable) {
                if (!running) break
                runCatching { Thread.sleep(20) }
                continue
            }

            val keys = sel.selectedKeys()
            val it = keys.iterator()
            while (it.hasNext()) {
                val key = it.next()
                it.remove()
                val att = key.attachment()
                try {
                    when {
                        !key.isValid -> Unit
                        key.isConnectable -> {
                            val f = att as? TcpFlow ?: continue
                            if (finishConnect(key, f)) busy = true
                        }
                        key.isReadable -> {
                            when (val a = key.attachment()) {
                                is TcpFlow -> if (readServer(a)) busy = true
                                is UdpFlow -> if (readUdp(a)) busy = true
                            }
                        }
                        key.isWritable -> {
                            val f = att as? TcpFlow ?: continue
                            if (writeToServer(f)) busy = true
                        }
                    }
                } catch (t: Throwable) {
                    // Same reason as in readUdp: the flow maps are owned by [lock].
                    synchronized(lock) {
                        (att as? TcpFlow)?.let { sendRstQuietly(it); dropFlow(it, false) }
                        (att as? UdpFlow)?.let { removeUdp(it) }
                    }
                }
            }

            pumpDown()
            maintenance(sel)
            if (hasPendingWork()) busy = true
        }
    }

    /**
     * Pushes out whatever the token buckets currently allow for every flow that still owes
     * the client bytes.
     *
     * Without this a shaped flow can stall: once the downstream buffer is full the read side
     * is suspended, and if the client has nothing left to acknowledge there is no packet left
     * to trigger a flush either - the bucket refills and sits there unused, which showed up
     * as a 16 KB/s limit delivering 1 KB/s.
     */
    private fun pumpDown() {
        synchronized(lock) {
            for (f in tcpFlows.values) {
                if (!f.connecting && f.down.unsent > 0) flushDown(f)
            }
        }
    }

    private fun maintenance(sel: Selector) {
        val now = System.currentTimeMillis()
        if (now - lastRuleRefresh > RULE_REFRESH_MS) {
            lastRuleRefresh = now
            firewall.refresh()
        }
        val tick = now - lastTick
        if (tick < TICK_MS) return
        lastTick = now

        synchronized(lock) {
            val expired = ArrayList<TcpFlow>()
            for (f in tcpFlows.values) {
                if (f.connecting && now - f.connectedAt > CONNECT_TIMEOUT_MS) {
                    expired.add(f); continue
                }
                if (f.closeAt != 0L && f.closeAt < now) { expired.add(f); continue }
                if (now - f.lastActivity > TCP_IDLE_MS && f.upBytes == 0 &&
                    f.down.unsent == 0 && f.down.pending == 0
                ) {
                    expired.add(f); continue
                }
                retransmitIfStale(f, now)
                if (!recheck(f, now)) { expired.add(f); sendRst(f) }
                // A flow that ran out of upload budget dropped its interest op; re-arm it
                // or nothing would ever wake the selector for those queued bytes.
                if (f.up.isNotEmpty() && !f.connecting) enableWrite(f)
            }
            expired.forEach { dropFlow(it, false) }

            val expiredUdp = udpFlows.values.filter { now - it.lastActivity > UDP_IDLE_MS }
            expiredUdp.forEach { removeUdp(it) }
        }
    }

    private fun retransmitIfStale(f: TcpFlow, now: Long) {
        if (f.connecting || f.down.sent <= f.down.start) return
        if (now - f.lastAckAt < f.rtoMs) return
        f.lastAckAt = now
        f.rtoMs = min(f.rtoMs * 2, MAX_RTO_MS)
        var offset = f.down.start
        while (offset < f.down.sent) {
            val n = min(f.mss, f.down.sent - offset)
            val seq = (f.down.base + offset) and 0xFFFFFFFFL
            emit(
                Packet.buildTcp(
                    srcIp = f.serverIp, srcPort = f.serverPort,
                    dstIp = f.clientIp, dstPort = f.clientPort,
                    seq = seq, ack = f.clientSeq,
                    flags = Packet.ACK or Packet.PSH, window = ourWindow(f),
                    payload = f.down.slice(offset, n), ipId = nextId()
                )
            )
            offset += n
        }
    }

    private fun hasPendingWork(): Boolean {
        synchronized(lock) {
            for (f in tcpFlows.values) {
                if (f.up.isNotEmpty()) return true
                if (f.down.unsent > 0 && !f.connecting) return true
            }
            return false
        }
    }

    private fun finishConnect(key: SelectionKey, f: TcpFlow): Boolean {
        val ch = f.channel ?: return false
        val ok = try {
            ch.finishConnect()
        } catch (t: Throwable) {
            sendRstQuietly(f)
            dropFlow(f, false)
            runCatching { key.cancel() }
            return false
        }
        if (!ok) return false
        f.connecting = false
        f.connectedAt = System.currentTimeMillis()
        try {
            key.interestOps(SelectionKey.OP_READ)
        } catch (t: Throwable) {
            dropFlow(f, false)
            return false
        }
        synchronized(lock) {
            if (f.up.isNotEmpty()) enableWrite(f)
            flushDown(f)
        }
        return true
    }

    /**
     * Reads server data into the per-flow buffer; pauses the channel when it fills up.
     *
     * The scratch buffer is allocated at the full [MAX_READ] every time rather than at
     * `capacityLeft`. Sizing it to the free space looked like the cheaper choice and was the
     * wrong one for a reason that is easy to miss: `ByteBuffer.allocate` hands back uninitialised
     * memory, so a short read leaves the tail as whatever the previous flow left there. `flip()`
     * then sets the limit to the number of bytes actually read, which is what makes it correct -
     * but only because the *count* is trusted. Any path that used `capacity` instead of the read
     * count would ship another flow's bytes.
     *
     * Allocating once per flow rather than per read keeps that guarantee obvious: the buffer is
     * a field, it is always cleared, and every read writes exactly the count it reports.
     */
    private fun readServer(f: TcpFlow): Boolean {
        val ch = f.channel ?: return false

        val space = f.down.capacityLeft.coerceAtLeast(0)
        if (space == 0) {
            suspendRead(f)
            return false
        }

        val want = min(MAX_READ, space)
        val tmp = ByteBuffer.allocate(want)
        tmp.clear()                 // position 0, limit = capacity; never carries stale state

        val n = ch.read(tmp)
        if (n < 0) {
            synchronized(lock) {
                f.serverClosed = true
                flushDown(f)
            }
            suspendRead(f)
            return true
        }
        if (n == 0) return false

        // Take exactly n bytes from position 0. Never limit/capacity: that is the distinction
        // between reading what arrived and reading whatever the allocation happened to contain.
        val bytes = ByteArray(n)
        tmp.position(0)
        tmp.limit(n)
        tmp.get(bytes)
        synchronized(lock) {
            if (!recheck(f, System.currentTimeMillis())) {
                rejectFlow(f)
                return true
            }
            if (!f.down.append(bytes, 0, bytes.size)) {
                // The flow is at its cap and the bytes are unacknowledged, so there is nowhere
                // to put them. Resetting is the only correct answer: silently skipping would put a
                // hole in the byte stream that the client cannot distinguish from corruption,
                // and it would spend the rest of the connection re-requesting instead of
                // reconnecting.
                com.netpilot.mobile.core.log.NpLog.warn(
                    "vpn",
                    "dropping a TCP flow that reached ${Relay.MAX_DOWN} bytes without " +
                        "acknowledging any of it; a peer that never acknowledges is bounded here " +
                        "rather than allowed to consume the phone's memory"
                )
                rejectFlow(f)
                return true
            }
            f.lastActivity = System.currentTimeMillis()
            flushDown(f)
            if (f.down.capacityLeft <= 0) suspendRead(f)
        }
        return true
    }

    /** Hands accepted client bytes to the server, paced by the upload budget. */
    private fun writeToServer(f: TcpFlow): Boolean {
        val ch = f.channel ?: return false
        val chunk: ByteArray
        synchronized(lock) {
            val head = f.up.firstOrNull()
            if (head == null) {
                clearWrite(f)
                return false
            }
            val take = firewall.reserve(f.pkg, up = true, head.size.toLong()).toInt()
            if (take <= 0) {
                // Out of budget: keep the queue, drop the interest op; maintenance re-arms
                // it so the flow resumes as soon as the bucket refills.
                clearWrite(f)
                return false
            }
            chunk = if (take >= head.size) {
                f.up.removeFirst()
                head
            } else {
                f.up.addFirst(head.copyOfRange(take, head.size))
                head.copyOfRange(0, take)
            }
            f.upBytes = f.up.sumOf { it.size }
        }

        val buf = ByteBuffer.wrap(chunk)
        val written = try {
            ch.write(buf)
        } catch (t: Throwable) {
            sendRstQuietly(f)
            dropFlow(f, false)
            return false
        }
        val rest = buf.remaining()
        synchronized(lock) {
            if (rest > 0) {
                val restBytes = ByteArray(rest)
                buf.get(restBytes)
                // Partial write: the unwritten tail must stay at the front of the queue -
                // and it was paid for but never sent, so it goes back into the budget.
                f.up.addFirst(restBytes)
                firewall.refund(f.pkg, up = true, restBytes.size.toLong())
            }
            f.upBytes = f.up.sumOf { it.size }
            if (f.up.isEmpty()) {
                clearWrite(f)
                if (f.clientClosed) runCatching { f.channel?.shutdownOutput() }
            } else {
                kickWrite(f)
            }
        }
        return written > 0 || rest > 0
    }

    private fun enableWrite(f: TcpFlow) {
        val key = f.channel?.keyFor(selector ?: return) ?: return
        try {
            key.interestOps(key.interestOps() or SelectionKey.OP_WRITE)
        } catch (t: Throwable) {
            // Selector closed while a flow was still moving — the tunnel is going away.
        }
    }

    private fun clearWrite(f: TcpFlow) {
        val key = f.channel?.keyFor(selector ?: return) ?: return
        try {
            key.interestOps(key.interestOps() and SelectionKey.OP_WRITE.inv())
        } catch (t: Throwable) {
        }
    }

    private fun kickWrite(f: TcpFlow) {
        runCatching { selector?.wakeup() }
    }

    private fun suspendRead(f: TcpFlow) {
        val key = f.channel?.keyFor(selector ?: return) ?: return
        try {
            key.interestOps(key.interestOps() and SelectionKey.OP_READ.inv())
        } catch (t: Throwable) {
        }
    }

    private fun resumeRead(f: TcpFlow) {
        val key = f.channel?.keyFor(selector ?: return) ?: return
        try {
            val ops = key.interestOps()
            if (ops and SelectionKey.OP_READ == 0) key.interestOps(ops or SelectionKey.OP_READ)
        } catch (t: Throwable) {
        }
    }

    private fun sendRstQuietly(f: TcpFlow) = runCatching { sendRst(f) }

    // ------------------------------------------------------------------ UDP

    private fun onUdp(p: ByteArray, ihl: Int, srcIp: Int, dstIp: Int) {
        val h = Packet.parseUdp(p, ihl) ?: return
        val decision = decide(Packet.UDP, srcIp, h.srcPort, dstIp, h.dstPort)

        if (decision is Decision.Block) {
            FirewallStats.blockedPackets++
            return
        }

        if (h.dstPort == 53) {
            if (h.payloadEnd - h.payloadOff >= 12) {
                onDnsUdp(
                    p.copyOfRange(h.payloadOff, h.payloadEnd),
                    srcIp, h.srcPort, dstIp, h.dstPort
                )
            }
            return
        }

        val payload = p.copyOfRange(h.payloadOff, h.payloadEnd)
        val key = "$srcIp:${h.srcPort}>$dstIp:${h.dstPort}"
        val now = System.currentTimeMillis()

        synchronized(lock) {
            val flow = udpFlows[key]
            if (flow == null) {
                if (udpFlows.size >= MAX_UDP_FLOWS) return
                val created = openUdpFlow(key, srcIp, h, dstIp, decision) ?: return
                sendUdp(created, payload, now)
                return
            }
            if (!recheck(flow, now)) {
                removeUdp(flow)
                return
            }
            sendUdp(flow, payload, now)
        }
    }

    private fun openUdpFlow(
        key: String,
        srcIp: Int,
        h: Packet.UdpHdr,
        dstIp: Int,
        decision: Decision
    ): UdpFlow? {
        val channel = try {
            DatagramChannel.open().also { ch ->
                ch.configureBlocking(false)
                runCatching { protectDatagram(ch.socket()) }
                ch.connect(InetSocketAddress(InetAddress.getByAddress(intBytes(dstIp)), h.dstPort))
            }
        } catch (t: Throwable) {
            return null
        }
        val flow = UdpFlow(
            key = key,
            clientIp = srcIp, clientPort = h.srcPort,
            serverIp = dstIp, serverPort = h.dstPort,
            decision = decision,
            pkg = pkgOf(decision),
            decidedAt = System.currentTimeMillis(),
            channel = channel
        )
        val sel = selector
        if (sel == null || !running) {
            runCatching { channel.close() }
            return null
        }
        try {
            channel.register(sel, SelectionKey.OP_READ, flow)
        } catch (t: Throwable) {
            runCatching { channel.close() }
            return null
        }
        udpFlows[key] = flow
        FirewallStats.relayedFlows++
        return flow
    }

    private fun sendUdp(flow: UdpFlow, payload: ByteArray, now: Long) {
        if (payload.isEmpty()) return
        if (!firewall.spend(flow.pkg, up = true, payload.size.toLong())) {
            FirewallStats.limitedDatagrams++
            return
        }
        runCatching { flow.channel.write(ByteBuffer.wrap(payload)) }
        flow.lastActivity = now
    }

    private fun readUdp(flow: UdpFlow): Boolean {
        // allocate() is uninitialised memory, so the read count is what gets taken - never the
        // capacity. See readServer for the full reasoning; this is the same rule in the datagram
        // path, and a datagram shorter than the buffer is the normal case rather than the
        // exception, so relying on it here would be the same bug with a much higher hit rate.
        val tmp = ByteBuffer.allocate(MAX_READ)
        tmp.clear()

        val n = try {
            flow.channel.read(tmp)
        } catch (t: Throwable) {
            // The map is guarded by [lock] everywhere else; removing an entry from the
            // selector thread without it races the reader thread's insert and can lose the
            // entry (and with it the channel, which only removeUdp closes).
            synchronized(lock) { removeUdp(flow) }
            return false
        }
        if (n <= 0) return false

        val bytes = ByteArray(n)
        tmp.position(0)
        tmp.limit(n)
        tmp.get(bytes)
        synchronized(lock) {
            if (!recheck(flow, System.currentTimeMillis())) {
                removeUdp(flow)
                return true
            }
            if (!firewall.spend(flow.pkg, up = false, n.toLong())) {
                FirewallStats.limitedDatagrams++
                return true
            }
            flow.lastActivity = System.currentTimeMillis()
            emit(
                Packet.buildUdp(
                    payload = bytes,
                    srcIp = flow.serverIp, srcPort = flow.serverPort,
                    dstIp = flow.clientIp, dstPort = flow.clientPort,
                    ipId = nextId()
                )
            )
        }
        return true
    }

    private fun removeUdp(flow: UdpFlow) {
        udpFlows.remove(flow.key)
        flow.closeQuietly()
    }

    // ------------------------------------------------------------------ helpers

    private fun pkgOf(d: Decision): String? = when (d) {
        is Decision.Shape -> d.pkg
        is Decision.Pass -> d.pkg
        else -> null
    }

    private fun nextId(): Int = synchronized(this) {
        ipId = (ipId + 1) and 0xFFFF
        ipId
    }

    private fun intBytes(ip: Int) = byteArrayOf(
        (ip ushr 24).toByte(), (ip ushr 16).toByte(), (ip ushr 8).toByte(), ip.toByte()
    )

    // ------------------------------------------------------------------ flow objects

    private inner class TcpFlow(
        val key: String,
        val clientIp: Int,
        val clientPort: Int,
        val serverIp: Int,
        val serverPort: Int,
        val ourIsn: Long,
        val mss: Int,
        override var decision: Decision,
        override var pkg: String?,
        override var decidedAt: Long
    ) : DecisionHolder {

        var channel: SocketChannel? = null
        var connecting = true
        var connectedAt = System.currentTimeMillis()
        var clientSeq = 0L
        var clientWindow = Packet.MAX_WINDOW
        var lastActivity = System.currentTimeMillis()
        var lastAckAt = System.currentTimeMillis()
        var rtoMs = RTO_MS
        var serverClosed = false
        var clientClosed = false
        var finSent = false
        var closeAt = 0L

        lateinit var down: Down
        val up = ArrayDeque<ByteArray>()
        var upBytes = 0

        fun closeQuietly() {
            runCatching { channel?.close() }
            channel = null
        }
    }

    /**
     * Per-flow receive buffer.
     *
     * `start` is the first unacknowledged byte, `sent` the first not yet handed to the TUN.
     * [compact] drops acknowledged bytes so a long flow keeps a bounded footprint.
     */
    private class Down(initialSeq: Long) {
        private var buf = ByteArray(16_384)
        var base = initialSeq and 0xFFFFFFFFL   // sequence number of buf[0]
        var start = 0                          // first byte not yet acknowledged
        var sent = 0                           // first byte not yet transmitted
        var end = 0                            // first free byte

        val pending: Int get() = end - start
        val unsent: Int get() = end - sent
        val capacityLeft: Int get() = MAX_DOWN - pending

        /**
         * Set when data had to be refused because the flow was already at its cap.
         *
         * The read side respects [MAX_DOWN] through [capacityLeft], so overflowing is not the
         * normal case - but it is reachable, and the reason is worth stating because the buffer
         * had no bound at all before: [compact] only reclaims *acknowledged* bytes, so a client
         * that never acknowledges anything leaves `start` at zero, compaction does nothing, and
         * `append` doubled the array on every read until the phone ran out of memory. One peer,
         * one flow, no cooperation required.
         *
         * Dropping the bytes silently would be worse: TCP is a byte stream, so a gap is
         * indistinguishable from corruption to the client, and it would spend the rest of the
         * connection re-requesting. Resetting the flow is the honest signal.
         */
        var overflowed = false
            private set

        /**
         * Appends, refusing when the flow is already at its cap.
         *
         * Returns false when the caller must reset the flow. The data is not copied in that case,
         * so nothing half-entered the buffer.
         */
        fun append(src: ByteArray, off: Int, len: Int): Boolean {
            if (len <= 0) return true

            // Compact *before* checking the cap, not only when the array is full.
            //
            // The previous order checked `pending + len > MAX_DOWN`, compacted if that failed, and
            // then separately compacted again if `end + len > buf.size`. Compaction moves `end`
            // down but does not change `pending` - pending is `end - start`, and both move by the
            // same amount - so a flow whose acknowledged bytes were sitting in the middle of the
            // array could reach the cap while `end + len` was still inside `buf.size`, hit the
            // refusal branch, compact, find `pending` unchanged, and reject the flow that was
            // about to fit. A test caught this at 16 KB against an 8 KB cap.
            //
            // Compacting unconditionally when anything is acknowledged is cheap - it is a
            // memmove of the unacknowledged tail, usually zero bytes - and it makes the bound
            // depend only on how much the client has actually acknowledged, which is the thing
            // the cap is meant to model.
            if (start > 0) compact()

            if (pending + len > MAX_DOWN) {
                overflowed = true
                return false
            }

            if (end + len > buf.size) {
                // Grow only as far as the cap. The previous `maxOf(size * 2, end + len)` had no
                // ceiling, so the doubling walked straight past MAX_DOWN on the way to whatever
                // the next read happened to be - which is how a peer that never acknowledges
                // could grow the buffer without limit.
                val want = maxOf(buf.size * 2, end + len)
                buf = buf.copyOf(minOf(want, MAX_DOWN).coerceAtLeast(end + len))
            }

            System.arraycopy(src, off, buf, end, len)
            end += len
            return true
        }

        fun ack(seq: Long) {
            val offset = Packet.seqDelta(seq, base)
            if (offset <= 0) return
            val newStart = if (offset > sent) sent else offset.toInt()
            if (newStart > start) start = newStart
        }

        fun advance(n: Int) {
            sent += n
        }

        fun slice(from: Int, len: Int): ByteArray = buf.copyOfRange(from, from + len)

        /**
         * Drops acknowledged bytes.
         *
         * A no-op when nothing has been acknowledged, and that is correct rather than a bug:
         * unacknowledged bytes are still owed to the client, so there is nothing to reclaim.
         * [append] is what enforces the bound in that case.
         */
        fun compact() {
            if (start <= 0) return
            val n = start
            val keep = end - n
            if (keep > 0) System.arraycopy(buf, n, buf, 0, keep)
            end -= n
            sent -= n
            start = 0
            base = (base + n) and 0xFFFFFFFFL
        }
    }

    private inner class UdpFlow(
        val key: String,
        val clientIp: Int,
        val clientPort: Int,
        val serverIp: Int,
        val serverPort: Int,
        override var decision: Decision,
        override var pkg: String?,
        override var decidedAt: Long,
        val channel: DatagramChannel
    ) : DecisionHolder {
        var lastActivity = System.currentTimeMillis()

        fun closeQuietly() {
            runCatching { channel.close() }
        }
    }

    // ------------------------------------------------------------------ tuning

    companion object {
        /** Per-direction per-flow buffer. Bounds both memory and the worst-case burst. */
        const val MAX_DOWN = 64 * 1024
        const val MAX_UP = 64 * 1024
        private const val MAX_READ = 32 * 1024
        private const val MAX_MSS = 1460

        private const val MAX_TCP_FLOWS = 48
        private const val MAX_UDP_FLOWS = 24
        private const val MAX_DECISIONS = 512

        private const val TICK_MS = 30L
        private const val IDLE_TICK_MS = 250L
        private const val RULE_REFRESH_MS = 2_000L
        private const val RULE_RECHECK_MS = 5_000L
        private const val DECISION_TTL_MS = 15_000L

        private const val CONNECT_TIMEOUT_MS = 10_000L
        private const val TCP_IDLE_MS = 5 * 60_000L
        private const val UDP_IDLE_MS = 60_000L
        private const val FIN_GRACE_MS = 5_000L

        private const val RTO_MS = 1_500L
        private const val MAX_RTO_MS = 8_000L
    }
}
