using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NetPilot.Desktop.Views;
using NetPilot.Core.Logging;
using NetPilot.Daemon;
using NetPilot.Services;

namespace NetPilot.Desktop;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window hosts the very same backend and bridge the daemon uses, so the phone
            // pairs with the graphical build exactly as it does with the headless one, and
            // there is only ever one implementation of that protocol.
            // Logging first. This is a separate process from the daemon, so it configures the
            // logger for itself - a diagnostics page reporting "memory only" is a page nobody
            // can send anything in.
            var stateDir = Program.ResolveStateDir();
            Log.Configure(Path.Combine(stateDir, "logs"), LogLevel.Info);
            Log.SetCategoryLevel("probe", LogLevel.Debug);
            Log.Info("gui", "NetPilot graphical build " + typeof(App).Assembly.GetName().Version +
                            " starting on " +
                            (OperatingSystem.IsWindows() ? "windows"
                             : OperatingSystem.IsMacOS() ? "macos" : "linux"));
            Log.Info("gui", $"log file: {Log.FilePath ?? "(memory only)"}");

            Directory.CreateDirectory(stateDir);
            var backend = Program.CreateBackend(stateDir);
            Services = new AppServices(backend, stateDir);
            Log.Info("gui", $"bridge reachable from a phone: {Services.ReachableFromPhone}");

            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(Services) };
            if (Entry.InitialPage is string key &&
                desktop.MainWindow.DataContext is ShellViewModel vm)
                vm.Pick(key);
            desktop.ShutdownRequested += (_, _) => Services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static AppServices Services { get; private set; }
}

/// <summary>Owns the backend and the bridge for the life of the window.</summary>
public sealed class AppServices : IDisposable
{
    private readonly System.Threading.CancellationTokenSource _cts = new();

    public AppServices(INetworkBackend backend, string stateDir)
    {
        Backend = backend;
        Bridge = new Bridge(backend, stateDir);
        // Started fire-and-forget: the listener is already bound by the time the first await
        // yields, so the window can query it immediately.
        _ = Bridge.RunAsync(_cts.Token, quiet: true);
    }

    public INetworkBackend Backend { get; }

    public Bridge Bridge { get; }

    /// <summary>False when the wildcard bind was refused, so the phone cannot reach us. The
    /// window shows this rather than leaving a phone that "cannot connect" unexplained.</summary>
    public bool ReachableFromPhone => Bridge.ReachableFromPhone;

    public void Dispose()
    {
        _cts.Cancel();
        Backend.Dispose();
        _cts.Dispose();
    }
}