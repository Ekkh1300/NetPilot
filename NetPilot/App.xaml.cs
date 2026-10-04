using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NetPilot.Core.Logging;

namespace NetPilot;

public partial class App : Application
{
    private static Mutex _singleInstanceMutex;

    /// <summary>True once a real exit was requested, so MainWindow.Close can tell
    /// "close to tray" apart from "actually quit".</summary>
    public static bool IsExiting;

    public static void ExitApp()
    {
        IsExiting = true;
        Current?.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "NetPilot_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running -> just exit silently.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);

        // Logging first, before any service starts. It used to be configured nowhere: the only
        // thing this app wrote was crash.log, from the 45 places that call LogCrash, with no
        // levels, no filtering and no rotation. A fault in the limiter or in the tunnel could
        // not be described at all, because nothing recorded the decisions either side of it.
        try
        {
            Log.Configure(LogDirectory(), LogLevel.Info);
            Log.Info("app", "NetPilot " + typeof(App).Assembly.GetName().Version + " starting");
            Log.Info("app", $"log file: {Log.FilePath ?? "(memory only)"}");
            Log.Info("app", $"run as {(IsElevated() ? "administrator" : "a normal user")}");
            Log.Info("app", $"data directory: {DataDirectory()}");
        }
        catch
        {
            // No logging is a degraded product, not a broken one. If this throws there is
            // nowhere to record it, which is exactly why the catch is total.
        }

        // `NetPilot.exe --healthtest`: assert the health-score curves and leave — before a
        // single service starts and before a window exists. The score is pure arithmetic
        // (HealthScoring), so it is the one part of the product this harness can verify
        // rather than merely look at, which is why it gets a flag of its own instead of a
        // screenshot. Result: healthcheck.txt, in the same folder harness.ps1 reads
        // langcheck.txt from.
        if (System.Array.Exists(e.Args, a =>
                string.Equals(a, "--healthtest", System.StringComparison.OrdinalIgnoreCase)))
        {
            RunHealthTest();
            Shutdown();
            return;
        }

        Services.ServiceHub.Init();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private const string HealthCheckFile = @"E:\op dn\NetPilot\healthcheck.txt";

    private static void RunHealthTest()
    {
        try
        {
            var failures = Services.HealthScoring.SelfTest();
            File.WriteAllText(HealthCheckFile, failures.Count == 0
                ? "ok=1\n"
                : "ok=0\n" + string.Join("\n", failures) + "\n");
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            try { File.WriteAllText(HealthCheckFile, "exception=" + ex.Message); } catch { }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogCrash(ex);
    }

    private static readonly object _logLock = new();
    private static string _lastSignature = "";
    private static DateTime _lastLoggedAt = DateTime.MinValue;
    private const long MaxLogBytes = 1_000_000;   // keep crash.log bounded

    /// <summary>Where everything this app persists lives. One place, so the log cannot end up
    /// somewhere the user will not look for it.</summary>
    public static string DataDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPilot");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    public static string LogDirectory() => Path.Combine(DataDirectory(), "logs");

    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Every existing call site keeps working, and now lands in the real log.
    ///
    /// All 45 callers are unchanged and none of them had to be touched: they were already
    /// saying "something went wrong", which is exactly a Warn or an Error with a category and
    /// a stack trace. The difference now is that those entries sit beside the decisions that
    /// led to them, share one rotation policy, and are redacted before they touch the disk.
    ///
    /// The de-duplication stays. A WPF layout exception fires on every measure pass, and
    /// logging it verbatim each time turned a few kilobytes into 110 MB.
    /// </summary>
    public static void LogCrash(Exception ex) => LogCrash("app", ex);

    public static void LogCrash(string category, Exception ex)
    {
        try
        {
            if (ex == null) return;

            string signature = ex.GetType().FullName + "|" + ex.Message;
            bool repeat;
            lock (_logLock)
            {
                repeat = string.Equals(signature, _lastSignature, StringComparison.Ordinal) &&
                         (DateTime.Now - _lastLoggedAt) < TimeSpan.FromMinutes(1);
                if (!repeat)
                {
                    _lastSignature = signature;
                    _lastLoggedAt = DateTime.Now;
                }
            }
            if (repeat) return;

            // Fatal for the ones that end the process, Error for the rest: the level is what a
            // reader filters on, and collapsing both into one bucket loses that.
            Log.Write(ex is OutOfMemoryException || ex is StackOverflowException
                         ? LogLevel.Fatal
                         : LogLevel.Error,
                      category, ex.Message, ex);
        }
        catch { /* a logger that throws takes the application with it */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        try
        {
            Log.Info("app", "shutting down");
            Services.ServiceHub.ShutdownAll();
        }
        catch (Exception ex) { LogCrash("app", ex); }

        // Flush before the process goes away. The last few entries are the ones a crash report
        // needs most, and a background writer does not get a say in when we exit.
        try { Log.Shutdown(3000); } catch { }
        base.OnExit(e);
    }
}
