using System;

namespace NetPilot.Services;

/// <summary>Central lifecycle for all background services.</summary>
public static class ServiceHub
{
    private static bool _started;

    public static void Init()
    {
        if (_started) return;
        _started = true;

        try { Lang.Lang.Instance.SetLanguage(SettingsService.Current.Language); } catch { }
        try { ProfileService.EnsureDefaults(); } catch { }
        try { MonitorService.Instance.Start(); } catch (Exception ex) { App.LogCrash(ex); }
        try { ProcessNetworkService.Instance.Start(); } catch (Exception ex) { App.LogCrash(ex); }
        try { LimiterService.Instance.Start(); } catch (Exception ex) { App.LogCrash(ex); }
        try { ScheduleService.Instance.Start(); } catch (Exception ex) { App.LogCrash(ex); }
        try { HealthService.Instance.Start(); } catch (Exception ex) { App.LogCrash(ex); }
        try { MobileVpnApi.Instance.Start(MobileVpnService.ApiPort); } catch (Exception ex) { App.LogCrash(ex); }
        try { TrayService.Instance.Init(); } catch (Exception ex) { App.LogCrash(ex); }
    }

    public static void ShutdownAll()
    {
        try { MonitorService.Instance.Stop(); } catch { }
        try { ProcessNetworkService.Instance.Stop(); } catch { }
        try { ScheduleService.Instance.Stop(); } catch { }
        try { HealthService.Instance.Stop(); } catch { }
        try { LimiterService.Instance.Stop(); } catch { }
        try { MobileVpnApi.Instance.Stop(); } catch { }
        try { UsageService.Instance.Save(); } catch { }
        try { TrayService.Instance.Dispose(); } catch { }
    }
}
