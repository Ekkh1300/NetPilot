using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace NetPilot.Core.Logging;

/// <summary>
/// The logger. One static facade, shared by the Windows app, the daemon and the graphical
/// build, so a log line means the same thing wherever it was produced.
///
/// Three properties this is built around, in priority order:
///
///  1. **It never throws.** A logger that throws takes the application down with it, and the
///     one moment you most need the log is the moment a broken disk would kill you. Every
///     write path is wrapped, and every failure is counted rather than propagated.
///
///  2. **It never blocks the caller.** Entries go onto a bounded queue drained by one
///     background thread. If the queue is full - a slow disk, a log viewer holding the file -
///     entries are *dropped and counted*. Blocking a UI thread on a disk write is a hang the
///     user sees; dropping a log line is not. DroppedCount is surfaced, so a gap is visible
///     rather than silent.
///
///  3. **Nothing sensitive is written.** <see cref="Redactor"/> runs before any sink sees the
///     text, once, in one place.
/// </summary>
public static class Log
{
    private static readonly object SetupLock = new();
    private static volatile bool _configured;

    // Bounded: 4096 entries is a few megabytes at worst and far more than a session produces
    // before the writer catches up.
    //
    // Not readonly: Shutdown replaces it rather than disposing it. Disposing a completed
    // BlockingCollection makes it permanently unusable - TryAdd and GetConsumingEnumerable then
    // throw ObjectDisposedException, which the write path swallows, so every later entry would
    // be dropped in silence and the log would simply stop. Found by the test suite, and it
    // would have hit any app that reconfigured its logging after a settings change.
    private static BlockingCollection<LogEntry> _queue = NewQueue();

    private static BlockingCollection<LogEntry> NewQueue() =>
        new(new ConcurrentQueue<LogEntry>(), 4096);

    private static Thread _worker;
    private static FileSink _file;
    private static RingBuffer _memory;

    private static long _dropped;
    private static long _written;
    private static long _writeFailures;
    private static volatile int _sinkFailed;

    /// <summary>Entries lost because the queue was full. Shown in diagnostics, because a log
    /// with a silent hole in it is worse than one that admits it.</summary>
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    public static long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>Times the file could not be written at all. Non-zero means the file on disk is
    /// incomplete, and the reason should be on screen.</summary>
    public static long WriteFailureCount => Interlocked.Read(ref _writeFailures);

    /// <summary>Entries waiting for the writer. Surfaced because "dropped: 447" is only
    /// interpretable next to "still queued: 0" - a large queue means the sink, not the
    /// producer, is the bottleneck.</summary>
    public static int QueuedCount
    {
        get { try { return _queue.Count; } catch { return 0; } }
    }

    /// <summary>Where the file sink is writing, or null when logging to memory only.</summary>
    public static string FilePath => _file?.CurrentPath;

    public static LogLevel MinimumLevel { get; private set; } = LogLevel.Info;

    /// <summary>Per-category overrides, e.g. "tunnel" at Trace while everything else stays Info.
    /// A category that is not listed falls back to <see cref="MinimumLevel"/>.</summary>
    public static IReadOnlyDictionary<string, LogLevel> CategoryLevels =>
        _categoryLevels ?? new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, LogLevel> _categoryLevels;

    /// <summary>
    /// Points the logger at a directory and starts the writer thread. Safe to call more than
    /// once; later calls replace the sinks but keep the counters, so a settings change to the
    /// log level does not lose the record of how much was dropped before it.
    /// </summary>
    public static void Configure(string directory, LogLevel minimum = LogLevel.Info,
                                 int maxFileBytes = 4 * 1024 * 1024, int keepFiles = 4)
    {
        lock (SetupLock)
        {
            MinimumLevel = minimum;
            _categoryLevels = null;

            // Re-point the sink even when already configured.
            //
            // The first version returned early if the logger had been configured before, on the
            // reasoning that nothing calls it twice. That is wrong in a way that bites: a first
            // call that failed to open its directory left _file null, and every later call was
            // ignored, so logging stayed dead for the life of the process with no error anywhere.
            // A settings change that moves the log directory has the same effect.
            var path = string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.Combine(directory, "netpilot.log");

            if (path == null)
            {
                try { _file?.Dispose(); } catch { }
                _file = null;
            }
            else if (_file == null || !string.Equals(_file.CurrentPath, path, StringComparison.Ordinal))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    try { _file?.Dispose(); } catch { }
                    _file = new FileSink(path, maxFileBytes, keepFiles);
                    Interlocked.Exchange(ref _sinkFailed, 0);
                }
                catch
                {
                    // No log directory is a degraded product, not a broken one. Carry on with
                    // the in-memory buffer, which is what the diagnostics page reads, and say so
                    // once so the user is not left guessing why there is no file.
                    try { _file?.Dispose(); } catch { }
                    _file = null;
                    _memory?.Add(new LogEntry(DateTime.UtcNow, LogLevel.Warn, "log",
                        "no log directory at '" + directory + "'; entries are kept in memory only"));
                }
            }

            _memory ??= new RingBuffer(2000);

            if (_worker is null)
            {
                _worker = new Thread(Drain)
                {
                    Name = "NetPilot log",
                    // Background, so it cannot hold the process open at shutdown.
                    IsBackground = true,
                };
                _worker.Start();
            }
            _configured = true;
        }
    }

    /// <summary>Turns on a finer level for one subsystem. Used by the diagnostics page's
    /// "trace the tunnel" switch.</summary>
    public static void SetCategoryLevel(string category, LogLevel level)
    {
        lock (SetupLock)
        {
            _categoryLevels ??= new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
            _categoryLevels[category] = level;
        }
    }

    public static bool IsEnabled(LogLevel level, string category = null)
    {
        // The category override *replaces* the global threshold, in both directions.
        //
        // Checking the global level first looks equivalent and is not: it means a category can
        // only ever be made quieter, so "trace the tunnel while everything else stays at Info"
        // - the entire reason per-category levels exist - silently did nothing. Caught by the
        // test that traces one category and asserts another stays quiet.
        var levels = _categoryLevels;
        if (levels != null && category != null && levels.TryGetValue(category, out var specific))
            return level >= specific;
        return level >= MinimumLevel;
    }

    // ---------------- the call sites ----------------

    public static void Trace(string category, string message) => Write(LogLevel.Trace, category, message, null);

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);

    public static void Warn(string category, string message, Exception ex = null) =>
        Write(LogLevel.Warn, category, message, ex);

    public static void Error(string category, string message, Exception ex = null) =>
        Write(LogLevel.Error, category, message, ex);

    public static void Fatal(string category, string message, Exception ex = null) =>
        Write(LogLevel.Fatal, category, message, ex);

    public static void Write(LogLevel level, string category, string message, Exception ex = null)
    {
        // Two guards, and neither of them is optional. The first keeps a call from an
        // unconfigured subsystem (a unit test, a tool) from silently doing nothing.
        if (!_configured)
        {
            Configure(null, LogLevel.Warn);
        }

        try
        {
            if (!IsEnabled(level, category)) return;

            var entry = new LogEntry(
                DateTime.UtcNow, level, category,
                Redactor.Apply(message),
                ex == null ? null : Redactor.Apply(ex));

            // TryAdd, never Add: Add on a bounded collection blocks when full, which is exactly
            // the failure mode this logger exists to avoid.
            if (!_queue.TryAdd(entry))
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
        }
        catch
        {
            // Nothing left to do, and throwing here would be the worst possible outcome.
        }
    }

    private static void Drain()
    {
        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            try
            {
                _memory?.Add(entry);

                if (_file is null) continue;
                try
                {
                    _file.Append(entry);
                    Interlocked.Increment(ref _written);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _writeFailures);
                    // Remember the first failure only: repeating it every entry would turn one
                    // broken disk into thousands of identical log lines.
                    if (Interlocked.CompareExchange(ref _sinkFailed, 1, 0) == 0)
                    {
                        _memory?.Add(new LogEntry(DateTime.UtcNow, LogLevel.Error, "log",
                            "the log file could not be written: " + ex.Message +
                            " - further entries are kept in memory only"));
                    }
                }
            }
            catch
            {
                // The worker must never die: a dead writer is a log that silently stops.
            }
        }
    }

    /// <summary>Snapshot of the in-memory buffer, newest first. What the diagnostics page shows.</summary>
    public static IReadOnlyList<LogEntry> Recent(int max = 500, LogLevel minimum = LogLevel.Trace,
                                                 string categoryFilter = null, string textFilter = null)
        => _memory?.Snapshot(max, minimum, categoryFilter, textFilter) ?? Array.Empty<LogEntry>();

    /// <summary>
    /// Everything needed for a support request, as text: the log, the counters, and the
    /// environment. This is what the "copy diagnostics" button puts on the clipboard.
    /// </summary>
    public static string DiagnosticsReport()
    {
        var sb = new StringBuilder(8192);
        sb.Append("NetPilot diagnostics\n");
        sb.Append("generated : ").Append(LogLevelExtensions.Stamp(DateTime.UtcNow)).Append('\n');
        sb.Append("runtime   : ").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
          .Append(" on ").Append(System.Runtime.InteropServices.RuntimeInformation.OSDescription).Append('\n');
        sb.Append("architecture: ").Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture).Append('\n');
        sb.Append("log level : ").Append(MinimumLevel).Append('\n');
        sb.Append("log file  : ").Append(FilePath ?? "(memory only)").Append('\n');
        sb.Append("written   : ").Append(WrittenCount).Append('\n');
        sb.Append("dropped   : ").Append(DroppedCount).Append('\n');
        sb.Append("write errs: ").Append(WriteFailureCount).Append('\n');
        sb.Append('\n');

        // The tail, not everything: a support log that is 200 MB gets truncated by whoever
        // reads it, and the interesting part is always recent.
        var entries = Recent(400);
        sb.Append("---- last ").Append(entries.Count).Append(" entries ----\n");
        foreach (var e in entries)
        {
            sb.Append(e.ToLine()).Append('\n');
            if (e.Detail != null) sb.Append(e.Detail);
        }
        return Redactor.Apply(sb.ToString());
    }

    /// <summary>Flushes and stops the writer. Called at shutdown so the last entries are not
    /// lost, and so tests do not leak a thread.</summary>
    public static void Shutdown(int timeoutMs = 2000)
    {
        if (!_configured) return;
        try
        {
            _queue.CompleteAdding();
            _worker?.Join(timeoutMs);
        }
        catch { }
        finally
        {
            try { _file?.Dispose(); } catch { }

            // A fresh queue, so the logger still works after a shutdown. The old one is
            // completed but never disposed - see the note on the field.
            lock (SetupLock)
            {
                try { var old = _queue; _queue = NewQueue(); old.CompleteAdding(); } catch { }
                _worker = null;
                _file = null;
                _memory = null;
                _configured = false;
            }
        }
    }
}

/// <summary>
/// A bounded, newest-first buffer held in memory.
///
/// Bounded because a diagnostics page that grows without limit is a memory leak wearing a
/// helpful hat, and because the whole point of the buffer is "the last few minutes", not
/// "everything since Tuesday".
/// </summary>
public sealed class RingBuffer
{
    private readonly LogEntry[] _items;
    private int _next;
    private int _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity) => _items = new LogEntry[Math.Max(16, capacity)];

    public int Count { get { lock (_lock) return _count; } }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _items[_next] = entry;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    /// <summary>Newest first, filtered. Filtering happens here rather than in the caller so
    /// the page never has to pull 2000 entries to show 20.</summary>
    public IReadOnlyList<LogEntry> Snapshot(int max, LogLevel minimum, string categoryFilter, string textFilter)
    {
        var result = new List<LogEntry>();
        lock (_lock)
        {
            for (int i = 0; i < _count && result.Count < max; i++)
            {
                int idx = (_next - 1 - i + _items.Length * 2) % _items.Length;
                var e = _items[idx];
                if (e is null) continue;
                if (e.Level < minimum) continue;
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    !e.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(textFilter) &&
                    e.Message.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(e);
            }
        }
        return result;
    }
}

/// <summary>
/// A rotating file sink.
///
/// Rotates on size rather than on date, because what matters is that the file cannot grow
/// without bound - a user who leaves a laptop on for a month must not fill their disk with
/// diagnostics. Keeps N previous files, each capped, so the total is bounded by a known
/// multiple of the cap.
/// </summary>
public sealed class FileSink : IDisposable
{
    private readonly object _lock = new();
    private readonly int _maxBytes;
    private readonly int _keep;

    // The writer is held open rather than reopening the file per line.
    //
    // File.AppendAllText opens, writes and closes for every entry. Measured while the tests
    // ran, that is slow enough that a burst of a few thousand entries fills the queue and
    // starts dropping - which is the logger failing at exactly the moment there is most to
    // record. One open handle with a buffered writer turns the same burst into a few writes.
    private StreamWriter _writer;
    private FileStream _stream;
    private long _bytes;

    // Flushed on every line, because a log that loses its tail on a crash is missing exactly
    // the entries that explain the crash.
    //
    // Both flushes are needed and neither is optional: StreamWriter.AutoFlush pushes the
    // writer's buffer into the FileStream, and the FileStream still holds its own. Skipping
    // the second left the entries counted as written and invisible to anyone reading the file
    // - which is how a dozen tests found a logger that recorded 10,000 entries into a file
    // nobody could find a single line in.
    private void FlushToDisk()
    {
        _writer?.Flush();
        _stream?.Flush();
    }

    public FileSink(string path, int maxBytes, int keepFiles)
    {
        CurrentPath = path;
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _keep = Math.Clamp(keepFiles, 1, 20);
        Open();
    }

    public string CurrentPath { get; }

    private void Open()
    {
        var dir = Path.GetDirectoryName(CurrentPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };

        try { _bytes = new FileInfo(CurrentPath).Length; }
        catch { _bytes = 0; }
    }

    public void Append(LogEntry entry)
    {
        lock (_lock)
        {
            if (_writer is null) Open();

            string line = entry.ToLine();
            // Rotate *before* writing, so a file never exceeds the cap.
            if (_bytes + line.Length > _maxBytes) Rotate();

            _writer.Write(line);
            _writer.Write('\n');
            if (entry.Detail != null) _writer.Write(entry.Detail);
            FlushToDisk();
            _bytes += line.Length + 1 + (entry.Detail?.Length ?? 0);
        }
    }

    private void Rotate()
    {
        // Close before moving: on Windows an open handle cannot be renamed, and on every
        // platform a moved-but-open file would keep receiving writes at its old name.
        try { FlushToDisk(); _writer?.Dispose(); _stream?.Dispose(); } catch { }
        _writer = null;
        _stream = null;

        // netpilot.log -> netpilot.1.log -> netpilot.2.log ... dropping the oldest.
        var oldest = ArchivePath(_keep);
        TryDelete(oldest);

        for (int i = _keep - 1; i >= 1; i--)
        {
            var from = ArchivePath(i);
            if (File.Exists(from)) TryMove(from, ArchivePath(i + 1));
        }
        if (File.Exists(CurrentPath)) TryMove(CurrentPath, ArchivePath(1));

        Open();
    }

    private string ArchivePath(int index) =>
        Path.Combine(Path.GetDirectoryName(CurrentPath) ?? ".",
                     Path.GetFileNameWithoutExtension(CurrentPath) + "." + index + ".log");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryMove(string from, string to)
    {
        try { if (File.Exists(to)) File.Delete(to); File.Move(from, to); } catch { }
    }

    /// <summary>Reads the log back for the diagnostics page, newest file last.</summary>
    public IReadOnlyList<string> ReadRecent(int maxLines)
    {
        var lines = new List<string>();
        try
        {
            // Flush first: the caller is about to read what is on disk, and the writer is
            // buffered. Without this the newest entries would be missing from the very view
            // meant to show them.
            lock (_lock) { FlushToDisk(); }

            var files = new List<string>();
            for (int i = _keep; i >= 1; i--)
                if (File.Exists(ArchivePath(i))) files.Add(ArchivePath(i));
            if (File.Exists(CurrentPath)) files.Add(CurrentPath);

            foreach (var f in files)
            {
                foreach (var l in ReadAllLinesShared(f))
                {
                    lines.Add(l);
                    if (lines.Count > maxLines * 2) lines.RemoveAt(0);
                }
            }
        }
        catch { }
        if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);
        return lines;
    }

    /// <summary>
    /// Reads a file while this process still has it open for writing.
    ///
    /// File.ReadAllLines opens with FileShare.Read, which forbids *other* handles from writing -
    /// and this process's own writer holds the log for exactly that. Windows enforces sharing
    /// in both directions, so the default reader cannot open a file that is still being
    /// appended to, and fails with "used by another process". Opening with ReadWrite is what
    /// makes a log tailable while it is written.
    ///
    /// Not a test-only concern: this is the diagnostics page reading the live log.
    /// </summary>
    public static IReadOnlyList<string> ReadAllLinesShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = new List<string>();
            string line;
            while ((line = reader.ReadLine()) != null) all.Add(line);
            return all;
        }
        catch
        {
            // A rotated archive somebody else holds, or a file that was just moved: fall back
            // rather than failing the caller over it.
            try { return File.ReadAllLines(path); } catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>The last lines of a log file, safe to read while logging continues.</summary>
    public static IReadOnlyList<string> ReadShared(string path, int maxLines)
    {
        var lines = ReadAllLinesShared(path);
        return lines.Count > maxLines ? lines.Skip(lines.Count - maxLines).ToList() : lines;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { FlushToDisk(); _writer?.Dispose(); _stream?.Dispose(); } catch { }
            _writer = null;
            _stream = null;
        }
    }
}