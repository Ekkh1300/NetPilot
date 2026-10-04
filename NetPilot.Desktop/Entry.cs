using Avalonia;
using NetPilot.Daemon;

namespace NetPilot.Desktop;

public static class Entry
{
    /// <summary>
    /// Avalonia's entry point. Not Main, because Avalonia's headless test runner and its
    /// designer both need to find the builder, and a Main here would hide that.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // "--page <key>" opens straight onto a page. It exists so the pages can be captured
        // and checked one at a time without synthesising clicks on a shared desktop, where a
        // stray click lands on whatever else happens to be in front.
        if (args.Length >= 2 && args[0] == "--page") InitialPage = args[1];

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string InitialPage { get; private set; }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}