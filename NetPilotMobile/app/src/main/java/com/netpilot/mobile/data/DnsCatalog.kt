package com.netpilot.mobile.data

import androidx.compose.runtime.mutableStateOf
import org.json.JSONArray

/**
 * Built-in resolver catalog + the user's custom entries, favourites and selection.
 * Mirrors the desktop DNS Manager so the same providers appear on both platforms.
 */
class DnsCatalog(private val store: Store) {

    val presets: List<DnsEntry> = BUILTINS

    // ------------------------------------------------------------------ custom entries

    fun custom(): List<DnsEntry> = runCatching {
        val arr = store.getArray(Store.D_DNS, "custom")
        buildList { for (i in 0 until arr.length()) add(DnsEntry.fromJson(arr.getJSONObject(i))) }
    }.getOrDefault(emptyList())

    fun all(): List<DnsEntry> = presets + custom()

    fun find(id: String): DnsEntry? = all().firstOrNull { it.id == id }

    fun byServer(ip: String): DnsEntry? =
        presets.firstOrNull { it.primary == ip || it.secondary == ip }

    fun addCustom(name: String, primary: String, secondary: String) {
        val id = "custom_${System.currentTimeMillis()}"
        val entry = DnsEntry(
            id = id, name = name, primary = primary, secondary = secondary, custom = true
        )
        store.edit(Store.D_DNS) { d ->
            val arr = d.optJSONArray("custom") ?: JSONArray()
            arr.put(entry.toJson())
            d.put("custom", arr)
        }
    }

    fun removeCustom(id: String) {
        store.edit(Store.D_DNS) { d ->
            val arr = d.optJSONArray("custom") ?: JSONArray()
            val next = JSONArray()
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                if (o.optString("id") != id) next.put(o)
            }
            d.put("custom", next)
            // Deleting the entry that was in use left "selected" pointing at an id that no
            // longer exists, so the DNS screen showed one resolver while the tunnel quietly
            // ran another. Drop the selection with the entry.
            if (d.optString("selected") == id) d.put("selected", "")
        }
        if (selectedId == id) selectedId = ""
    }

    // ------------------------------------------------------------------ favourites

    fun favorites(): Set<String> = runCatching {
        val arr = store.getArray(Store.D_DNS, "fav")
        buildSet { for (i in 0 until arr.length()) add(arr.getString(i)) }
    }.getOrDefault(emptySet())

    fun toggleFavorite(id: String) {
        store.edit(Store.D_DNS) { d ->
            val arr = d.optJSONArray("fav") ?: JSONArray()
            val next = JSONArray()
            val wanted = id
            var present = false
            for (i in 0 until arr.length()) {
                val v = arr.getString(i)
                if (v == wanted) present = true else next.put(v)
            }
            if (!present) next.put(wanted)
            d.put("fav", next)
        }
    }

    // ------------------------------------------------------------------ selection

    /**
     * Currently applied DNS entry id ("" = system default / tunnel stopped).
     *
     * The store stays the source of truth, but the bump is what Compose subscribes to:
     * without it every screen showing "Current DNS" kept the old provider until something
     * else happened to redraw (profiles header, smart-DNS, …).
     */
    private val selectionVersion = mutableStateOf(0)

    var selectedId: String
        get() {
            selectionVersion.value   // subscribe: read first, then the stored value
            return store.getString(Store.D_DNS, "selected", "")
        }
        private set(value) {
            store.putString(Store.D_DNS, "selected", value)
            selectionVersion.value++
        }

    /** Saved system DNS so "Restore" can go back to it. */
    var previousIds: List<String>
        get() = runCatching {
            val arr = store.getArray(Store.D_DNS, "prev")
            buildList { for (i in 0 until arr.length()) add(arr.getString(i)) }
        }.getOrDefault(emptyList())
        private set(value) = store.edit(Store.D_DNS) { d ->
            val arr = JSONArray(); value.forEach { arr.put(it) }; d.put("prev", arr)
        }

    fun markSelected(id: String) {
        selectedId = id
    }

    fun rememberSystemDns(servers: List<String>) {
        if (servers.isEmpty()) return
        if (previousIds.isEmpty()) previousIds = servers
    }

    fun clearSelection() {
        selectedId = ""
    }

    companion object {
        val BUILTINS: List<DnsEntry> = listOf(
            DnsEntry("cloudflare", "Cloudflare", "1.1.1.1", "1.0.0.1",
                tagEn = "Fastest · no logs", tagFa = "سریع‌ترین · بدون ثبت"),
            DnsEntry("cloudflare_security", "Cloudflare Security", "1.1.1.2", "1.0.0.2",
                tagEn = "Blocks malware", tagFa = "مسدودسازی بدافزار"),
            DnsEntry("cloudflare_family", "Cloudflare Family", "1.1.1.3", "1.0.0.3",
                tagEn = "Malware + adult", tagFa = "بدافزار + محتوای بزرگسال", family = true),
            DnsEntry("google", "Google", "8.8.8.8", "8.8.4.4",
                tagEn = "Worldwide anycast", tagFa = "آنی‌کست جهانی"),
            DnsEntry("quad9", "Quad9", "9.9.9.9", "149.112.112.112",
                tagEn = "Blocks threats", tagFa = "مسدودسازی تهدیدات"),
            DnsEntry("quad9_unsec", "Quad9 Unsecured", "9.9.9.10", "149.112.112.10",
                tagEn = "No blocking", tagFa = "بدون فیلتر"),
            DnsEntry("adguard", "AdGuard DNS", "94.140.14.14", "94.140.15.15",
                tagEn = "Ads & trackers", tagFa = "تبلیغات و ردیاب‌ها"),
            DnsEntry("adguard_family", "AdGuard Family", "94.140.14.140", "94.140.15.140",
                tagEn = "Family protection", tagFa = "محافظت خانواده", family = true),
            DnsEntry("adguard_unfiltered", "AdGuard Unfiltered", "94.140.14.142", "94.140.15.142",
                tagEn = "No filtering", tagFa = "بدون فیلتر"),
            DnsEntry("opendns", "OpenDNS", "208.67.222.222", "208.67.220.220",
                tagEn = "Cisco uptime", tagFa = "پایداری سیسکو"),
            DnsEntry("opendns_family", "OpenDNS Family", "208.67.222.123", "208.67.220.123",
                tagEn = "Family filter", tagFa = "فیلتر خانواده", family = true),
            DnsEntry("cleansing_security", "CleanBrowsing", "185.228.168.9", "185.228.169.9",
                tagEn = "Security filter", tagFa = "فیلتر امنیتی"),
            DnsEntry("cleansing_family", "CleanBrowsing Family", "185.228.168.168", "185.228.169.168",
                tagEn = "Family filter", tagFa = "فیلتر خانواده", family = true),
            DnsEntry("controld", "ControlD", "76.76.2.0", "76.76.10.0",
                tagEn = "Unfiltered", tagFa = "بدون فیلتر"),
            DnsEntry("nextdns", "NextDNS", "45.90.28.0", "45.90.30.0",
                tagEn = "Configurable", tagFa = "قابل پیکربندی"),
            DnsEntry("mullvad", "Mullvad DNS", "194.242.2.2", "2a07:e340::2",
                tagEn = "Privacy first", tagFa = "حریم خصوصی"),
            DnsEntry("comodo", "Comodo Secure", "8.26.56.26", "8.20.247.20",
                tagEn = "Secure DNS", tagFa = "DNS امن"),
            DnsEntry("level3", "Level3", "4.2.2.1", "4.2.2.2",
                tagEn = "Legacy stable", tagFa = "کلاسیک و پایدار"),
            DnsEntry("alibaba", "AliDNS", "223.5.5.5", "223.6.6.6",
                tagEn = "Asia optimized", tagFa = "بهینه برای آسیا"),
            DnsEntry("dnspod", "DNSPod", "119.29.29.29", "182.254.116.116",
                tagEn = "CN anycast", tagFa = "آنیکست چین"),
            DnsEntry("yandex", "Yandex", "77.88.8.8", "77.88.8.1",
                tagEn = "Basic protection", tagFa = "محافظت پایه")
        )
    }
}
