package com.netpilot.mobile.data

import com.netpilot.mobile.core.AppGraph
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import java.util.UUID

/**
 * State-backed repositories over [Store]. Every mutation updates both the in-memory
 * StateFlow (so Compose recomposes immediately) and the JSON document on disk.
 */
object Repo {

    // ------------------------------------------------------------------ settings

    private val _language = MutableStateFlow("en")
    val language: StateFlow<String> = _language.asStateFlow()

    private val _settingsVersion = MutableStateFlow(0)
    val settingsVersion: StateFlow<Int> = _settingsVersion.asStateFlow()

    fun loadLanguage(): String {
        val v = AppGraph.store.getString(Store.D_SETTINGS, "lang", "en")
        _language.value = v
        Strings.setLanguage(v)
        return v
    }

    fun setLanguage(code: String) {
        val v = if (code == "fa") "fa" else "en"
        _language.value = v
        Strings.setLanguage(v)
        AppGraph.store.putString(Store.D_SETTINGS, "lang", v)
        _settingsVersion.value++
    }

    fun monitorInterval(): Long = AppGraph.store.getLong(Store.D_SETTINGS, "interval", 1000L)

    fun setMonitorInterval(ms: Long) {
        AppGraph.store.putLong(Store.D_SETTINGS, "interval", ms)
        _settingsVersion.value++
    }

    fun activeProfileId(): String = AppGraph.store.getString(Store.D_SETTINGS, "profile", "default")

    fun setActiveProfile(id: String) {
        AppGraph.store.putString(Store.D_SETTINGS, "profile", id)
        _settingsVersion.value++
    }

    // ------------------------------------------------------------------ rules

    private val _rules = MutableStateFlow(loadRules())
    val rules: StateFlow<List<LimitRule>> = _rules.asStateFlow()

    private fun loadRules(): List<LimitRule> = runCatching {
        val arr = AppGraph.store.getArray(Store.D_RULES, "list")
        buildList { for (i in 0 until arr.length()) add(LimitRule.fromJson(arr.getJSONObject(i))) }
    }.getOrDefault(emptyList())

    private fun persistRules() {
        val arr = JSONArray()
        _rules.value.forEach { arr.put(it.toJson()) }
        AppGraph.store.putArray(Store.D_RULES, "list", arr)
    }

    fun setRule(rule: LimitRule) {
        _rules.value = _rules.value.filterNot { it.pkg == rule.pkg } + rule
        persistRules()
    }

    /**
     * Replaces the whole limit set - what applying a profile means.
     *
     * The upsert in [setRule] on its own never removed anything, so switching to a profile
     * that carries no limits (the built-in "Default" has an empty list) left every previous
     * rule in force: the screen claimed the profile was applied while the tunnel still
     * enforced limits the user thought they had just dropped.
     */
    fun replaceRules(rules: List<LimitRule>) {
        _rules.value = rules
        persistRules()
    }

    fun removeRule(pkg: String) {
        _rules.value = _rules.value.filterNot { it.pkg == pkg }
        persistRules()
    }

    fun ruleFor(pkg: String): LimitRule? = _rules.value.firstOrNull { it.pkg == pkg }

    fun activeRules(): List<LimitRule> = _rules.value.filter { it.mode != RuleMode.ALLOW }

    // ------------------------------------------------------------------ schedules

    private val _schedules = MutableStateFlow(loadSchedules())
    val schedules: StateFlow<List<ScheduleRule>> = _schedules.asStateFlow()

    private fun loadSchedules(): List<ScheduleRule> = runCatching {
        val arr = AppGraph.store.getArray(Store.D_SCHEDULES, "list")
        buildList {
            for (i in 0 until arr.length()) add(ScheduleRule.fromJson(arr.getJSONObject(i)))
        }
    }.getOrDefault(emptyList())

    private fun persistSchedules() {
        val arr = JSONArray()
        _schedules.value.forEach { arr.put(it.toJson()) }
        AppGraph.store.putArray(Store.D_SCHEDULES, "list", arr)
    }

    fun addSchedule(rule: ScheduleRule) {
        _schedules.value = _schedules.value + rule.copy(
            id = if (rule.id.isBlank()) UUID.randomUUID().toString() else rule.id
        )
        persistSchedules()
    }

    fun updateSchedule(rule: ScheduleRule) {
        _schedules.value = _schedules.value.map { if (it.id == rule.id) rule else it }
        persistSchedules()
    }

    fun removeSchedule(id: String) {
        _schedules.value = _schedules.value.filterNot { it.id == id }
        persistSchedules()
    }

    fun toggleSchedule(id: String, enabled: Boolean) {
        _schedules.value = _schedules.value.map {
            if (it.id == id) it.copy(enabled = enabled) else it
        }
        persistSchedules()
    }

    /** Limits that are in force right now (base rule overridden by an active schedule). */
    fun effectiveRules(nowMin: Int = minutesOfDay()): Map<String, LimitRule> {
        val map = HashMap<String, LimitRule>()
        for (r in _rules.value) map[r.pkg] = r
        for (s in _schedules.value) {
            if (!s.enabled || !s.activeAt(nowMin)) continue
            val base = map[s.pkg]
            map[s.pkg] = LimitRule(
                pkg = s.pkg,
                label = s.label.ifBlank { base?.label ?: s.pkg },
                mode = if (s.downBps > 0 || s.upBps > 0) RuleMode.LIMIT else RuleMode.BLOCK,
                downBps = if (s.downBps > 0) s.downBps else base?.downBps ?: 0L,
                upBps = if (s.upBps > 0) s.upBps else base?.upBps ?: 0L
            )
        }
        return map
    }

    // ------------------------------------------------------------------ profiles

    private val _profiles = MutableStateFlow(loadProfiles())
    val profiles: StateFlow<List<Profile>> = _profiles.asStateFlow()

    private fun builtinProfiles(): List<Profile> = listOf(
        Profile(
            id = "default", name = "Default", builtin = true,
            dnsId = "cloudflare", rules = emptyList()
        ),
        Profile(
            id = "browsing", name = "Browsing", builtin = true,
            dnsId = "cloudflare",
            rules = emptyList()
        ),
        Profile(
            id = "gaming", name = "Gaming", builtin = true,
            dnsId = "quad9",
            rules = emptyList()
        )
    )

    private fun loadProfiles(): List<Profile> {
        val saved = runCatching {
            val arr = AppGraph.store.getArray(Store.D_PROFILES, "list")
            buildList {
                for (i in 0 until arr.length()) add(Profile.fromJson(arr.getJSONObject(i)))
            }
        }.getOrDefault(emptyList())
        // Built-ins are always present; user edits to them are kept.
        val byId = LinkedHashMap<String, Profile>()
        builtinProfiles().forEach { byId[it.id] = it }
        saved.forEach { byId[it.id] = it }
        return byId.values.toList()
    }

    private fun persistProfiles() {
        val arr = JSONArray()
        _profiles.value.forEach { arr.put(it.toJson()) }
        AppGraph.store.putArray(Store.D_PROFILES, "list", arr)
    }

    fun profile(id: String): Profile? = _profiles.value.firstOrNull { it.id == id }

    fun saveProfile(profile: Profile) {
        val list = _profiles.value
        _profiles.value = if (list.any { it.id == profile.id }) {
            list.map { if (it.id == profile.id) profile else it }
        } else {
            list + profile
        }
        persistProfiles()
    }

    fun addProfile(name: String): Profile {
        val p = Profile(
            id = "p_" + UUID.randomUUID().toString().substring(0, 8),
            name = name,
            builtin = false,
            dnsId = AppGraph.dnsCatalog.selectedId,
            rules = _rules.value
        )
        _profiles.value = _profiles.value + p
        persistProfiles()
        return p
    }

    fun deleteProfile(id: String) {
        val p = profile(id) ?: return
        if (p.builtin) return
        _profiles.value = _profiles.value.filterNot { it.id == id }
        persistProfiles()
    }

    /** Snapshot the current DNS + rules into the given profile. */
    fun captureInto(id: String) {
        val p = profile(id) ?: return
        saveProfile(
            p.copy(
                dnsId = AppGraph.dnsCatalog.selectedId.ifBlank { p.dnsId },
                rules = _rules.value
            )
        )
    }

    // ------------------------------------------------------------------ events

    private val _events = MutableStateFlow(loadEvents())
    val events: StateFlow<List<HistEvent>> = _events.asStateFlow()

    private fun loadEvents(): List<HistEvent> = runCatching {
        val arr = AppGraph.store.getArray(Store.D_EVENTS, "list")
        buildList { for (i in 0 until arr.length()) add(HistEvent.fromJson(arr.getJSONObject(i))) }
    }.getOrDefault(emptyList())

    fun log(type: String, info: String = "") {
        val ev = HistEvent(ts = System.currentTimeMillis(), type = type, info = info)
        val list = (listOf(ev) + _events.value).take(MAX_EVENTS)
        _events.value = list
        runCatching {
            val arr = JSONArray()
            list.forEach { arr.put(it.toJson()) }
            AppGraph.store.putArray(Store.D_EVENTS, "list", arr)
        }
    }

    /** Drop the audit trail (kept separate from [clearAll] so nothing else is touched). */
    fun clearEvents() {
        _events.value = emptyList()
        AppGraph.store.putArray(Store.D_EVENTS, "list", JSONArray())
    }

    // ------------------------------------------------------------------ usage history

    private val _days = MutableStateFlow(loadDays())
    val days: StateFlow<Map<String, DayUsage>> = _days.asStateFlow()

    private fun loadDays(): Map<String, DayUsage> = runCatching {
        val obj = AppGraph.store.doc(Store.D_USAGE)
        val out = LinkedHashMap<String, DayUsage>()
        for (key in obj.keys()) {
            if (key == "today") continue
            val o = obj.optJSONObject(key) ?: continue
            out[key] = DayUsage(
                day = key,
                rx = o.optLong("rx"),
                tx = o.optLong("tx"),
                topPkg = o.optString("topPkg"),
                topRx = o.optLong("topRx"),
                topTx = o.optLong("topTx")
            )
        }
        out
    }.getOrDefault(emptyMap())

    /** Record a delta of device-wide traffic against today's bucket. */
    fun recordUsage(dRx: Long, dTx: Long, topPkg: String = "", topRx: Long = 0, topTx: Long = 0) {
        if (dRx <= 0 && dTx <= 0) return
        val day = dayKey(System.currentTimeMillis())
        AppGraph.store.edit(Store.D_USAGE) { doc ->
            val o = doc.optJSONObject(day) ?: JSONObject()
            o.put("rx", o.optLong("rx") + dRx)
            o.put("tx", o.optLong("tx") + dTx)
            if (topPkg.isNotBlank() && (topRx + topTx) > 0) {
                val prevTop = o.optLong("topRx") + o.optLong("topTx")
                if (topRx + topTx >= prevTop) {
                    o.put("topPkg", topPkg)
                    o.put("topRx", topRx)
                    o.put("topTx", topTx)
                }
            }
            doc.put(day, o)
        }
        _days.value = loadDays()
    }

    /**
     * Writes today's bucket as **absolute** numbers: NetworkStats is queried over the whole
     * day, so both the total and the top app describe the same window. [recordUsage] stays
     * for the fallback path where only a running monitor can see the traffic.
     */
    fun setUsage(dayRx: Long, dayTx: Long, topPkg: String = "", topRx: Long = 0, topTx: Long = 0) {
        if (dayRx <= 0 && dayTx <= 0) return
        val day = dayKey(System.currentTimeMillis())
        AppGraph.store.edit(Store.D_USAGE) { doc ->
            val o = doc.optJSONObject(day) ?: JSONObject()
            o.put("rx", dayRx)
            o.put("tx", dayTx)
            if (topPkg.isNotBlank() && (topRx + topTx) > 0) {
                o.put("topPkg", topPkg)
                o.put("topRx", topRx)
                o.put("topTx", topTx)
            }
            doc.put(day, o)
        }
        _days.value = loadDays()
    }

    fun clearAll() {
        runCatching {
            AppGraph.store.editMany(Store.allDocuments()) { map ->
                map.values.forEach { doc ->
                    val keys = ArrayList<String>()
                    doc.keys().forEachRemaining { keys.add(it) }
                    keys.forEach { doc.remove(it) }
                }
            }
        }
        _rules.value = emptyList()
        _schedules.value = emptyList()
        _events.value = emptyList()
        _days.value = emptyMap()
        _profiles.value = builtinProfiles()
        persistRules(); persistSchedules(); persistProfiles()
    }

    /**
     * Re-reads every document the UI is bound to.
     *
     * Import writes straight to the store. Without this the app kept serving the pre-import
     * lists from memory: the import reported success, the screens still showed the old rules
     * and the tunnel enforced them, and the next edit serialised the stale list straight over
     * the imported data - the import was silently undone.
     */
    fun reloadAll() {
        _rules.value = loadRules()
        _schedules.value = loadSchedules()
        _profiles.value = loadProfiles()
        _events.value = loadEvents()
        _days.value = loadDays()
    }

    fun dayKey(ts: Long): String = DAY_FMT.format(Date(ts))

    private val DAY_FMT = SimpleDateFormat("yyyy-MM-dd", Locale.US)

    fun minutesOfDay(): Int = Calendar.getInstance().get(Calendar.HOUR_OF_DAY) * 60 +
            Calendar.getInstance().get(Calendar.MINUTE)

    const val MAX_EVENTS = 400
}
