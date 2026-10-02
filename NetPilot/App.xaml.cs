using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

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

    public static void LogCrash(Exception ex)
    {
        try
        {
            if (ex == null) return;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPilot");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "crash.log");

            // A XAML/layout exception recurs on every measure pass. Logging it verbatim
            // each time turned a few kilobytes into a 110 MB file (and every append was
            // a whole stack trace), so identical back-to-back reports are collapsed.
            string signature = ex.GetType().FullName + "|" + ex.Message;

            lock (_logLock)
            {
                if (string.Equals(signature, _lastSignature, StringComparison.Ordinal) &&
                    (DateTime.Now - _lastLoggedAt) < TimeSpan.FromMinutes(1))
                    return;
                _lastSignature = signature;
                _lastLoggedAt = DateTime.Now;

                // Trim from the front when the file grows past the cap, keeping the most
                // recent tail instead of losing all history or filling the disk.
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    try
                    {
                        byte[] all = File.ReadAllBytes(file);
                        int keep = (int)Math.Min(all.Length, MaxLogBytes / 4);
                        byte[] tail = new byte[keep];
                        Buffer.BlockCopy(all, all.Length - keep, tail, 0, keep);
                        File.WriteAllBytes(file, tail);
                    }
                    catch { /* trimming is best effort; appending below still runs */ }
                }

                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
            }
        }
        catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        try { Services.ServiceHub.ShutdownAll(); } catch { }
        base.OnExit(e);
    }
}
