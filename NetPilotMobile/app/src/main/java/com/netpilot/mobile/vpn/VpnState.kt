package com.netpilot.mobile.vpn

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Shared, observable state of the local tunnel. Written by
 * [NetPilotVpnService], read by the UI (dashboard, DNS manager, quick switcher,
 * tray/notification) — the mobile equivalent of the desktop service's public state.
 */
object VpnState {

    enum class Mode { OFF, DNS, FIREWALL }

    data class DnsStats(
        val queries: Long = 0,
        val answered: Long = 0,
        val errors: Long = 0,
        val avgMs: Int = -1,
        val cacheHits: Long = 0,
        val lastMs: Int = -1
    )

    private val _mode = MutableStateFlow(Mode.OFF)
    val mode: StateFlow<Mode> = _mode.asStateFlow()

    /** Resolver entry id currently applied through the tunnel ("" = system DNS). */
    private val _dnsId = MutableStateFlow("")
    val dnsId: StateFlow<String> = _dnsId.asStateFlow()

    private val _dnsName = MutableStateFlow("")
    val dnsName: StateFlow<String> = _dnsName.asStateFlow()

    private val _stats = MutableStateFlow(DnsStats())
    val stats: StateFlow<DnsStats> = _stats.asStateFlow()

    private val _error = MutableStateFlow("")
    val error: StateFlow<String> = _error.asStateFlow()

    private val _establishedAt = MutableStateFlow(0L)
    val establishedAt: StateFlow<Long> = _establishedAt.asStateFlow()

    val active: Boolean get() = _mode.value != Mode.OFF
    val isDnsActive: Boolean get() = _mode.value == Mode.DNS

    // ------------------------------------------------------------------ writes (service side)

    fun onTunnelUp(mode: Mode, dnsId: String, dnsName: String) {
        _mode.value = mode
        _dnsId.value = dnsId
        _dnsName.value = dnsName
        _establishedAt.value = System.currentTimeMillis()
        _error.value = ""
    }

    fun onTunnelDown() {
        _mode.value = Mode.OFF
        _dnsId.value = ""
        _dnsName.value = ""
        _establishedAt.value = 0L
    }

    fun onError(message: String) {
        _error.value = message
    }

    fun onQuery(ms: Int, ok: Boolean, cacheHit: Boolean = false) {
        // Answers land from the UDP coroutines and the TCP pool at the same time; a plain
        // read-modify-write on the flow let two of them start from the same snapshot and the
        // second write drop the first one's increment. update{} retries on contention.
        _stats.update { s ->
            val answered = s.answered + if (ok) 1 else 0
            val errors = s.errors + if (ok) 0 else 1
            val avg = if (ms < 0) s.avgMs
            else if (s.avgMs < 0) ms
            else ((s.avgMs * 0.8) + (ms * 0.2)).toInt()
            DnsStats(
                queries = s.queries + 1,
                answered = answered,
                errors = errors,
                avgMs = avg,
                cacheHits = s.cacheHits + if (cacheHit) 1 else 0,
                lastMs = ms
            )
        }
    }

    fun resetStats() {
        _stats.value = DnsStats()
    }

    fun setFirewall(on: Boolean) {
        // FIREWALL mode is used once the per-app engine is engaged; DNS keeps working.
        if (on && _mode.value == Mode.DNS) _mode.value = Mode.FIREWALL
        else if (!on && _mode.value == Mode.FIREWALL) _mode.value = Mode.DNS
    }
}
