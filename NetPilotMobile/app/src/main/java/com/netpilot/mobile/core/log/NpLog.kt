package com.netpilot.mobile.core.log

import android.util.Log as AndroidLog
import java.io.File
import java.io.BufferedWriter
import java.io.OutputStreamWriter
import java.io.FileOutputStream
import java.text.SimpleDateFormat
import java.util.ArrayDeque
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/** How much an entry matters, and therefore whether it is written at all. */
enum class LogLevel(val order: Int, val tag: String) {
    /** Far too chatty to ship enabled: a per-packet trace on a phone is both a battery cost
     *  and a file nobody can read. Off by default. */
    TRACE(0, "TRC"),

    /** Why a decision was made - which resolver won, which link was picked. */
    DEBUG(1, "DBG"),

    /** Normal operation: started, stopped, paired, rule applied. */
    INFO(2, "INF"),

    /** Something unexpected that was recovered from. */
    WARN(3, "WRN"),

    /** A failure the user will notice. */
    ERROR(4, "ERR"),

    /** The app cannot continue. */
    FATAL(5, "FTL");

    companion object {
        fun parse(text: String?): LogLevel? {
            if (text.isNullOrBlank()) return null
            return entries.firstOrNull { it.name.equals(text.trim(), ignoreCase = true) }
        }
    }
}

/** One line in the log. Immutable, so it can be handed to a sink without a copy. */
class LogEntry(
    val timestamp: Long,
    val level: LogLevel,
    val category: String,
    val message: String,
    val detail: String? = null,
) {
    /** One line, fixed layout, so the file stays readable with grep. */
    fun toLine(): String {
        val head = buildString {
            append(STAMP.format(Date(timestamp)))
            append(' ').append(level.tag)
            append(" [").append(category).append(']')
            append(' ').append(message.replace('\n', '\u2028').replace('\r', ' '))
        }
        return if (detail.isNullOrEmpty()) head else head + "\n" + detail
    }

    companion object {
        /** ISO 8601 to the millisecond, UTC. A log is evidence: "01:15" twice a year is not a
         *  timestamp, and a phone in another time zone has to be comparable with this one. */
        private val STAMP = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS'Z'", Locale.US).apply {
            timeZone = TimeZone.getTimeZone("UTC")
        }
        @Synchronized fun stamp(t: Long): String = STAMP.format(Date(t))
    }
}

/**
 * The logger.
 *
 * A separate implementation from NetPilot.Core.Logging because Kotlin on Android cannot share
 * a C# assembly, but with the same rules on purpose: same levels, same categories, same
 * redaction, same rotation, same never-throw guarantee. A log copied from the phone to a bug
 * report must mean the same thing as one from the desktop, and the two writers agreeing is what
 * makes that true.
 *
 * Three properties, in priority order:
 *
 *  1. **It never throws.** A logger that throws takes the app down, and the moment you most
 *     need the log is the moment the disk is full.
 *  2. **It never blocks the caller.** Entries go on a bounded queue drained by one thread.
 *     When it is full - a slow card, a log viewer holding the file - entries are dropped and
 *     *counted*, never blocked on. Dropping a line is invisible; blocking the UI thread is a
 *     hang the user feels.
 *  3. **Nothing sensitive is written.** [Redactor] runs before any sink sees the text.
 */
object NpLog {

    private const val TAG = "NetPilot"

    /** Bounded. 2048 entries is a few megabytes at worst, and far more than a phone session
     *  produces before the writer catches up. */
    private const val MAX_QUEUE = 2048

    private val queue = LinkedBlockingQueue<LogEntry>(MAX_QUEUE)

    private val dropped = AtomicLong(0)
    private val written = AtomicLong(0)
    private val writeFailures = AtomicLong(0)

    @Volatile var minimum: LogLevel = LogLevel.INFO
        private set

    /** Per-category overrides, e.g. "tunnel" at TRACE while everything else stays at INFO.
     *  A category override *replaces* the global threshold, in both directions. */
    private val categoryLevels = HashMap<String, LogLevel>()

    @Volatile private var logFile: File? = null

    /** Held open rather than reopened per line; see [appendToFile]. Guarded by `this`. */
    private var writer: BufferedWriter? = null
    private var lastFlushAt = 0L

    /** Bytes currently in the live file, tracked in memory rather than asked for. See
     *  [appendToFile] for why that matters. */
    private var bytesOnDisk = 0L

    /** How stale the file may get before it is flushed. 250 ms is short enough that a crash
     *  report has the lines around the failure, and long enough that a burst of thousands of
     *  entries costs a few flushes rather than one per line. */
    private const val FLUSH_INTERVAL_MS = 250L
    private var maxBytes = 2 * 1024 * 1024
    private var keepFiles = 3

    /** Newest-first, bounded. What the app's diagnostics screen shows. */
    private val memory = ArrayDeque<LogEntry>()
    private const val MEMORY_CAPACITY = 1000

    @Volatile private var configured = false
    private val configuring = AtomicBoolean(false)
    private var worker: Thread? = null
    private val stopped = java.util.concurrent.atomic.AtomicBoolean(false)

    val droppedCount: Long get() = dropped.get()
    val writtenCount: Long get() = written.get()
    val writeFailureCount: Long get() = writeFailures.get()

    /** Entries still waiting for the writer. Shown next to "dropped", because a drop count on
     *  its own cannot say whether the sink or the producer was the bottleneck. */
    val queuedCount: Int get() = queue.size

    @Volatile var filePath: String? = null
        private set

    /**
     * Points the logger at a directory and starts the writer. Safe to call more than once -
     * a later call re-points the file rather than being ignored, because the first version
     * returned early and left logging dead for the life of the process after one failure.
     */
    @Synchronized
    fun configure(directory: File?, level: LogLevel = LogLevel.INFO) {
        if (configuring.getAndSet(true)) return
        try {
            minimum = level
            synchronized(categoryLevels) { categoryLevels.clear() }

            synchronized(this) { closeWriter() }
            logFile = if (directory == null) null else {
                try {
                    // The parent is created explicitly rather than relied on: on Android the
                    // app's files directory can be made by the framework, but a configured
                    // directory under it is ours to create.
                    directory.parentFile?.let { if (!it.exists()) it.mkdirs() }
                    if (!directory.exists()) directory.mkdirs()
                    File(directory, "netpilot.log")
                } catch (_: Throwable) {
                    null
                }
            }
            filePath = logFile?.absolutePath

            if (worker == null || worker?.isAlive != true) {
                stopped.set(false)
                worker = Thread({ drain() }, "NetPilot log").apply {
                    isDaemon = true
                    // Daemon: a stuck writer must not keep the process alive.
                    start()
                }
            }
            configured = true
            if (logFile == null) {
                info("log", "no writable log directory; entries are kept in memory only")
            }
        } catch (_: Throwable) {
            // Nothing left to do, and throwing here would be the worst possible outcome.
        } finally {
            configuring.set(false)
        }
    }

    fun setCategoryLevel(category: String, level: LogLevel) {
        synchronized(categoryLevels) { categoryLevels[category] = level }
    }

    fun isEnabled(level: LogLevel, category: String? = null): Boolean {
        // The category override replaces the global threshold in both directions. Checking
        // the global level first would mean a category can only ever be made quieter, so
        // "trace the tunnel while the app stays at INFO" would silently do nothing.
        synchronized(categoryLevels) {
            categoryLevels[category]?.let { return level.order >= it.order }
        }
        return level.order >= minimum.order
    }

    fun trace(category: String, message: String) = write(LogLevel.TRACE, category, message, null)
    fun debug(category: String, message: String) = write(LogLevel.DEBUG, category, message, null)
    fun info(category: String, message: String) = write(LogLevel.INFO, category, message, null)
    fun warn(category: String, message: String, t: Throwable? = null) =
        write(LogLevel.WARN, category, message, t)
    fun error(category: String, message: String, t: Throwable? = null) =
        write(LogLevel.ERROR, category, message, t)
    fun fatal(category: String, message: String, t: Throwable? = null) =
        write(LogLevel.FATAL, category, message, t)

    fun write(level: LogLevel, category: String, message: String, t: Throwable? = null) {
        try {
            if (!configured) {
                // A call from a unit test or a tool, before anything configured the logger.
                configure(null, LogLevel.WARN)
            }
            if (!isEnabled(level, category)) return

            val entry = LogEntry(
                System.currentTimeMillis(), level, category,
                Redactor.apply(message),
                if (t == null) null else Redactor.apply(t)
            )

            // offer(), never put(): put() on a full queue blocks, which is exactly the
            // failure this logger exists to avoid.
            if (!queue.offer(entry)) dropped.incrementAndGet()
            // Logcat mirrors Warn and above, and only those.
            //
            // Logcat is a live stream for things that need attention now; the file is the
            // record. Emitting Info to both doubled the per-entry cost for no benefit, and it
            // was the reason a 6000-entry burst lost half its entries: the writer thread
            // could not keep up, so the queue overflowed.
            //
            // It is also the only part that misbehaves under a JVM unit test, where
            // android.util.Log is not mocked and every call throws - caught, but the cost is
            // an exception and a stack trace per entry.
            try {
                if (level.order >= LogLevel.WARN.order) {
                    AndroidLog.e(TAG, "[$category] $message", t)
                }
            } catch (_: Throwable) {
                // Logcat is optional. Never let it take the entry down with it.
            }
        } catch (_: Throwable) {
            // Nothing left to do, and throwing from a logger is the worst outcome there is.
        }
    }

    private fun drain() {
        while (!stopped.get()) {
            val entry = try {
                queue.poll(200, java.util.concurrent.TimeUnit.MILLISECONDS)
            } catch (_: InterruptedException) {
                null
            } ?: continue

            try {
                synchronized(memory) {
                    memory.addLast(entry)
                    while (memory.size > MEMORY_CAPACITY) memory.removeFirst()
                }
                appendToFile(entry)
                written.incrementAndGet()
            } catch (_: Throwable) {
                writeFailures.incrementAndGet()
            }
        }
    }

    private fun appendToFile(entry: LogEntry) {
        val file = logFile ?: return
        synchronized(this) {
            val line = entry.toLine()

            // Rotate before writing, so a file never exceeds the cap.
            //
            // The size is the tracked count, not file.length(). Asking the filesystem on every
            // entry was measurably the entire cost of writing: one stat syscall per line, so a
            // few thousand entries could not be drained before the bounded queue overflowed and
            // began dropping them. The desktop build had the same bug and the same fix.
            if (bytesOnDisk > 0 && bytesOnDisk + line.length > maxBytes) {
                closeWriter()
                rotate(file)
                bytesOnDisk = 0L
            }
            try {
                var w = writer
                if (w == null) {
                    w = BufferedWriter(
                        OutputStreamWriter(FileOutputStream(file, true), Charsets.UTF_8)
                    )
                    writer = w
                    // Seeded once, when the handle is opened. A wrong starting count would
                    // only shift the rotation by one line, so it is not worth failing over.
                    bytesOnDisk = try { file.length() } catch (_: Throwable) { 0L }
                    // Zero, so the first entry into a fresh file is flushed at once. Leaving
                    // the previous file's timestamp here meant a lone entry written just
                    // after a rotation or a reconfigure sat in the buffer and never appeared -
                    // which is how five tests found an empty log file.
                    lastFlushAt = 0L
                }
                w.append(line).append("\n")
                bytesOnDisk += line.length + 1

                // Flush when the burst has gone quiet, not on a timer alone.
                //
                // A time-based check on the write path has a hole: if the last few entries
                // arrive inside one interval and nothing follows them, they sit in the buffer
                // for ever. Three tests found an empty log file that way - the entries were
                // counted as written, and nothing ever put them on disk. So: flush when there
                // is nothing left queued (the stream has gone quiet), when the entry is
                // urgent, or when the interval has passed - whichever comes first.
                val now = System.currentTimeMillis()
                val urgent = entry.level.order >= LogLevel.WARN.order
                val quiet = queue.isEmpty()
                if (urgent || quiet || now - lastFlushAt >= FLUSH_INTERVAL_MS) {
                    w.flush()
                    lastFlushAt = now
                }
            } catch (t: Throwable) {
                // The handle is probably stale after a rotation or a card swap. Drop it so the
                // next entry opens a fresh one instead of failing forever.
                closeWriter()
                throw t
            }
        }
    }

    private fun closeWriter() {
        try { writer?.flush(); writer?.close() } catch (_: Throwable) {}
        writer = null
    }

    private fun rotate(file: File) {
        // netpilot.log -> netpilot.1.log -> netpilot.2.log, oldest dropped.
        try { File(file.parentFile, "${file.nameWithoutExtension}.$keepFiles.log").delete() } catch (_: Throwable) {}
        for (i in keepFiles - 1 downTo 1) {
            try {
                val from = File(file.parentFile, "${file.nameWithoutExtension}.$i.log")
                if (from.exists()) {
                    val to = File(file.parentFile, "${file.nameWithoutExtension}.${i + 1}.log")
                    to.delete()
                    from.renameTo(to)
                }
            } catch (_: Throwable) {}
        }
        try {
            val first = File(file.parentFile, "${file.nameWithoutExtension}.1.log")
            first.delete()
            file.renameTo(first)
        } catch (_: Throwable) {}
    }

    /** Snapshot for the diagnostics screen: newest first, filtered. */
    fun recent(
        max: Int = 300,
        level: LogLevel = LogLevel.TRACE,
        categoryFilter: String? = null,
        textFilter: String? = null,
    ): List<LogEntry> {
        val out = ArrayList<LogEntry>()
        synchronized(memory) {
            val it = memory.descendingIterator()
            while (it.hasNext() && out.size < max) {
                val e = it.next()
                if (e.level.order < level.order) continue
                if (!categoryFilter.isNullOrBlank() &&
                    !e.category.equals(categoryFilter, ignoreCase = true)
                ) continue
                if (!textFilter.isNullOrBlank() &&
                    !e.message.contains(textFilter, ignoreCase = true)
                ) continue
                out.add(e)
            }
        }
        return out
    }

    /** Everything needed for a bug report: the log, the counters and the device. */
    fun diagnosticsReport(): String {
        val sb = StringBuilder(8192)
        sb.append("NetPilot Android diagnostics\n")
        sb.append("generated : ").append(LogEntry.stamp(System.currentTimeMillis())).append('\n')
        sb.append("android   : ").append(android.os.Build.VERSION.RELEASE)
            .append(" (API ").append(android.os.Build.VERSION.SDK_INT).append(")\n")
        sb.append("device    : ").append(android.os.Build.MANUFACTURER)
            .append(' ').append(android.os.Build.MODEL).append('\n')
        sb.append("log level : ").append(minimum).append('\n')
        sb.append("log file  : ").append(filePath ?: "(memory only)").append('\n')
        sb.append("written   : ").append(written.get()).append('\n')
        sb.append("dropped   : ").append(dropped.get()).append('\n')
        sb.append("queued    : ").append(queuedCount).append('\n')
        sb.append("write errs: ").append(writeFailures.get()).append('\n')
        sb.append('\n')

        val entries = recent(300)
        sb.append("---- last ").append(entries.size).append(" entries ----\n")
        entries.forEach {
            sb.append(it.toLine()).append('\n')
            if (!it.detail.isNullOrEmpty()) sb.append(it.detail)
        }
        return Redactor.apply(sb.toString())
    }

    /** Flushes and stops the writer. The queue is completed and replaced rather than reused,
     *  so a later [configure] still works - a logger that dies on shutdown is a logger that
     *  goes quiet on the next settings change. */
    fun shutdown(timeoutMs: Long = 2000) {
        if (!configured) return
        stopped.set(true)
        worker?.interrupt()
        synchronized(this) { closeWriter() }
        try { worker?.join(timeoutMs) } catch (_: InterruptedException) {}
        worker = null
        configured = false
        queue.clear()
    }

    /** Blocks until the writer has drained the queue, or the timeout expires. Used by the
     *  tests, and by a crash handler that needs the entry on disk before the process dies. */
    fun flush(timeoutMs: Long = 3000): Boolean {
        // A marker written by the same worker: seeing it on disk means everything queued
        // before it has been written.
        queue.offer(LogEntry(System.currentTimeMillis(), LogLevel.TRACE, "log", "__flush__", null))
        return try {
            // The flush marker is written by the same worker, so seeing it on disk means
            // everything queued before it has been written.
            val deadline = System.currentTimeMillis() + timeoutMs
            while (System.currentTimeMillis() < deadline) {
                val f = logFile
                if (f != null && f.exists() && f.readText().contains("__flush__")) return true
                Thread.sleep(25)
            }
            false
        } catch (_: Throwable) {
            false
        }
    }
}
