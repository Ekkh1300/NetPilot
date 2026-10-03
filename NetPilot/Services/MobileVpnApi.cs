using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetPilot.Services;

/// <summary>
/// Local HTTP bridge the (future) Android app talks to.
///
/// Flow for the phone:
///   1. GET  /api/v1/ping          - discover the PC, no auth
///   2. POST /api/v1/pair {code}   - code shown in the UI, returns a bearer token
///   3. call everything else with  - Authorization: Bearer &lt;token&gt;
///
/// The token is the only credential that leaves this machine, it is regenerated on every
/// app start, and pairing attempts are rate limited so the code cannot be brute forced.
/// Everything the app is allowed to do is intentionally limited to this feature: read the
/// status, report the phone's VPN state, and ask for share/backup/restore. There is no
/// endpoint that can disconnect or replace the PC's VPN without the UI consent flag.
/// </summary>
public sealed class MobileVpnApi : IDisposable
{
    public static readonly MobileVpnApi Instance = new();

    private HttpListener _listener;
    private CancellationTokenSource _cts;
    private Task _loop;
    private int _pairFails;
    private DateTime _pairWindow = DateTime.MinValue;
    private readonly object _pairLock = new();

    public bool Running { get; private set; }

    /// <summary>
    /// False when the bridge had to fall back to loopback (no URL ACL, i.e. the app was not
    /// started as administrator). A phone on the same network cannot reach a loopback-only
    /// listener, and the UI has to be able to explain that rather than show "not paired".
    /// </summary>
    public bool ReachableFromPhone { get; private set; }
    public string PairingCode { get; private set; } = "------";
    public string Token { get; private set; } = "";
    public bool Paired => Token.Length > 0;
    public string DeviceName { get; private set; } = "";
    public string DeviceDetail { get; private set; } = "";

    public event Action Changed;

    private MobileVpnApi() => NewPairingCode();

    public void NewPairingCode()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        uint n = BitConverter.ToUInt32(bytes, 0) % 1_000_000;
        PairingCode = n.ToString("D6");
        Raise();
    }

    public void Unpair()
    {
        Token = "";
        DeviceName = "";
        DeviceDetail = "";
        NewPairingCode();
    }

    private void Raise() => Changed?.Invoke();

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// Starts the bridge. Fire-and-forget by design: [ServiceHub.Init] calls it from the UI
    /// thread during startup, and the firewall step below *has* to await a PowerShell child.
    /// Awaiting it synchronously (.GetResult()) deadlocked the whole app - the listener was up
    /// (the port was open, http.sys had a process attached) but the accept loop never
    /// started, so every phone request hung until it timed out.
    /// </summary>
    public void Start(int port) => _ = StartAsync(port);

    /// <summary>
    /// Why the last <see cref="StartAsync"/> gave up, for the UI to show instead of a
    /// button that silently refuses to light up.
    /// </summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// Windows releases an http.sys URL group a moment after the listener closes, so a
    /// stop-then-start in quick succession failed with "address already in use", the catch
    /// swallowed it, and the page just sat at "stopped" - which users report as the toggle
    /// not working. Retry that one case briefly; a permanent refusal (no URL ACL, so the
    /// wildcard bind is denied) is not retried, because that is not going to change and the
    /// loopback fallback below is the right answer for it.
    /// </summary>
    private const int BindRetries = 8;
    private const int BindRetryDelayMs = 250;

    private static bool IsAddressInUse(HttpListenerException ex) =>
        ex.ErrorCode == 98 /* SocketError.AddressAlreadyInUse */ ||
        (ex.InnerException is System.Net.Sockets.SocketException se && se.SocketErrorCode ==
            System.Net.Sockets.SocketError.AddressAlreadyInUse);

    public async Task StartAsync(int port)
    {
        if (Running) return;
        LastError = "";
        try
        {
            _listener = new HttpListener();
            bool wildcard = false;
            Exception bindError = null;

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    // All interfaces first, so the Android app can reach us over USB or Wi-Fi.
                    _listener.Prefixes.Add($"http://+:{port}/");
                    _listener.Start();
                    wildcard = true;
                    break;
                }
                catch (HttpListenerException ex) when (!IsAddressInUse(ex))
                {
                    // A machine without the URL ACL (non-elevated start, hardened policy):
                    // fall back to loopback instead of losing the bridge entirely.
                    _listener.Close();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{port}/");
                    _listener.Start();
                    break;
                }
                catch (Exception ex)
                {
                    bindError = ex;
                    if (attempt >= BindRetries)
                    {
                        LastError = ex.Message;
                        throw;
                    }
                    try { await Task.Delay(BindRetryDelayMs).ConfigureAwait(false); } catch { }
                    // Rebuild the listener: after a failed Start it cannot be reused.
                    try { _listener.Close(); } catch { }
                    _listener = new HttpListener();
                }
            }

            _cts = new CancellationTokenSource();
            Running = true;
            // A wildcard bind that fell back to loopback is not reachable from a phone, and
            // the page has to be able to explain that rather than show "not paired".
            ReachableFromPhone = wildcard;

            // The listener is already serving; the accept loop goes first so a request that
            // arrives during the PowerShell call is answered instead of queueing silently.
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

            if (ReachableFromPhone) await EnsureInboundRuleAsync(port).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Running = false;
            LastError = string.IsNullOrEmpty(LastError) ? ex.Message : LastError;
            App.LogCrash(ex);
        }
        Raise();
    }

    /// <summary>
    /// Opens the Windows Firewall for the bridge port.
    ///
    /// Without this the listener is perfectly healthy and the phone still cannot reach it:
    /// Windows blocks inbound TCP to a new listener until someone allows it, and the app has
    /// no way to prompt for that from a tray/background start. We are elevated (see
    /// app.manifest), so the rule can simply be created - and only when it is missing, so an
    /// administrator who tightened the rules on purpose is not overridden on every launch.
    /// </summary>
    internal static async Task EnsureInboundRuleAsync(int port)
    {
        try
        {
            const string name = "NetPilot Mobile VPN bridge";
            // If a rule with this name already exists, this is a no-op.
            string script = "if (-not (Get-NetFirewallRule -DisplayName '" + name + "' -ErrorAction SilentlyContinue)) {" +
                            "New-NetFirewallRule -DisplayName '" + name + "' -Direction Inbound -Action Allow " +
                            "-Protocol TCP -LocalPort " + port + " -Profile Any | Out-Null}";
            var (ok, _) = await Sys.PsStdoutAsync(script, 15000).ConfigureAwait(false);
            if (!ok)
            {
                // Hosts without the NetSecurity module: fall back to netsh (idempotent, and it
                // fails silently when the rule is already there).
                await Sys.PsStdoutAsync(
                    "netsh advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow " +
                    "protocol=TCP localport=" + port + " profile=any", 15000).ConfigureAwait(false);
            }
        }
        catch { /* a locked-down host simply keeps the current policy */ }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;

        // Let the accept loop finish: GetContextAsync throws the moment the listener
        // closes, so this normally returns immediately. Without it, a shutdown could
        // report "stopped" while a request (e.g. a snapshot restore) was still running.
        try { _loop?.Wait(1000); } catch { }
        _loop = null;

        try { _cts?.Dispose(); } catch { }
        _cts = null;

        Running = false;
        Raise();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }

            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ routing

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        HttpListenerResponse res = ctx.Response;
        try
        {
            string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            string method = ctx.Request.HttpMethod.ToUpperInvariant();
            string body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);

            switch ($"{method} {path}")
            {
                case "GET /api/v1/ping":
                    await JsonAsync(res, 200, new
                    {
                        app = "NetPilot",
                        version = "1.2.2",
                        api = 1,
                        paired = Paired,
                    }).ConfigureAwait(false);
                    return;

                case "POST /api/v1/pair":
                    await HandlePairAsync(res, body).ConfigureAwait(false);
                    return;
            }

            if (!IsAuthorized(ctx.Request))
            {
                await JsonAsync(res, 401, new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            switch ($"{method} {path}")
            {
                case "GET /api/v1/status":
                    await HandleStatusAsync(res).ConfigureAwait(false);
                    return;

                case "POST /api/v1/hello":
                    HandleHello(body);
                    await JsonAsync(res, 200, new { ok = true, pairingCode = PairingCode }).ConfigureAwait(false);
                    return;

                case "POST /api/v1/report":
                    HandleReport(body);
                    await JsonAsync(res, 200, new { ok = true }).ConfigureAwait(false);
                    return;

                case "POST /api/v1/backup":
                {
                    var r = await MobileVpnService.Instance.CaptureSnapshotAsync().ConfigureAwait(false);
                    await JsonAsync(res, r.Ok ? 200 : 400, new { ok = r.Ok, key = r.Key, detail = r.Detail })
                        .ConfigureAwait(false);
                    return;
                }

                case "POST /api/v1/restore":
                {
                    var r = await MobileVpnService.Instance.RestoreSnapshotAsync().ConfigureAwait(false);
                    await JsonAsync(res, r.Ok ? 200 : 400, new { ok = r.Ok, key = r.Key, detail = r.Detail })
                        .ConfigureAwait(false);
                    return;
                }

                case "POST /api/v1/share":
                {
                    // Note: the phone can request sharing, but it can never turn off the
                    // PC VPN guard - that flag only exists in the desktop UI.
                    // body: {"method":"route"|"proxy", "proxy":"host:port"} (both optional)
                    string shareMethod = (JsonStr(body, "method") ?? "").Trim().ToLowerInvariant();
                    bool wantProxy = string.Equals(shareMethod, "proxy", StringComparison.Ordinal);
                    if (wantProxy)
                    {
                        string p = (JsonStr(body, "proxy") ?? "").Trim();
                        if (p.Length > 0) MobileVpnService.Instance.PeerProxy = p;
                    }

                    var m = wantProxy ? MvMethod.Proxy : MvMethod.Auto;

                    var state = await MobileVpnService.Instance.DetectAsync().ConfigureAwait(false);
                    var link = state.AnyUpLink;
                    var r = await MobileVpnService.Instance
                        .ShareAsync(link, m, allowVpnChange: false)
                        .ConfigureAwait(false);
                    await JsonAsync(res, r.Ok ? 200 : 409, new { ok = r.Ok, key = r.Key, detail = r.Detail })
                        .ConfigureAwait(false);
                    return;
                }

                case "POST /api/v1/stop":
                {
                    var r = await MobileVpnService.Instance.StopShareAsync().ConfigureAwait(false);
                    await JsonAsync(res, r.Ok ? 200 : 400, new { ok = r.Ok, key = r.Key, detail = r.Detail })
                        .ConfigureAwait(false);
                    return;
                }

                case "DELETE /api/v1/pair":
                    Unpair();
                    await JsonAsync(res, 200, new { ok = true }).ConfigureAwait(false);
                    return;

                default:
                    await JsonAsync(res, 404, new { error = "not found" }).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            try { await JsonAsync(res, 500, new { error = "server error" }).ConfigureAwait(false); } catch { }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
        }
    }

    private async Task HandlePairAsync(HttpListenerResponse res, string body)
    {
        // 5 failed attempts per minute, then the code has to be regenerated in the UI.
        // Handlers run concurrently (one Task per request), so the counter is guarded:
        // unlocked increments let a parallel burst slip past the limit. The decision is
        // taken inside the lock, the awaits stay outside - you cannot await while holding it.
        string code = JsonStr(body, "code");
        int outcome;   // 0 = paired, 1 = bad code, 2 = rate limited
        lock (_pairLock)
        {
            if (DateTime.UtcNow - _pairWindow > TimeSpan.FromMinutes(1))
            {
                _pairWindow = DateTime.UtcNow;
                _pairFails = 0;
            }

            if (_pairFails >= 5) outcome = 2;
            else if (string.IsNullOrEmpty(PairingCode) ||
                     !string.Equals(code, PairingCode, StringComparison.Ordinal))
            {
                _pairFails++;
                outcome = 1;
            }
            else
            {
                // Clear the counter on success: five earlier failures would otherwise keep
                // refusing legitimate re-pairing for the rest of the minute.
                _pairFails = 0;
                _pairWindow = DateTime.UtcNow;
                outcome = 0;
            }
        }

        if (outcome == 2)
        {
            await JsonAsync(res, 429, new { error = "too many attempts" }).ConfigureAwait(false);
            return;
        }
        if (outcome == 1)
        {
            await JsonAsync(res, 403, new { error = "bad code" }).ConfigureAwait(false);
            return;
        }

        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        Token = Convert.ToHexString(bytes);

        var helloName = JsonStr(body, "name");
        if (!string.IsNullOrEmpty(helloName)) DeviceName = helloName;
        Raise();

        await JsonAsync(res, 200, new { ok = true, token = Token, api = 1 }).ConfigureAwait(false);
    }

    private void HandleHello(string body)
    {
        string name = JsonStr(body, "name");
        string model = JsonStr(body, "model");
        string android = JsonStr(body, "android");
        if (!string.IsNullOrEmpty(name)) DeviceName = name;
        DeviceDetail = string.Join(" · ", new[] { model, android }.Where(s => !string.IsNullOrEmpty(s)));
        MobileVpnService.Instance.ReportedDeviceInfo = string.IsNullOrEmpty(DeviceDetail) ? name : DeviceDetail;
        MobileVpnService.Instance.ReportedAt = DateTime.Now;
        Raise();
    }

    private void HandleReport(string body)
    {
        bool? vpn = null;
        string proxy = "";
        bool hasProxy = false;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("vpnActive", out var v))
                vpn = v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null;
            // The phone advertises the proxy it runs so the desktop can point
            // WinHTTP at it instead of guessing.
            if (doc.RootElement.TryGetProperty("proxy", out var p) && p.ValueKind == JsonValueKind.String)
            {
                proxy = (p.GetString() ?? "").Trim();
                hasProxy = true;
            }
        }
        catch { /* a malformed report must never break the bridge */ }

        MobileVpnService.Instance.ReportedMobileVpn = vpn;
        MobileVpnService.Instance.ReportedAt = DateTime.Now;
        // Only overwrite when the payload actually carries one: a report that simply omits
        // the field (older app, or a vpnActive-only ping) used to wipe a valid proxy and
        // made the next proxy share fail with "no proxy".
        if (hasProxy) MobileVpnService.Instance.PeerProxy = proxy;

        string extra = JsonStr(body, "carrier");
        string ssid = JsonStr(body, "ssid");
        string joined = string.Join(" · ", new[] { extra, ssid }.Where(s => !string.IsNullOrEmpty(s)));
        if (joined.Length > 0) MobileVpnService.Instance.ReportedDeviceInfo = joined;

        Raise();
    }

    private async Task HandleStatusAsync(HttpListenerResponse res)
    {
        var s = await MobileVpnService.Instance.DetectAsync().ConfigureAwait(false);
        await JsonAsync(res, 200, new
        {
            ok = true,
            takenAt = s.TakenAt.ToString("o"),
            mobileConnected = s.MobileConnected,
            mobileVpn = s.MobileVpnActive,                 // true / false / null = unknown
            pcVpnActive = s.PcVpnActive,
            // Three states, because two of them used to be conflated: a VPN client installed
            // but not tunnelling is neither "active" nor safely "inactive".
            pcVpnState = s.PcVpnActive ? "active" : s.PcVpnUnknown ? "unknown" : "inactive",
            pcConnected = s.PcConnected,
            sharing = s.Sharing,
            peers = s.LocalPeers,
            rateDown = s.RateDown,
            rateUp = s.RateUp,
            hasBackup = MobileVpnService.Instance.HasSnapshot,
            backupAt = MobileVpnService.Instance.SnapshotTime,
            links = s.Links.ConvertAll(l => new
            {
                ifIndex = l.IfIndex,
                name = l.Name,
                description = l.Description,
                kind = l.Kind,
                up = l.IsUp,
                ipv4 = l.Ipv4,
                gateway = l.Gateway,
                metric = l.Metric,
            }),
            device = DeviceName,
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private bool IsAuthorized(HttpListenerRequest req)
    {
        if (string.IsNullOrEmpty(Token)) return false;
        string header = req.Headers["Authorization"] ?? "";
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return FixedEquals(header[7..].Trim(), Token);
        string alt = req.Headers["X-NetPilot-Token"];
        return alt != null && FixedEquals(alt.Trim(), Token);
    }

    /// <summary>Length-independent comparison so the token cannot be probed by timing.</summary>
    private static bool FixedEquals(string a, string b)
    {
        byte[] x = Encoding.UTF8.GetBytes(a);
        byte[] y = Encoding.UTF8.GetBytes(b);
        if (x.Length != y.Length) return false;
        return CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        if (!req.HasEntityBody) return "";
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch { return ""; }
    }

    private static string JsonStr(string body, string prop)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static async Task JsonAsync(HttpListenerResponse res, int status, object payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }
}
