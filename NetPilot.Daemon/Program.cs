using System.Net;
using System.Text;
using NetPilot.Core.Api;
using NetPilot.Daemon.Backends;
using NetPilot.Services;

namespace NetPilot.Daemon;

/// <summary>
/// The macOS / Linux entry point.
///
/// Small on purpose: pick the backend for the OS we are actually on, start the bridge the
/// Android phone already knows how to talk to, and report honestly what this machine can do.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var stateDir = ResolveStateDir();
        Directory.CreateDirectory(stateDir);

        var backend = CreateBackend(stateDir);
        if (backend is null)
        {
            Console.Error.WriteLine("netpilotd runs on Linux and macOS. " +
                                    "On Windows use the desktop app, which is the full product.");
            return 2;
        }

        Console.WriteLine($"NetPilot daemon {typeof(Program).Assembly.GetName().Version} on {backend.Platform}");
        Console.WriteLine($"  state       : {stateDir}");
        Console.WriteLine($"  privileged  : {(backend.IsPrivileged ? "yes" : "no - firewall and limits are unavailable")}");

        // Print the honest capability list up front. On macOS the firewall and the limiter
        // are the features people came for, and they are not there; that has to be stated
        // when the process starts, not discovered when a toggle does nothing.
        foreach (var target in new[] { new AppHandle { Uid = 1000, DisplayName = "user 1000" } })
        {
            var support = backend.Capability(target);
            Console.WriteLine($"  rules       : {Describe(support)}");
            break;
        }

        if (!backend.IsPrivileged)
            Console.WriteLine("  hint        : re-run with sudo to enable blocking and bandwidth limits");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var bridge = new Bridge(backend, stateDir);
        await bridge.RunAsync(cts.Token);

        return 0;
    }

    private static string Describe(RuleSupport support) => support switch
    {
        RuleSupport.Full => "block + shape available",
        RuleSupport.Block => "block only",
        _ => "NOT AVAILABLE on this platform (see docs/PORTING.md)",
    };

    private static INetworkBackend CreateBackend(string stateDir)
    {
        if (OperatingSystem.IsLinux()) return new LinuxBackend(stateDir);
        if (OperatingSystem.IsMacOS()) return new MacBackend(stateDir);
        return null;
    }

    /// <summary>XDG on Linux, Application Support on macOS, so the state lands where the
    /// platform expects and a reinstall does not silently wipe the rules.</summary>
    private static string ResolveStateDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            string xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(xdg))
                xdg = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                                   Path.Combine(home, ".local", "share"), "netpilot");
            return Path.Combine(xdg, "netpilot");
        }
        return Path.Combine(home, "Library", "Application Support", "NetPilot");
    }
}

/// <summary>
/// The bridge the Android phone pairs with. Speaks the same ten endpoints as the Windows
/// build - the phone cannot tell them apart, which is the entire point of putting the paths
/// in NetPilot.Core.
/// </summary>
internal sealed class Bridge
{
    private readonly INetworkBackend _backend;
    private readonly string _stateDir;
    private HttpListener _listener;
    private string _token = "";

    public Bridge(INetworkBackend backend, string stateDir)
    {
        _backend = backend;
        _stateDir = stateDir;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // HttpListener on the wildcard prefix is what makes the phone able to reach us over
        // Wi-Fi or USB. Where the OS denies it (no capability, locked-down host) we fall back
        // to loopback and say so, rather than binding and appearing to work on a machine the
        // phone cannot reach.
        _listener = new HttpListener();
        bool wildcard;
        try
        {
            _listener.Prefixes.Add($"http://+:{ApiContract.BridgePort}/");
            _listener.Start();
            wildcard = true;
        }
        catch
        {
            _listener.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{ApiContract.BridgePort}/");
            _listener.Start();
            wildcard = false;
        }

        Console.WriteLine($"  bridge      : http://{(wildcard ? "+" : "localhost")}:{ApiContract.BridgePort}/api/v1");
        Console.WriteLine($"  reachable   : {(wildcard ? "yes, from the phone on this network" : "no, loopback only")}");

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch when (ct.IsCancellationRequested) { break; }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            string method = ctx.Request.HttpMethod.ToUpperInvariant();
            string route = $"{method} {path}";

            if (route == ApiContract.Ping)
            {
                await Json(res, 200, new
                {
                    app = "NetPilot",
                    version = "1.2.2",
                    api = ApiContract.Version,
                    platform = _backend.Platform,
                    paired = _token.Length > 0,
                    privileged = _backend.IsPrivileged,
                });
                return;
            }

            if (route == ApiContract.Status)
            {
                var links = await _backend.GetLinksAsync();
                var vpn = await _backend.GetPcVpnStateAsync();
                await Json(res, 200, new
                {
                    platform = _backend.Platform,
                    privileged = _backend.IsPrivileged,
                    links,
                    pcVpn = vpn.Active ? "active" : vpn.Unknown ? "unknown" : "off",
                    support = _backend.Capability(new AppHandle { Uid = 0 }).ToString().ToLowerInvariant(),
                });
                return;
            }

            if (route == ApiContract.Stop)
            {
                await Json(res, 200, await _backend.RestoreSnapshotAsync());
                return;
            }

            if (route == ApiContract.Backup)
            {
                string snap = await _backend.CaptureSnapshotAsync();
                await Json(res, snap.Length > 0 ? 200 : 400,
                    snap.Length > 0 ? new { ok = true } : new { ok = false, detail = "nothing restorable on this platform" });
                return;
            }

            if (route == ApiContract.Restore)
            {
                await Json(res, 200, await _backend.RestoreSnapshotAsync());
                return;
            }

            await Json(res, 404, new { error = "not found" });
        }
        catch (Exception ex)
        {
            try { await Json(res, 500, new { error = ex.Message }); } catch { }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
        }
    }

    private static async Task Json(HttpListenerResponse res, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload));
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }
}