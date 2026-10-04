using System;
using NetPilot.Core.Logging;

namespace NetPilot.Services;

/// <summary>Central lifecycle for all background services.</summary>
public static class ServiceHub
{
    private static bool _started;

    public static void Init()
    {
        if (_started) return;
        _started = true;
        Log.Info("app", "starting services");

        // Each service gets its own category. Every one of these used to report through the
        // same "app" bucket, so a limiter that would not start was indistinguishable in the log
        // from a tray icon that would not appear - and with seven silent catches in a row, the
        // question "which of these actually failed" had no answer at all.
        try { Lang.Lang.Instance.SetLanguage(SettingsService.Current.Language); Log.Debug("app", "language applied"); }
        catch (Exception ex) { Fail("app", "language", ex); }

        try { ProfileService.EnsureDefaults(); Log.Debug("app", "default profiles ensured"); }
        catch (Exception ex) { Fail("app", "profiles", ex); }

        Start("monitor", () => MonitorService.Instance.Start());
        Start("process", () => ProcessNetworkService.Instance.Start());
        Start("limiter", () => LimiterService.Instance.Start());
        Start("schedule", () => ScheduleService.Instance.Start());
        Start("health", () => HealthService.Instance.Start());
        Start("bridge", () => MobileVpnApi.Instance.Start(MobileVpnService.ApiPort));
        Start("tray", () => TrayService.Instance.Init());

        Log.Info("app", "services started");
    }

    /// <summary>
    /// Starts one service and records the outcome either way.
    ///
    /// The catch used to call App.LogCrash, which wrote to crash.log with no category - so the
    /// log could say that something failed but not what. Naming the service is the difference
    /// between "the app broke" and "the limiter did not start because it needs Administrator".
    /// </summary>
    private static void Start(string category, Action action)
    {
        try
        {
            action();
            Log.Debug(category, "started");
        }
        catch (Exception ex)
        {
            Fail(category, "start", ex);
        }
    }

    private static void Fail(string category, string what, Exception ex)
    {
        // Warn, not Error: a service that fails to start leaves the rest of the app running,
        // and the user gets a partly working product rather than a broken one. The severity
        // that matters here is "notice this", not "stop everything".
        Log.Warn(category, what + " failed", ex);
    }

    public static void ShutdownAll()
    {
        Log.Info("app", "stopping services");
        Stop("monitor", () => MonitorService.Instance.Stop());
        Stop("process", () => ProcessNetworkService.Instance.Stop());
        Stop("schedule", () => ScheduleService.Instance.Stop());
        Stop("health", () => HealthService.Instance.Stop());
        Stop("limiter", () => LimiterService.Instance.Stop());
        Stop("bridge", () => MobileVpnApi.Instance.Stop());
        Stop("usage", () => UsageService.Instance.Save());
        Stop("tray", () => TrayService.Instance.Dispose());
    }

    /// <summary>Shutdown failures used to be swallowed entirely. They are quiet by design -
    /// one service failing to stop must not prevent the others - but "entirely" is not the same
    /// as "unrecorded", which is what a diagnostic file is for.</summary>
    private static void Stop(string category, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn(category, "did not stop cleanly", ex); }
    }
}