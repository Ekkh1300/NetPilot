using System.Net;
using System.Text;
using NetPilot.Core.Api;
using NetPilot.Core.Logging;
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
        // Logging comes first, before anything that can fail. This daemon used to have eight
        // Console.WriteLine calls and nothing else - which on a machine started from a
        // file manager, over ssh without a tty, or from a service unit is no logging at all.
        Log.Configure(Path.Combine(stateDirectory(), ""), LogLevel.Info);
        Log.SetCategoryLevel("probe", LogLevel.Debug);

        var stateDir = ResolveStateDir();
        Directory.CreateDirectory(stateDir);
        Log.Info("main", "NetPilot daemon " + typeof(Program).Assembly.GetName().Version + " starting");
        Log.Info("main", "state directory: " + stateDir);

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

    /// <summary>
    /// Where the log lives. Next to the state rather than inside the app bundle, so an upgrade
    /// does not throw away the evidence from the version that had the fault.
    /// </summary>
    private static string stateDirectory() => Path.Combine(ResolveStateDir(), "logs");

    /// <summary>One line for a result: what happened, and why not when it did not happen.</summary>
    private static string Describe(MvResult r) =>
        r.Ok ? "applied"
             : "REFUSED - " + r.Key + (r.Detail.Length > 0 ? " (" + r.Detail + ")" : "");

    private static string Describe(RuleSupport support) => support switch
    {
        RuleSupport.Full => "block + shape available",
        RuleSupport.Block => "block only",
        _ => "NOT AVAILABLE on this platform (see docs/PORTING.md)",
    };

    /// <summary>Picks the backend for the OS we are actually running on. Public because the
    /// graphical build hosts the same one, and a second selection logic here would be a
    /// second thing to keep in step.</summary>
    public static INetworkBackend CreateBackend(string stateDir)
    {
        if (OperatingSystem.IsLinux()) return new LinuxBackend(stateDir);
        if (OperatingSystem.IsMacOS()) return new MacBackend(stateDir);

        // Windows gets the read-only portable backend rather than "unsupported". The desktop
        // app is the full product here; this exists so the daemon - and the Avalonia window
        // built on top of it - can be run and verified on a machine that has no Linux or macOS
        // available at all.
        return new ManagedBackend(stateDir);
    }

    /// <summary>XDG on Linux, Application Support on macOS, AppData on Windows, so state lands
    /// where the platform expects and a reinstall does not silently wipe the rules.</summary>
    public static string ResolveStateDir()
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

        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "NetPilot");

        // Windows. This used to fall through to the macOS branch, because the check was
        // "if Linux do X, otherwise Y" rather than "if Linux X, else if macOS Y, else Z" -
        // so on a Windows machine the state, and now the log, landed in
        // C:\Users\<name>\Library\Application Support\NetPilot.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetPilot");
    }
}

/// <summary>
/// The bridge the Android phone pairs with. Speaks the same ten endpoints as the Windows
/// build - the phone cannot tell them apart, which is the entire point of putting the paths
/// in NetPilot.Core.
/// </summary>
/// <summary>
/// The control bridge, and the component the graphical build is built on.
///
/// Public because the Avalonia window hosts it in-process: the desktop app talks to the same
/// backend over the same endpoints, so there is one implementation of the phone protocol
/// rather than a second one for the window to drift from.
/// </summary>
public sealed class Bridge
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

    /// <summary>True when the wildcard bind succeeded, so a phone on this network can reach us.
    /// The window shows this: "loopback only" explains a phone that cannot connect, where a
    /// silent fallback just looks like a bug.</summary>
    public bool ReachableFromPhone { get; private set; }

    public async Task RunAsync(CancellationToken ct, bool quiet = false)
    {
        // Quiet mode is for the graphical build: there is no console to print to, and a window
        // that says "loopback only" is more use than a line nobody reads.
        // HttpListener on the wildcard prefix is what makes the phone able to reach us over
        // Wi-Fi or USB. Where the OS denies it (no capability, a locked-down host) we fall back
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

        Console.WriteLine($"  reachable   : {(wildcard ? "yes, from the phone on this network" : "no, loopback only")}");

        // Whether a phone can actually reach us is the single most useful fact in this feature,
        // and it is decided by whether the wildcard bind succeeded. A silent fallback to
        // loopback is exactly how "the phone says it cannot connect" happens with nobody able
        // to say why.
        ReachableFromPhone = wildcard;
        if (!quiet)
        {
            Console.WriteLine($"  bridge      : http://{(wildcard ? "+" : "localhost")}:{ApiContract.BridgePort}/api/v1");
            Console.WriteLine($"  reachable   : {(wildcard ? "yes, from the phone on this network" : "no, loopback only")}");
        }
        Log.Info("bridge", wildcard
            ? $"listening on all interfaces, port {ApiContract.BridgePort} - a phone on this network can reach us"
            : "the wildcard bind was refused, so this is loopback only and a phone cannot reach it");
        Log.Info("bridge", $"log file: {Log.FilePath ?? "(memory only)"}");

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch when (ct.IsCancellationRequested) { break; }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    /// <summary>One line for a result: what happened, and why not when it did not happen.</summary>
    private static string Describe(MvResult r) =>
        r.Ok ? "applied"
             : "REFUSED - " + r.Key + (r.Detail.Length > 0 ? " (" + r.Detail + ")" : "");
    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;

        // Hoisted out of the try on purpose: the catch below logs which request failed, and a
        // variable declared inside the try is not in scope there. Without this the only thing
        // recorded is "a request failed", which is not a diagnostic.
        string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
        string method = ctx.Request.HttpMethod.ToUpperInvariant();
        string route = $"{method} {path}";

        try
        {
            string body = "";
            if (method is "POST" or "PUT")
            {
                using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                body = await sr.ReadToEndAsync().ConfigureAwait(false);
            }

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

            // The privileged operations. Exposed over the bridge so the daemon can be
            // exercised on a real machine without a GUI - which is the only way to find out
            // whether the kernel accepts what we generate before a user does.
            if (route == ApiContract.Block)
            {
                var (uid, okUid) = ReadInt(body, "uid");
                var (blocked, okBlocked) = ReadBool(body, "blocked");
                if (!okUid) { await Json(res, 400, new { error = "uid is required" }); return; }
                if (!okBlocked) blocked = true;
                var blockedResult = await _backend.SetBlockedAsync(
                    new AppHandle { Uid = uid, DisplayName = "uid " + uid }, blocked);

                // Recorded either way, and a refusal as prominently as a success: a firewall
                // toggle that silently did nothing is the exact failure a log exists to expose.
                Log.Info("bridge", (blocked ? "block uid " : "unblock uid ") + uid + ": " +
                                    Describe(blockedResult));
                await Json(res, 200, blockedResult);
                return;
            }

            if (route == ApiContract.Limit)
            {
                var (uid, okUid) = ReadInt(body, "uid");
                var (up, _) = ReadInt(body, "upBps");
                var (down, _) = ReadInt(body, "downBps");
                if (!okUid) { await Json(res, 400, new { error = "uid is required" }); return; }

                var target = new AppHandle { Uid = uid, DisplayName = "uid " + uid };
                var upResult = await _backend.SetUploadLimitAsync(target, up);
                var downResult = await _backend.SetDownloadLimitAsync(target, down);
                Log.Info("bridge", $"limit uid {uid}: up {Describe(upResult)}, down {Describe(downResult)}");
                await Json(res, 200, new
                {
                    up = upResult,
                    down = downResult,
                    support = _backend.Capability(target).ToString(),
                });
                return;
            }

            if (route == ApiContract.Capability)
            {
                var (uid, okUid) = ReadInt(body, "uid");
                var target = new AppHandle { Uid = okUid ? uid : -1 };
                var support = _backend.Capability(target);
                // Report what a call would actually do, not just a capability flag: on macOS
                // the honest answer is a refusal, and it should be visible without side effects.
                var probe = await _backend.SetBlockedAsync(target, true);
                await Json(res, 200, new
                {
                    support = support.ToString(),
                    privileged = _backend.IsPrivileged,
                    blockingProbe = probe,
                });
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
            // The phone only ever sees the 500 it gets back. Without this line a handler that
            // throws leaves nothing behind but a status code.
            Log.Error("bridge", route + " failed", ex);
            try { await Json(res, 500, new { error = ex.Message }); } catch { }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
        }
    }

    /// <summary>
    /// One serializer configuration for the whole bridge.
    ///
    /// <c>IncludeFields</c> is not optional here: <c>MvLink</c> and <c>MvResult</c> are
    /// declared with public <i>fields</i>, not properties, and the default serializer silently
    /// drops every one of them. The bridge answered <c>/status</c> with an array of empty
    /// objects. Compiling it looked fine, starting it looked fine, and only asking it a
    /// question on a real machine showed it.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new()
        {
            IncludeFields = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

    private static async Task Json(HttpListenerResponse res, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(payload, JsonOptions));
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    private static (int Value, bool Ok) ReadInt(string body, string name)
    {
        if (string.IsNullOrWhiteSpace(body)) return (0, false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return (0, false);
            if (!doc.RootElement.TryGetProperty(name, out var v)) return (0, false);
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number) return (v.GetInt32(), true);
            if (v.ValueKind == System.Text.Json.JsonValueKind.String &&
                int.TryParse(v.GetString(), out var p)) return (p, true);
            return (0, false);
        }
        catch { return (0, false); }
    }

    private static (bool Value, bool Ok) ReadBool(string body, string name)
    {
        if (string.IsNullOrWhiteSpace(body)) return (false, false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return (false, false);
            if (!doc.RootElement.TryGetProperty(name, out var v)) return (false, false);
            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => (true, true),
                System.Text.Json.JsonValueKind.False => (false, true),
                _ => (false, false),
            };
        }
        catch { return (false, false); }
    }
}
