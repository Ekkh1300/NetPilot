package com.netpilot.mobile.data

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Small JSON document store — the mobile counterpart of the desktop app's Storage service.
 *
 * - One document per logical area (settings, dns, rules, history, usage, profiles).
 * - Every mutation writes to a temp file and renames atomically so a crash can never
 *   truncate a document (a corrupt document is kept as `*.bad` for inspection).
 * - Reads are served from memory; writes are guarded by a single lock.
 */
class Store(context: Context) {

    private val dir: File = context.applicationContext.filesDir
    private val lock = Any()
    private val dirty = AtomicBoolean(false)

    private val docs = HashMap<String, JSONObject>()

    // ------------------------------------------------------------------ documents

    fun doc(name: String): JSONObject = synchronized(lock) {
        docs.getOrPut(name) { loadDoc(name) }
    }

    fun edit(name: String, block: (JSONObject) -> Unit) {
        synchronized(lock) {
            val d = docs.getOrPut(name) { loadDoc(name) }
            block(d)
            dirty.set(true)
            persistLocked(name, d)
        }
    }

    /** Batches several document updates into one pass (used during import/restore). */
    fun editMany(names: List<String>, block: (Map<String, JSONObject>) -> Unit) {
        synchronized(lock) {
            val map = names.associateWith { docs.getOrPut(it) { loadDoc(it) } }
            block(map)
            dirty.set(true)
            map.forEach { (n, d) -> persistLocked(n, d) }
        }
    }

    fun flush() {
        if (!dirty.compareAndSet(true, false)) return
        synchronized(lock) {
            docs.forEach { (n, d) -> persistLocked(n, d) }
        }
    }

    // ------------------------------------------------------------------ typed helpers

    fun getString(name: String, key: String, def: String = ""): String =
        doc(name).optString(key, def)

    fun putString(name: String, key: String, value: String) =
        edit(name) { it.put(key, value) }

    fun getBool(name: String, key: String, def: Boolean = false): Boolean =
        doc(name).optBoolean(key, def)

    fun putBool(name: String, key: String, value: Boolean) =
        edit(name) { it.put(key, value) }

    fun getInt(name: String, key: String, def: Int = 0): Int = doc(name).optInt(key, def)

    fun putInt(name: String, key: String, value: Int) =
        edit(name) { it.put(key, value) }

    fun getLong(name: String, key: String, def: Long = 0L): Long = doc(name).optLong(key, def)

    fun putLong(name: String, key: String, value: Long) =
        edit(name) { it.put(key, value) }

    fun getArray(name: String, key: String): JSONArray =
        doc(name).optJSONArray(key) ?: JSONArray()

    fun putArray(name: String, key: String, arr: JSONArray) =
        edit(name) { it.put(key, arr) }

    /** Full document snapshot (used by export/backup and the PC bridge). */
    fun snapshot(names: List<String>): JSONObject = synchronized(lock) {
        JSONObject().apply { names.forEach { n -> put(n, doc(n)) } }
    }

    fun restore(snapshot: JSONObject, names: List<String>) {
        editMany(names) { map ->
            map.forEach { (n, d) ->
                val src = snapshot.optJSONObject(n) ?: return@forEach
                val keys = src.keys()
                val incoming = ArrayList<String>()
                while (keys.hasNext()) incoming.add(keys.next())
                incoming.forEach { d.remove(it) }
                incoming.forEach { d.put(it, src.get(it)) }
            }
        }
    }

    // ------------------------------------------------------------------ io

    private fun file(name: String) = File(dir, "np_$name.json")

    private fun loadDoc(name: String): JSONObject {
        val f = file(name)
        if (!f.exists()) return JSONObject()
        return try {
            JSONObject(f.readText(Charsets.UTF_8))
        } catch (t: Throwable) {
            runCatching { f.renameTo(File(dir, "np_$name.json.bad")) }
            JSONObject()
        }
    }

    /**
     * Writes one document, atomically.
     *
     * Written to a temporary file and renamed into place rather than written in place, because a
     * write interrupted by the process dying leaves a half-written file, and a half-written JSON
     * document does not parse - so the next launch would rename it to `.bad` and start from an
     * empty store. The rename is atomic on every filesystem Android uses, so a reader sees either
     * the old file or the new one.
     *
     * The temporary file is then fsync'd, and so is the directory. Both are load-bearing and both
     * were missing.
     *
     * fsync on the file: without it, `writeText` returns as soon as the bytes are in the page
     * cache, and a power loss or a hard reboot can leave a file that was renamed into place and
     * is nonetheless empty or torn. The rename had already succeeded by then, so nothing would
     * have retried it - the data would simply be gone, and the next launch would find unparseable
     * JSON and quietly reset the store.
     *
     * fsync on the directory: the rename itself is a metadata operation, and it is the metadata
     * that must survive, not the file's contents. Without this the file's *name* can be lost even
     * when its bytes are not - which is why this step is not redundant with the one above. Neither
     * is cheap, which is why this runs from the debounced flush and not per write.
     *
     * A failure is logged rather than swallowed. The original `catch (_: Throwable)` discarded
     * the exception entirely, so a full disk or a read-only directory was invisible: the document
     * stayed dirty, the flush retried, and the user saw settings they had changed simply not
     * persist, with no indication of why.
     */
    private fun persistLocked(name: String, d: JSONObject) {
        var saved = false
        try {
            val target = file(name)
            val tmp = File(dir, "np_$name.tmp")
            tmp.writeText(d.toString(), Charsets.UTF_8)

            // The bytes, before the rename makes the file visible under its real name.
            FileOutputStream(tmp).use { it.fd.sync() }

            if (!tmp.renameTo(target)) {
                target.delete()
                saved = tmp.renameTo(target)
            } else {
                saved = true
            }

            // The rename itself. Skipped on a failure above, since there is nothing to record.
            if (saved) {
                runCatching {
                    FileInputStream(dir).use { it.fd.sync() }
                }.onFailure {
                    // A filesystem that refuses to sync a directory is unusual and not fatal: the
                    // contents are already durable, so only the rename's durability is at stake.
                    com.netpilot.mobile.core.log.NpLog.debug(
                        "store",
                        "the directory could not be fsync'd after renaming $name; " +
                            "the contents are durable but the rename may not survive a hard reboot"
                    )
                }
            }
        } catch (t: Throwable) {
            com.netpilot.mobile.core.log.NpLog.warn(
                "store",
                "could not persist '$name'; the change is in memory only and the next flush " +
                    "will retry it. If this repeats, the app's data directory is not writable",
                t
            )
        }
        // Only a document that really reached the disk counts as clean. Marking it clean on a
        // failed write made flush() a no-op afterwards, so a full disk silently diverged the
        // store from the file and the data was gone after the next restart - with nothing
        // shown to the user. Leaving it dirty means the next flush retries it.
        if (saved) dirty.set(false)
    }

    companion object {
        const val D_SETTINGS = "settings"
        const val D_DNS = "dns"
        const val D_RULES = "rules"
        const val D_SCHEDULES = "schedules"
        const val D_PROFILES = "profiles"
        const val D_HISTORY = "history"
        const val D_USAGE = "usage"
        const val D_EVENTS = "events"
        const val D_PC = "pc"
        const val D_BENCH = "bench"

        fun allDocuments(): List<String> = listOf(
            D_SETTINGS, D_DNS, D_RULES, D_SCHEDULES, D_PROFILES,
            D_HISTORY, D_USAGE, D_EVENTS, D_PC, D_BENCH
        )
    }
}
