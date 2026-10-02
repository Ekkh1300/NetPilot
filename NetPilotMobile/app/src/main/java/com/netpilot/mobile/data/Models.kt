package com.netpilot.mobile.data

// ---------------------------------------------------------------------------------------
// Shared domain models. Everything here is plain data so it can be serialized to JSON by
// [Store] and shared between the UI, the VPN engine and the PC bridge.
// ---------------------------------------------------------------------------------------

/** A DNS resolver (preset or user defined). */
data class DnsEntry(
    val id: String,
    val name: String,
    val primary: String,
    val secondary: String = "",
    val custom: Boolean = false,
    val tagEn: String = "",
    val tagFa: String = "",
    val family: Boolean = false
) {
    val servers: List<String>
        get() = listOf(primary) + if (secondary.isBlank()) emptyList() else listOf(secondary)

    fun toJson(): org.json.JSONObject = org.json.JSONObject().apply {
        put("id", id); put("name", name); put("primary", primary); put("secondary", secondary)
        put("custom", custom); put("tagEn", tagEn); put("tagFa", tagFa); put("family", family)
    }

    companion object {
        fun fromJson(o: org.json.JSONObject) = DnsEntry(
            id = o.optString("id"),
            name = o.optString("name"),
            primary = o.optString("primary"),
            secondary = o.optString("secondary"),
            custom = o.optBoolean("custom"),
            tagEn = o.optString("tagEn"),
            tagFa = o.optString("tagFa"),
            family = o.optBoolean("family")
        )
    }
}

/** Result of a single DNS query probe. */
data class DnsProbe(
    val server: String,
    val ok: Boolean,
    val ms: Int,             // -1 when the probe failed
    val lossPct: Double,     // 0..100 over the round set
    val avgMs: Int,          // average of the successful probes
    val jitterMs: Int        // mean absolute deviation between consecutive successes
)

/** Aggregate benchmark result for one resolver. */
data class DnsBenchResult(
    val entry: DnsEntry,
    val probes: List<DnsProbe>,
    val avgMs: Int,
    val jitterMs: Int,
    val lossPct: Double,
    val rank: Int = 0
)

/** Per-app firewall / shaping rule. */
enum class RuleMode { ALLOW, LIMIT, BLOCK }

data class LimitRule(
    val pkg: String,
    val label: String,
    val mode: RuleMode = RuleMode.ALLOW,
    val downBps: Long = 0L,   // bytes/second, 0 = unlimited
    val upBps: Long = 0L
) {
    fun toJson(): org.json.JSONObject = org.json.JSONObject().apply {
        put("pkg", pkg); put("label", label); put("mode", mode.name)
        put("down", downBps); put("up", upBps)
    }

    companion object {
        fun fromJson(o: org.json.JSONObject) = LimitRule(
            pkg = o.optString("pkg"),
            label = o.optString("label"),
            mode = runCatching { RuleMode.valueOf(o.optString("mode")) }.getOrDefault(RuleMode.ALLOW),
            downBps = o.optLong("down"),
            upBps = o.optLong("up")
        )
    }
}

/** Time window during which a limit is enforced (minutes from local midnight). */
data class ScheduleRule(
    val id: String,
    val pkg: String,
    val label: String,
    val startMin: Int,
    val endMin: Int,
    val downBps: Long = 0L,
    val upBps: Long = 0L,
    val enabled: Boolean = true
) {
    fun toJson(): org.json.JSONObject = org.json.JSONObject().apply {
        put("id", id); put("pkg", pkg); put("label", label)
        put("start", startMin); put("end", endMin)
        put("down", downBps); put("up", upBps); put("enabled", enabled)
    }

    companion object {
        fun fromJson(o: org.json.JSONObject) = ScheduleRule(
            id = o.optString("id"),
            pkg = o.optString("pkg"),
            label = o.optString("label"),
            startMin = o.optInt("start"),
            endMin = o.optInt("end"),
            downBps = o.optLong("down"),
            upBps = o.optLong("up"),
            enabled = o.optBoolean("enabled", true)
        )
    }

    fun activeAt(minutesOfDay: Int): Boolean {
        return if (startMin <= endMin) minutesOfDay in startMin until endMin
        else minutesOfDay >= startMin || minutesOfDay < endMin
    }
}

/** A stored configuration bundle (DNS + limits + settings). */
data class Profile(
    val id: String,
    val name: String,
    val builtin: Boolean,
    val dnsId: String = "",
    val rules: List<LimitRule> = emptyList(),
    val quickDnsId: String = ""
) {
    fun toJson(): org.json.JSONObject = org.json.JSONObject().apply {
        put("id", id); put("name", name); put("builtin", builtin)
        put("dnsId", dnsId); put("quickDnsId", quickDnsId)
        val arr = org.json.JSONArray(); rules.forEach { arr.put(it.toJson()) }
        put("rules", arr)
    }

    companion object {
        fun fromJson(o: org.json.JSONObject): Profile {
            val arr = o.optJSONArray("rules") ?: org.json.JSONArray()
            val rules = buildList { for (i in 0 until arr.length()) add(LimitRule.fromJson(arr.getJSONObject(i))) }
            return Profile(
                id = o.optString("id"),
                name = o.optString("name"),
                builtin = o.optBoolean("builtin"),
                dnsId = o.optString("dnsId"),
                rules = rules,
                quickDnsId = o.optString("quickDnsId")
            )
        }
    }
}

/** Connection-history event. */
data class HistEvent(
    val ts: Long,
    val type: String,   // dns_changed, dns_restored, limit_on, limit_off, blocked, unblocked,
    // profile_applied, net_reset, vpn_start, vpn_stop, pc_paired, pc_disconnected
    val info: String = ""
) {
    fun toJson(): org.json.JSONObject =
        org.json.JSONObject().apply { put("ts", ts); put("type", type); put("info", info) }

    companion object {
        fun fromJson(o: org.json.JSONObject) = HistEvent(
            ts = o.optLong("ts"), type = o.optString("type"), info = o.optString("info")
        )
    }
}

/** One day of traffic totals (device-wide, from TrafficStats). */
data class DayUsage(
    val day: String,   // yyyy-MM-dd
    val rx: Long,
    val tx: Long,
    val topPkg: String = "",
    val topRx: Long = 0L,
    val topTx: Long = 0L
) {
    val total: Long get() = rx + tx
}

/** Per-app usage row shown on the Per-App page. */
data class AppUsage(
    val pkg: String,
    val label: String,
    val uid: Int,
    val rxTotal: Long,
    val txTotal: Long,
    var rxRate: Long = 0L,
    var txRate: Long = 0L
) {
    val total: Long get() = rxTotal + txTotal
}

/** Live link/network facts used by the dashboard, health score and adapter page. */
data class LinkState(
    val connected: Boolean = false,
    val transport: String = "",        // WIFI / CELLULAR / ETHERNET / VPN / NONE
    val typeName: String = "",
    val rxRate: Long = 0L,
    val txRate: Long = 0L,
    val dnsServers: List<String> = emptyList(),
    val localIp: String = "",
    val gateway: String = "",
    val linkSpeedMbps: Int = 0,
    val vpnActive: Boolean = false
)

/** Network health breakdown — every field comes from a real measurement. */
data class HealthScore(
    val score: Int,
    val dnsMs: Int,
    val dnsLabel: String = "",
    val latencyMs: Int,
    val latencyLabel: String = "",
    val lossPct: Double,
    val lossLabel: String = "",
    val stableLabel: String = "",
    val sampleCount: Int = 0
)
