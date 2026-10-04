using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Core.Logging;
using Xunit;

namespace NetPilot.Core.Tests;

/// <summary>
/// The logger's two guarantees that matter when something has already gone wrong: it never
/// throws, and it never writes a secret.
///
/// A logger that throws takes the application down, and the moment you need the log is exactly
/// the moment the disk is full or the directory is read-only. A logger that leaks the pairing
/// token hands the machine to whoever can read the thread it was pasted into.
/// </summary>
[Collection("logger")]
public class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
                                                "np-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Log.Shutdown(20000); } catch { }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static void WaitFor(Func<bool> ok, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline) { if (ok()) return; Thread.Sleep(25); }
    }

    private void WaitForFile(string marker, int ms = 6000) => WaitForFileIn(_dir, marker, ms);

    private void WaitForFileIn(string directory, string marker, int ms = 6000)
    {
        var path = Path.Combine(directory, "netpilot.log");
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) && File.Exists(path) && FileSink.ReadShared(path, 100000).Any(l => l.Contains(marker))) return;
            }
            catch { }
            Thread.Sleep(25);
        }
        Assert.Fail("the log file never contained: " + marker +
                    "\ndirectory : " + _dir +
                    "\nwritten   : " + Log.WrittenCount +
                    "  dropped: " + Log.DroppedCount +
                    "  errors: " + Log.WriteFailureCount +
                    "\nfile sink : " + (Log.FilePath ?? "(none)") +
                    "\non disk   : " + Describe(directory));
    }
    private void Configure(LogLevel level = LogLevel.Trace) => Log.Configure(_dir, level);
    /// <summary>What is actually in a directory, for a failure message. Guessing at the cause
    /// of a test that cannot find its own file wastes more time than printing the listing.</summary>
    private static string FirstLine(string file)
    {
        try { return FileSink.ReadShared(file, 100000).FirstOrDefault()?.Trim() ?? ""; }
        catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
    }
    private static string Describe(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "(the directory does not exist)";
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            if (files.Length == 0) return "(empty)";
            return string.Join("; ", files.Select(f =>
                Path.GetFileName(f) + "=" + new FileInfo(f).Length + "B" +
                " {" + FirstLine(f) + "}"));
        }
        catch (Exception ex) { return "(listing failed: " + ex.Message + ")"; }
    }

    // ---------------- it never throws ----------------

    [Fact]
    public void WritingWithNoConfiguration_DoesNotThrow()
    {
        // A call from a unit test or a tool, before anything configured the logger. It must be a
        // no-op rather than a NullReferenceException in someone's constructor.
        Log.Info("test", "no configuration yet");
        Log.Error("test", "and an error too", new InvalidOperationException("boom"));
    }

    [Fact]
    public void AnUnwritableDirectory_DegradesInsteadOfThrowing()
    {
        // A path that cannot be created: logging must not be the thing that breaks the app.
        Log.Configure("/proc/definitely/not/writable/at/all", LogLevel.Trace);
        Log.Info("test", "still fine");
        Log.Error("test", "and errors too", new Exception("nope"));
        Assert.True(true);   // reaching here is the assertion
    }

    [Fact]
    public void AHugeMessage_DoesNotThrow()
    {
        Configure();
        Log.Info("test", new string('x', 500_000));
        WaitForFile("xxxxxxxx", 8000);
    }

    [Fact]
    public async Task ConcurrentWriters_AllLand()
    {
        Configure();
        const int threads = 8, each = 200;

        // The counters deliberately survive a reconfigure, so this measures the *change* over
        // the window rather than an absolute total - otherwise the assertion is about which
        // tests ran before this one, not about this one.
        long before = Log.WrittenCount + Log.WriteFailureCount;
        long droppedBefore = Log.DroppedCount;

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < each; i++)
                Log.Info("test", $"thread {t} entry {i}");
        })).ToArray();
        await Task.WhenAll(tasks);

        WaitForFile("entry 199", 20000);
        Thread.Sleep(400);      // let the tail drain before measuring

        long after = Log.WrittenCount + Log.WriteFailureCount;
        Assert.Equal(0, Log.DroppedCount - droppedBefore);   // a queue this small must not overflow
        Assert.Equal(threads * each, after - before);
    }

    /// <summary>Reconfiguring has to take effect. An earlier version ignored every call after
    /// the first, so a first call that failed to open its directory left logging dead for the
    /// rest of the process with no error anywhere.</summary>
    [Fact]
    public void ReconfiguringMovesTheLogToTheNewDirectory()
    {
        var first = Path.Combine(_dir, "a");
        var second = Path.Combine(_dir, "b");
        Log.Configure(first, LogLevel.Trace);
        Log.Info("test", "written to the first directory");
        WaitForFileIn(first, "written to the first directory");
        Assert.True(File.Exists(Path.Combine(first, "netpilot.log")));

        Log.Configure(second, LogLevel.Trace);
        Log.Info("test", "written to the second directory");
        WaitForFileIn(second, "written to the second directory");

        Assert.True(File.Exists(Path.Combine(second, "netpilot.log")),
            "the second Configure was ignored, so nothing was written to the new path");
    }

    [Fact]
    public void AFailedConfigureDoesNotKillLoggingForever()
    {
        // The exact sequence that used to leave the logger permanently silent.
        Log.Configure("/proc/definitely/not/writable/at/all", LogLevel.Trace);
        Log.Info("test", "written while there is no directory");
        Log.Configure(_dir, LogLevel.Trace);
        Log.Info("test", "written once a real directory exists");
        WaitForFile("written once a real directory exists");
    }

    [Fact]
    public void EntriesReachTheFile()
    {
        Configure();
        Log.Info("bridge", "a distinctive marker line");
        WaitForFile("a distinctive marker line");
        Assert.Contains("a distinctive marker line", string.Join("\n", FileSink.ReadShared(Path.Combine(_dir, "netpilot.log"), 100000)));
    }

    // ---------------- levels ----------------

    [Fact]
    public void TheThresholdIsRespected()
    {
        Log.Configure(_dir, LogLevel.Warn);
        Log.Debug("test", "this should not appear");
        Log.Info("test", "nor this");
        Log.Warn("test", "but this should");
        WaitForFile("but this should");
        var text = string.Join("\n", FileSink.ReadShared(Path.Combine(_dir, "netpilot.log"), 100000));
        Assert.DoesNotContain("this should not appear", text);
        Assert.DoesNotContain("nor this", text);
        Assert.Contains("but this should", text);
    }

    [Fact]
    public void OneCategoryCanBeTracedWhileTheRestStaysQuiet()
    {
        Log.Configure(_dir, LogLevel.Info);
        Log.SetCategoryLevel("tunnel", LogLevel.Trace);
        Log.Trace("tunnel", "chatter from the tunnel");
        Log.Trace("ui", "chatter from the ui");
        Log.Info("tunnel", "something worth knowing");
        WaitForFile("chatter from the tunnel");
        var text = string.Join("\n", FileSink.ReadShared(Path.Combine(_dir, "netpilot.log"), 100000));
        Assert.Contains("chatter from the tunnel", text);
        Assert.DoesNotContain("chatter from the ui", text);
        Assert.Contains("something worth knowing", text);
    }

    [Fact]
    public void EveryLevelHasAFixedWidthTag()
    {
        Assert.Equal("TRC", LogLevel.Trace.Tag());
        Assert.Equal("DBG", LogLevel.Debug.Tag());
        Assert.Equal("INF", LogLevel.Info.Tag());
        Assert.Equal("WRN", LogLevel.Warn.Tag());
        Assert.Equal("ERR", LogLevel.Error.Tag());
        Assert.Equal("FTL", LogLevel.Fatal.Tag());
    }

    [Fact]
    public void LevelsCompareInTheRightOrder()
    {
        Assert.True(LogLevel.Info.Includes(LogLevel.Error));
        Assert.False(LogLevel.Error.Includes(LogLevel.Info));
        Assert.True(LogLevel.Warn.Includes(LogLevel.Warn));   // equal counts as included
    }

    [Fact]
    public void AnUnparsableLevelIsRefusedRatherThanDefaulted()
    {
        // A typo that silently became "log everything" would be a privacy problem, not a
        // convenience.
        Assert.False(LogLevelExtensions.TryParse("verbsoe", out _));
        Assert.True(LogLevelExtensions.TryParse("  DEBUG ", out var level));
        Assert.Equal(LogLevel.Debug, level);
    }

    /// <summary>A category override must work in *both* directions. Checking the global
    /// threshold first made an override able only to quieten a category, so tracing one
    /// subsystem while the app sat at Info produced nothing at all.</summary>
    [Theory]
    [InlineData(LogLevel.Trace, "tunnel", true)]      // override lowers the bar
    [InlineData(LogLevel.Debug, "tunnel", true)]
    [InlineData(LogLevel.Trace, "ui", false)]         // ...and only for that category
    [InlineData(LogLevel.Debug, "ui", false)]
    [InlineData(LogLevel.Info, "tunnel", true)]
    [InlineData(LogLevel.Info, "ui", true)]
    public void ACategoryOverrideReplacesTheGlobalThreshold(LogLevel level, string category, bool expected)
    {
        Log.Configure(_dir, LogLevel.Info);
        Log.SetCategoryLevel("tunnel", LogLevel.Trace);
        Assert.Equal(expected, Log.IsEnabled(level, category));
    }

    /// <summary>The inverse: a category can also be made quieter than everything else.</summary>
    [Fact]
    public void ACategoryCanBeQuieterThanTheRest()
    {
        Log.Configure(_dir, LogLevel.Trace);
        Log.SetCategoryLevel("noisy", LogLevel.Error);
        Assert.True(Log.IsEnabled(LogLevel.Trace, "other"));
        Assert.False(Log.IsEnabled(LogLevel.Info, "noisy"));
        Assert.True(Log.IsEnabled(LogLevel.Error, "noisy"));
    }

    // ---------------- redaction: the part that must not be wrong ----------------

    [Fact]
    public void ABearerTokenNeverReachesTheFile()
    {
        Configure();
        Log.Info("bridge", "auth failed for Authorization: Bearer abc123def456ghi789jkl");
        WaitForFile("[redacted]");
        var text = string.Join("\n", FileSink.ReadShared(Path.Combine(_dir, "netpilot.log"), 100000));
        Assert.DoesNotContain("abc123def456ghi789jkl", text);
        Assert.Contains(Redactor.Mask, text);
    }

    [Fact]
    public void ATokenInJsonIsRemovedWhateverTheKeyCasing()
    {
        foreach (var key in new[] { "token", "Token", "pairingCode", "pairing_code", "secret", "password", "apiKey" })
        {
            var outp = Redactor.Apply($"{{\"{key}\":\"hunter2value\"}}");
            Assert.DoesNotContain("hunter2value", outp);
        }
    }

    [Fact]
    public void ABareSixDigitPairingCodeIsRemoved()
    {
        var outp = Redactor.Apply("pairing code 482913 accepted");
        Assert.DoesNotContain("482913", outp);
        Assert.Contains(Redactor.Mask, outp);
    }

    [Fact]
    public void AMacAddressIsRemoved()
    {
        var outp = Redactor.Apply("adapter AA:BB:CC:DD:EE:FF came up");
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", outp);
    }

    /// <summary>The other direction, and the one that matters more: over-redaction destroys the
    /// file's only reason to exist. Addresses, counters and interface names must survive.</summary>
    [Fact]
    public void DiagnosticDetailIsNotDestroyed()
    {
        var line = "link wlan0 up 192.168.1.100 gw 192.168.1.1 rx 123456789 tx 987654321 rate 4096";
        var outp = Redactor.Apply(line);
        Assert.Equal(line, outp);

        Assert.Contains("192.168.1.100", Redactor.Apply("ip 192.168.1.100"));
        Assert.Contains("802.11n", Redactor.Apply("desc 802.11n USB Wireless LAN Card"));
        Assert.Contains("1234567890", Redactor.Apply("rx 1234567890"));
    }

    [Fact]
    public void AnExceptionChainIsRedactedThroughout()
    {
        var ex = new InvalidOperationException("outer Bearer abc123def456ghi",
            new ArgumentException("inner token: \"abcdef123456\""));
        var text = Redactor.Apply(ex);
        Assert.DoesNotContain("abc123def456", text);
        Assert.DoesNotContain("abcdef123456", text);
        // The shape survives, which is the point: a redacted stack trace is still useful.
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("ArgumentException", text);
        Assert.Contains("outer", text);
    }

    [Fact]
    public void AnAggregateExceptionsSiblingsAreNotLost()
    {
        var ex = new AggregateException(new Exception("first"),
                                        new Exception("second Bearer abc123def456"));
        var text = Redactor.Apply(ex);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
        Assert.DoesNotContain("abc123def456", text);
    }

    [Fact]
    public void RedactingSomethingWithNothingToHideChangesNothing()
    {
        var line = "the tunnel started on wlan0";
        Assert.Equal(line, Redactor.Apply(line));
        Assert.Equal(0, Redactor.CountRedactions(line));
        Assert.True(Redactor.CountRedactions("code 123456") > 0);
    }

    // ---------------- rotation ----------------

    [Fact]
    public void TheFileIsCappedAndOldOnesAreDropped()
    {
        // A tiny cap so rotation happens without writing megabytes.
        Log.Configure(_dir, LogLevel.Trace, maxFileBytes: 64 * 1024, keepFiles: 3);
        for (int i = 0; i < 4000; i++)
            Log.Info("test", $"line {i} padded out so the file actually grows past the cap");
        WaitForFile("line 3999", 60000);

        // Leave nothing queued behind. The logger is static state shared by the whole test
        // class, so a backlog here becomes the next test's dropped entries - and the failure
        // lands on an unrelated test, which is a miserable thing to debug.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (Log.QueuedCount > 0 && DateTime.UtcNow < until) Thread.Sleep(50);
        Assert.Equal(0, Log.QueuedCount);
        var files = Directory.GetFiles(_dir, "netpilot*.log");
        Assert.True(files.Length <= 4, $"expected at most 4 files, found {files.Length}");

        long total = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });
        // Each file is capped and there are at most 4, so the total is bounded. This is the
        // property that stops a month-long uptime from filling someone's disk.
        Assert.True(total < 64 * 1024 * 6,
                    $"log directory holds {total} bytes, which is not bounded as intended");
    }

    // ---------------- the diagnostics page's view ----------------

    [Fact]
    public void TheInMemoryBufferIsNewestFirstAndFilterable()
    {
        Configure();
        Log.Info("alpha", "first");
        Log.Warn("beta", "second");
        Log.Error("alpha", "third");
        WaitFor(() => Log.Recent(100).Count >= 3);

        var recent = Log.Recent(100);
        Assert.Equal("third", recent[0].Message);
        Assert.Equal("second", recent[1].Message);

        Assert.True(Log.Recent(100, LogLevel.Trace, "alpha").Count == 2);
        Assert.True(Log.Recent(100, LogLevel.Warn).Count == 2);   // info is below the floor
        Assert.Single(Log.Recent(100, LogLevel.Trace, null, "second"));
    }

    [Fact]
    public void TheBufferIsBounded()
    {
        Configure();
        for (int i = 0; i < 5000; i++) Log.Info("test", "flood " + i);
        WaitFor(() => Log.Recent(100000).Count >= 2000);
        // The ring holds at most its capacity; without the bound this would be 5000 entries of
        // live objects for the life of the process.
        Assert.True(Log.Recent(100000).Count <= 2000);
    }

    [Fact]
    public void TheDiagnosticsReportCarriesTheCountersAndNoSecrets()
    {
        Configure();
        Log.Info("bridge", "pairing code 918273 issued");
        Log.Error("bridge", "auth failed Bearer abc123def456ghi789");
        WaitForFile("pairing code [redacted] issued");

        var report = Log.DiagnosticsReport();
        Assert.Contains("NetPilot diagnostics", report);
        Assert.Contains("dropped", report);
        Assert.Contains("write errs", report);
        Assert.DoesNotContain("918273", report);
        Assert.DoesNotContain("abc123def456ghi789", report);
    }

    [Fact]
    public void ShutdownStopsTheWorkerAndFlushes()
    {
        Configure();
        Log.Info("test", "before shutdown");
        Log.Shutdown(3000);
        // The last entries must be on disk: a log that loses its final lines is exactly the
        // log you needed.
        Assert.Contains("before shutdown", string.Join("\n", FileSink.ReadShared(Path.Combine(_dir, "netpilot.log"), 100000)));
        // And using it afterwards must not throw.
        Log.Info("test", "after shutdown");
    }
    /// <summary>The diagnostics page reads the live log while the writer still holds it. The
    /// default reader cannot do that - Windows refuses, because the reader's share mode forbids
    /// the writer's access - so the page needs the shared-open helper. Found by these tests
    /// failing with "used by another process" on a file the logger had just written.
    /// </summary>
    [Fact]
    public void TheLiveLogCanBeReadWhileItIsBeingWritten()
    {
        Configure();
        Log.Info("test", "written and then read back immediately");
        WaitForFile("written and then read back immediately");

        var path = Path.Combine(_dir, "netpilot.log");
        Assert.Contains("written and then read back immediately",
            string.Join("\n", FileSink.ReadShared(path, 1000)));
    }

    [Fact]
    public void ThePlainReaderCannotReadIt_WhichIsWhyTheHelperExists()
    {
        Configure();
        Log.Info("test", "still held open by the writer");
        WaitForFile("still held open by the writer");

        // Documents the platform behaviour the helper works around. If this ever starts
        // passing, the helper can go and the comment above it with it.
        Assert.Throws<IOException>(() =>
        {
            using var stream = new FileStream(Path.Combine(_dir, "netpilot.log"),
                                              FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.ReadByte();
        });
    }
    // ---------------- formatting ----------------

    [Fact]
    public void TimestampsAreUtcAndIso()
    {
        var t = new DateTime(2026, 10, 3, 14, 5, 6, DateTimeKind.Local);
        var stamp = LogLevelExtensions.Stamp(t);
        Assert.EndsWith("Z", stamp);
        // A local 14:05 in a +03:30 zone is 10:35 UTC; what matters is that Z is present and
        // the format is sortable, which is what support needs.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z$", stamp);
    }

    [Fact]
    public void AMultiLineMessageStaysOnOneLine()
    {
        // A stack trace embedded in a message would otherwise break "one line per entry",
        // which is the whole reason the file is greppable.
        var e = new LogEntry(DateTime.UtcNow, LogLevel.Info, "cat", "first\nsecond\rthird");
        Assert.DoesNotContain("\n", e.ToLine());
    }
}

/// <summary>
/// The logger is static state with a background thread. Running its tests in parallel would
/// have them configuring each other out from under themselves.
/// </summary>
[CollectionDefinition("logger", DisableParallelization = true)]
public class LoggerCollection { }