using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>
/// Detects the phone link, keeps a restorable snapshot of the PC network state and
/// moves traffic onto the phone link. Two hard rules:
///   1. every mutation is preceded by a snapshot, so Restore can undo it;
///   2. an active PC VPN is never touched unless the user explicitly opted in.
/// The phone side talks to us through <see cref="MobileVpnApi"/> (the Android app
/// pairs, then reports its VPN state over the same channel).
/// </summary>
public sealed class MobileVpnService
{
    public static readonly MobileVpnService Instance = new();

    public const int ApiPort = 8787;
    public static string SnapshotPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "NetPilot", "mobile_vpn_snapshot.json");

    /// <summary>Reported by the paired Android app: null until it says something.</summary>
    public bool? ReportedMobileVpn { get; set; }
    public string ReportedDeviceInfo { get; set; } = "";
    public DateTime? ReportedAt { get; set; }

    /// <summary>
    /// "host:port" of the HTTP/SOCKS proxy the paired phone offers, taken from its
    /// /report payload. The proxy share mode points WinHTTP at this - never at our own
    /// API port, which is a REST endpoint and would simply swallow every request.
    /// </summary>
    public string PeerProxy { get; set; } = "";
    /// <summary>True while this app has redirected traffic onto the phone link.</summary>
    public bool IsSharing { get; private set; }

    /// <summary>True when a *previous* run left changes behind (see the marker file).</summary>
    public bool NeedsRestore => IsSharing || PendingOnDisk;

    private long _prevRx;
    private long _prevTx;
    private DateTime _prevAt = DateTime.MinValue;
    /// <summary>Last probe that actually ran, so a failed probe can re-serve it instead of
    /// reporting an empty network.</summary>
    private MvState _lastGood;
    /// <summary>Identity of the link the last sample came from, so a rate is never
    /// computed as the difference between two different adapters' counters.</summary>
    private int _prevIfIndex;
    private string _proxyApplied;

    /// <summary>
    /// Marker file for "this app changed the machine's network settings and has not put them
    /// back yet".
    ///
    /// The flag used to live only in memory, so a restart made the app forget it: the page
    /// showed "ready" while the interface metrics (or the system-wide WinHTTP proxy) were
    /// still ours, and the next Share captured the *modified* state as the restore point -
    /// after which the user's real settings were unrecoverable. On disk it survives a crash
    /// or a reboot, which is exactly when it is needed.
    /// </summary>
    private const string PendingMarkerPath = @"E:\op dn\NetPilot\pending-share.flag";
    private static readonly object MarkerLock = new();

    private static bool PendingOnDisk
    {
        get
        {
            try { return File.Exists(PendingMarkerPath); }
            catch { return false; }
        }
    }

    private static void WriteMarker(string what)
    {
        lock (MarkerLock)
        {
            try
            {
                File.WriteAllText(PendingMarkerPath, what + "|" + DateTime.Now.ToString("O"));
                // The app's data directory is the natural home; if this folder is read-only
                // (portable install on a locked drive) the in-memory flag still carries the
                // same information for this session.
            }
            catch { }
        }
    }

    private static void ClearMarker()
    {
        lock (MarkerLock)
        {
            try { if (File.Exists(PendingMarkerPath)) File.Delete(PendingMarkerPath); }
            catch { }
        }
    }

    private const int DetectCacheMs = 3000;
    private readonly object _detectLock = new();
    private MvState _detectCached;
    private DateTime _detectCachedAt;
    private Task<MvState> _detectRunning;

    // The classifier moved to NetPilot.Core so Linux, macOS and Windows answer "what is this
    // interface?" the same way. These forwarders keep every existing call site - and the
    // tests that pin this behaviour - working against the single shared implementation.
    public static string ClassifyLink(string description, string name, bool isVpn) =>
        LinkClassifier.ClassifyLink(description, name, isVpn);

    internal static bool HasUsableIpv4(string list) => LinkClassifier.HasUsableIpv4(list);

    private static bool IsVpnLike(string description, string name) =>
        LinkClassifier.IsVpnLike(description, name);

    // ------------------------------------------------------------------ detect

    /// <summary>
    /// Cached wrapper around the probe.
    ///
    /// The probe shells out to PowerShell, which costs seconds on a cold or busy machine.
    /// The desktop polls it while the phone polls it too, and without a cache every poll
    /// started another PowerShell - the phone's HTTP client gave up long before the answer
    /// was ready and reported the bridge as unreachable. Requests that arrive while a probe
    /// is running share its result instead of stacking up another process.
    ///
    /// <paramref name="force"/> bypasses the cached answer (still sharing a running probe).
    /// The rate readout needs it: the second sample of a link has to be a *new* measurement,
    /// not the same zero-by-definition baseline the first one returned.
    /// </summary>
    public Task<MvState> DetectAsync(bool force = false)
    {
        lock (_detectLock)
        {
            if (!force && _detectCached != null &&
                DateTime.Now - _detectCachedAt < TimeSpan.FromMilliseconds(DetectCacheMs))
                return Task.FromResult(_detectCached);

            if (_detectRunning != null) return _detectRunning;
            _detectRunning = DetectAndCacheAsync();
            return _detectRunning;
        }
    }

    private async Task<MvState> DetectAndCacheAsync()
    {
        try
        {
            var state = await DetectCoreAsync().ConfigureAwait(false);
            lock (_detectLock)
            {
                _detectCached = state;
                _detectCachedAt = DateTime.Now;
            }
            return state;
        }
        finally
        {
            lock (_detectLock) { _detectRunning = null; }
        }
    }

    /// <summary>Full probe. Runs PowerShell on a background thread; never touches the UI.</summary>
    private async Task<MvState> DetectCoreAsync()
    {
        var state = new MvState { TakenAt = DateTime.Now };

        var (ok, output) = await Sys.PsStdoutAsync(DetectScript, 20000).ConfigureAwait(false);
        if (!ok || !Sys.TryExtractJson(output, out var json)) return FailedProbeState();

        bool pcVpnUp = false;
        bool pcVpnUnconfirmed = false;
        int vpnConn = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("peers", out var peers))
                state.LocalPeers = peers.ValueKind == JsonValueKind.Number ? peers.GetInt32() : 0;
            if (root.TryGetProperty("vpnConn", out var vc) && vc.ValueKind == JsonValueKind.Number)
                vpnConn = vc.GetInt32();

            if (root.TryGetProperty("adapters", out var ads) && ads.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in ads.EnumerateArray())
                {
                    string desc = Str(a, "Desc");
                    string name = Str(a, "Name");
                    string status = Str(a, "Status");

                    var link = new MvLink
                    {
                        // Guarded like every other field here: the throwing accessor made a
                        // single odd adapter abort the whole enumeration, and the catch then
                        // returned a *truncated* link list that the UI showed as complete.
                        IfIndex = (int)Long(a, "IfIndex"),
                        Name = name,
                        Description = desc,
                        IsUp = string.Equals(status, "Up", StringComparison.OrdinalIgnoreCase),
                        Ipv4 = Str(a, "Ipv4"),
                        Gateway = Str(a, "Gateway"),
                        Rx = Long(a, "Rx"),
                        Tx = Long(a, "Tx"),
                        Metric = (int)Long(a, "Metric"),
                    };
                    link.IsVpn = AdapterService.Classify(desc) == AdapterKind.Vpn || IsVpnLike(desc, name);
                    link.Kind = ClassifyLink(desc, name, link.IsVpn);

                    // VPN adapters get their own tile, so keep them out of the link list -
                    // but they are exactly what the "PC VPN" indicator is built from.
                    if (link.IsVpn)
                    {
                        // "Up" only means the virtual NIC is connected at the driver level,
                        // which is true on most machines that merely *have* a VPN client
                        // installed but are not tunnelling. Treating that as a live tunnel is
                        // what made the page claim "PC VPN connected" on machines where it
                        // was not - and a wrong "active" also blocks sharing. An established
                        // tunnel carries a usable address; an idle one is empty or APIPA.
                        if (link.IsUp)
                        {
                            if (HasUsableIpv4(link.Ipv4)) pcVpnUp = true;
                            else pcVpnUnconfirmed = true;
                        }
                        continue;
                    }
                    state.Links.Add(link);
                }
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }

        // A Windows VPN profile that reports Connected is authoritative and needs no
        // corroboration. Otherwise fall back to the adapters, and if a VPN-looking adapter
        // is up but carries no address, say "unknown" rather than guess either way.
        state.PcVpnActive = vpnConn > 0 || pcVpnUp;
        state.PcVpnUnknown = !state.PcVpnActive && pcVpnUnconfirmed;
        state.PcConnected = state.Links.Any(l => l.IsUp && !l.IsVpn && !string.IsNullOrEmpty(l.Ipv4) &&
                                                  !string.IsNullOrEmpty(l.Gateway));
        state.MobileVpnActive = ReportedMobileVpn;
        // NeedsRestore, not IsSharing: after a restart the machine is still routed onto the
        // phone, and the page has to say so instead of claiming everything is back to normal.
        state.Sharing = NeedsRestore;

        // Phone considered linked when a tether/hotspot interface is live, or when the
        // Android app has paired with the bridge and reported recently.
        bool reportedRecently = ReportedAt.HasValue && DateTime.Now - ReportedAt.Value < TimeSpan.FromMinutes(5);
        state.MobileConnected =
            state.Links.Any(l => (l.Kind == "usb" || l.Kind == "hotspot") && l.IsUp) || reportedRecently;

        // Traffic rate over the chosen link.
        var linkNow = state.AnyUpLink;
        long prevRxBefore = _prevRx, prevTxBefore = _prevTx;
        int prevIdxBefore = _prevIfIndex;
        DateTime prevAtBefore = _prevAt;
        if (linkNow != null)
        {
            var now = DateTime.UtcNow;
            bool sameLink = _prevAt != DateTime.MinValue && _prevIfIndex == linkNow.IfIndex;
            if (sameLink && (now - _prevAt).TotalMilliseconds > 200)
            {
                double secs = (now - _prevAt).TotalSeconds;
                state.RateDown = (long)Math.Max(0, (linkNow.Rx - _prevRx) / secs);
                state.RateUp = (long)Math.Max(0, (linkNow.Tx - _prevTx) / secs);
            }
            else
            {
                state.WasBaseline = true;
            }
            _prevRx = linkNow.Rx;
            _prevTx = linkNow.Tx;
            _prevAt = now;
            _prevIfIndex = linkNow.IfIndex;
        }
        else
        {
            state.WasBaseline = true;
        }

        LogProbe(linkNow, prevRxBefore, prevIdxBefore, prevAtBefore, state);

        _lastGood = state;
        return state;
    }

    /// <summary>Result of a probe that could not run at all (PowerShell failure/timeout).
    /// Returning an empty state here made the page flicker to "disconnected" for one poll
    /// and - because a fresh MvState has WasBaseline = false - the caller took that empty
    /// state as final instead of re-sampling. Re-serve the last good sample and ask for a
    /// retry instead.</summary>
    private MvState FailedProbeState()
    {
        var prev = _lastGood;
        if (prev == null)
            return new MvState { TakenAt = DateTime.Now, WasBaseline = true, MobileVpnActive = null };

        return new MvState
        {
            TakenAt = prev.TakenAt,
            Links = new List<MvLink>(prev.Links),
            MobileConnected = prev.MobileConnected,
            MobileVpnActive = prev.MobileVpnActive,
            PcVpnActive = prev.PcVpnActive,
            PcVpnUnknown = prev.PcVpnUnknown,
            PcConnected = prev.PcConnected,
            LocalPeers = prev.LocalPeers,
            RateDown = prev.RateDown,
            RateUp = prev.RateUp,
            Sharing = IsSharing,
            WasBaseline = true,   // no fresh counters were read: never present these as current
        };
    }

    /// <summary>One line per probe in mv_probe.log - only used while diagnosing the
    /// Traffic Status tile; failures here must never disturb the probe itself.</summary>
    private void LogProbe(MvLink link, long prevRx, int prevIdx, DateTime prevAt, MvState state)
    {
        try
        {
            string dir = Path.GetDirectoryName(SnapshotPath)!;
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "mv_probe.log");
            var fi = new FileInfo(file);
            if (fi.Exists && fi.Length > 65536) File.Delete(file);

            double gap = prevAt == DateTime.MinValue ? -1 : (DateTime.UtcNow - prevAt).TotalSeconds;
            File.AppendAllText(file,
                $"{DateTime.Now:HH:mm:ss.fff}\tlink={(link == null ? "NULL" : link.Name)}" +
                $" idx={(link == null ? -1 : link.IfIndex)} rx={(link == null ? -1 : link.Rx)}" +
                $" tx={(link == null ? -1 : link.Tx)}\tprevRx={prevRx} prevIdx={prevIdx}" +
                $" gap={gap:0.00}\trateDown={state.RateDown} rateUp={state.RateUp}" +
                $" base={(state.WasBaseline ? 1 : 0)}" +
                $" links={state.Links.Count} pcVpn={state.PcVpnActive}{Environment.NewLine}");
        }
        catch { }
    }

    // ------------------------------------------------------------------ backup

    public bool HasSnapshot => File.Exists(SnapshotPath);

    public string SnapshotTime =>
        HasSnapshot ? File.GetLastWriteTime(SnapshotPath).ToString("yyyy-MM-dd HH:mm") : "";

    /// <summary>Captures adapter metrics, default routes, DNS, VPN entries and proxy settings.</summary>
    public async Task<MvResult> CaptureSnapshotAsync()
    {
        var (ok, output) = await Sys.PsStdoutAsync(SnapshotScript, 20000).ConfigureAwait(false);
        if (!ok || !Sys.TryExtractJson(output, out var json))
            return MvResult.Fail("mv_error", output);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath));
            File.WriteAllText(SnapshotPath, json);
            return MvResult.Success("mv_snapshot_saved");
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            return MvResult.Fail("mv_error", ex.Message);
        }
    }

    /// <summary>Puts back everything this feature is allowed to change.</summary>
    public async Task<MvResult> RestoreSnapshotAsync()
    {
        if (!HasSnapshot) return MvResult.Fail("mv_snapshot_none");
        string json;
        try { json = File.ReadAllText(SnapshotPath); }
        catch (Exception ex) { return MvResult.Fail("mv_error", ex.Message); }

        // Only revert the proxy if this app was the one that wrote it - otherwise we would
        // silently overwrite a change the user made afterwards. The marker file widens that
        // to previous runs: a proxy this app installed survives our restart, and without the
        // marker every WinHTTP request on the machine kept going through the (now gone) phone.
        bool revertProxy = _proxyApplied != null || PendingOnDisk;
        string script = RestoreScript
            .Replace("{{SNAPSHOT}}", json.Replace("'", "''"))
            .Replace("{{REVERT_PROXY}}", revertProxy ? "1" : "0");

        var (ok, output) = await Sys.PsStdoutAsync(script, 25000).ConfigureAwait(false);
        if (!ok) return MvResult.Fail("mv_error", output);

        // The script reports how many adapters it actually restored. All of them failing is
        // a failed restore, not a successful one - reporting success there left the machine
        // on our metrics while the UI said everything was back to normal.
        int restored = ReadCount(output, "RESTORED=");
        int failed = ReadCount(output, "FAILED=");
        if (restored <= 0 && failed > 0)
        {
            // Deliberately leave IsSharing and the marker alone when the restore did not
            // work: the machine may still carry our metrics, so the app still owes a
            // restore and every later share must keep refusing to re-capture a snapshot
            // over the top of it. Clearing them here would look tidier and would strand
            // the machine on our settings with nothing left to repair it.
            return MvResult.Fail("mv_restore_failed", output);
        }

        _proxyApplied = null;
        IsSharing = false;
        ClearMarker();
        return failed > 0
            ? MvResult.Fail("mv_restore_partial", $"restored {restored}, failed {failed}")
            : MvResult.Success("mv_snapshot_restored");
    }

    // ------------------------------------------------------------------ share

    /// <summary>
    /// Moves PC traffic onto the phone link.
    /// <paramref name="method"/> is "route" (default-route metric) or "proxy" (WinHTTP/WinINET proxy).
    /// </summary>
    public async Task<MvResult> ShareAsync(MvLink link, MvMethod method, bool allowVpnChange)
    {
        // Proxy mode only rewrites WinHTTP/WinINET - it never touches an interface, so
        // demanding a recognised uplink here refused the share on any machine whose link
        // detection came back empty (probe failure, VPN-only box), for no reason at all.
        // Route mode still needs the interface it is about to re-metric.
        if (method != MvMethod.Proxy && (link == null || link.IfIndex <= 0))
            return MvResult.Fail("mv_no_link");

        // Fresh, not cached: sharing rewrites interface metrics, so it must not act on an
        // answer that was measured before the last change.
        var probe = await DetectAsync(force: true).ConfigureAwait(false);
        if (probe.PcVpnActive && !allowVpnChange) return MvResult.Fail("mv_blocked_vpn");

        // Requirement: keep the current state before touching anything - but only the
        // FIRST time. Re-capturing while already sharing would record our own modified
        // metrics and Restore could never bring the original state back. NeedsRestore also
        // covers a previous run that left the marker behind, which is when re-capturing
        // would be worst: it would snapshot our own numbers as the "original" ones.
        if (NeedsRestore)
        {
            if (!HasSnapshot) return MvResult.Fail("mv_snapshot_none");
        }
        else
        {
            var snap = await CaptureSnapshotAsync().ConfigureAwait(false);
            if (!snap.Ok) return snap;
        }

        return method == MvMethod.Proxy
            ? await ApplyProxyAsync(link).ConfigureAwait(false)
            : await ApplyRouteAsync(link).ConfigureAwait(false);
    }

    private static async Task<MvResult> ApplyRouteAsync(MvLink link)
    {
        // Only the interface metric is touched: Windows resolves a default route as
        // InterfaceMetric + RouteMetric, so lowering the phone link and raising the
        // rest moves PC traffic onto the phone. No adapter is disabled, no address is
        // rewritten and no VPN profile is modified - Restore puts every metric back.
        // Every Set-NetIPInterface used to be silenced and unchecked, so a run where all of
        // them failed (non-elevated, a driver in the way) still reported "share started"
        // while the routing preference had not moved at all. Count what actually happened.
        string script = $@"
$target = {link.IfIndex}
$done = 0
$failed = 0
foreach ($_ in @(Get-NetAdapter -ErrorAction SilentlyContinue)) {{
  $i = $_.ifIndex
  $if4 = Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue
  if (-not $if4) {{ continue }}
  $want = if ($i -eq $target) {{ 5 }} else {{ 50 }}
  try {{
    Set-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric $want -ErrorAction Stop
    $done++
  }} catch {{ $failed++ }}
}}
'CHANGED=' + $done
'FAILED=' + $failed";
        var (ok, output) = await Sys.PsAsync(script, 25000).ConfigureAwait(false);
        if (!ok) return MvResult.Fail("mv_error", output);

        int changed = ReadCount(output, "CHANGED=");
        if (changed <= 0)
        {
            // Nothing was re-metrored: say so instead of showing a green "sharing active".
            return MvResult.Fail("mv_no_change", output);
        }
        Instance.IsSharing = true;
        WriteMarker("share");
        return MvResult.Success("mv_share_started");
    }

    /// <summary>Reads "KEY=123" out of a PowerShell transcript; -1 when the key is missing.</summary>
    internal static int ReadCount(string output, string key)
    {
        foreach (var line in (output ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(key, StringComparison.Ordinal))
            {
                var rest = t.Substring(key.Length).Trim();
                if (int.TryParse(rest, out var v)) return v;
            }
        }
        return -1;
    }

    private async Task<MvResult> ApplyProxyAsync(MvLink link)
    {
        // The proxy must be the one the phone offers. Our own HTTP listener on
        // ApiPort speaks the REST API, so pointing WinHTTP at it would black-hole
        // every request instead of carrying it through the phone.
        string proxy = (PeerProxy ?? "").Trim();
        if (proxy.Length == 0) return MvResult.Fail("mv_no_proxy");

        // WinHTTP is system-wide: applying a proxy that no longer answers takes the whole
        // machine offline, and the phone may have moved to another IP since the report that
        // carried this address. Prove it answers before a single byte is written.
        if (!await ProxyReachableAsync(proxy).ConfigureAwait(false))
            return MvResult.Fail("mv_proxy_unreachable");

        string script = $@"
netsh winhttp set proxy proxy-server=""{proxy}"" | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyEnable -Value 1 -ErrorAction SilentlyContinue
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyServer -Value '{proxy}' -ErrorAction SilentlyContinue
'OK'";
        var (ok, output) = await Sys.PsAsync(script, 20000).ConfigureAwait(false);
        if (!ok || !output.Contains("OK")) return MvResult.Fail("mv_error", output);

        _proxyApplied = proxy;
        IsSharing = true;
        WriteMarker("share");
        return MvResult.Success("mv_share_started");
    }

    /// <summary>
    /// True only when the far end actually answers as an HTTP proxy.
    /// </summary>
    /// <remarks>
    /// A TCP handshake is not proof of reachability: networks that intercept or masquerade
    /// connections accept a connect to an address that is not there at all (TEST-NET-1
    /// does, on some LANs), and applying that address as the system proxy black-holes every
    /// WinHTTP request on this machine. So after the connect we make the far end speak:
    /// <c>CONNECT</c> to a port nobody listens on makes a real proxy reply immediately
    /// (NetPilot's own answers 502, with no DNS lookup and no outbound connection), while a
    /// middlebox that merely swallowed the handshake stays silent and is refused here.
    /// </remarks>
    internal static async Task<bool> ProxyReachableAsync(string hostPort)
    {
        string host = hostPort;
        int port = 8080;
        int colon = hostPort.LastIndexOf(':');
        if (colon > 0 && int.TryParse(hostPort.Substring(colon + 1), out int p) && p > 0 && p < 65536)
        {
            port = p;
            host = hostPort.Substring(0, colon);
        }
        if (string.IsNullOrWhiteSpace(host)) return false;

        // Resolve here instead of handing the name to TcpClient: ConnectAsync(host, port)
        // tries the resolver's addresses in order under a single deadline, so the first
        // address can eat the whole budget. On a host where the IPv6 loopback answers
        // nothing, "localhost" resolved to ::1 first and stalled until the 1500 ms timeout
        // expired - the 127.0.0.1 listener was never tried, and a perfectly reachable proxy
        // was reported as unreachable (Share then refused with mv_proxy_unreachable). Every
        // address now gets its own attempt, so one dead address family cannot hide a live one.
        System.Net.IPAddress[] addrs;
        try { addrs = await System.Net.Dns.GetHostAddressesAsync(host).ConfigureAwait(false); }
        catch { return false; }
        if (addrs == null || addrs.Length == 0) return false;

        foreach (var addr in addrs)
            if (await ProbeAddressAsync(addr, port).ConfigureAwait(false)) return true;
        return false;
    }

    private static async Task<bool> ProbeAddressAsync(System.Net.IPAddress addr, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient(addr.AddressFamily);
            var connect = client.ConnectAsync(addr, port);
            if (await Task.WhenAny(connect, Task.Delay(1200)).ConfigureAwait(false) != connect)
                return false;
            await connect.ConfigureAwait(false);
            if (!client.Connected) return false;

            using var stream = client.GetStream();
            var probe = System.Text.Encoding.ASCII.GetBytes(
                "CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(probe, 0, probe.Length).ConfigureAwait(false);

            var buf = new byte[5];
            using var cts = new System.Threading.CancellationTokenSource(1500);
            int got = 0;
            while (got < buf.Length)
            {
                int n = await stream.ReadAsync(buf, got, buf.Length - got, cts.Token)
                    .ConfigureAwait(false);
                if (n <= 0) break;
                got += n;
            }

            return got == buf.Length &&
                   string.Equals(System.Text.Encoding.ASCII.GetString(buf, 0, got), "HTTP/",
                       StringComparison.Ordinal);
        }
        catch { return false; }   // refused / reset / silence: not a proxy
    }

    public Task<MvResult> StopShareAsync() => RestoreSnapshotAsync();

    // ------------------------------------------------------------------ scripts

    private const string DetectScript = @"
$ad = @()
foreach ($_ in @(Get-NetAdapter -ErrorAction SilentlyContinue)) {
  $i = $_.ifIndex
  $cfg = Get-NetIPConfiguration -InterfaceIndex $i -ErrorAction SilentlyContinue
  $v4 = ''; $gw = ''
  if ($cfg) {
    $v4 = (@($cfg.IPv4Address.IPAddress) -join ',')
    $gw = (@($cfg.IPv4DefaultGateway.NextHop) -join ',')
  }
  $st = Get-NetAdapterStatistics -Name ([string]$_.Name) -ErrorAction SilentlyContinue
  $if4 = Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue
  $ad += [pscustomobject]@{
    IfIndex = [int]$i
    Name = [string]$_.Name
    Desc = [string]$_.InterfaceDescription
    Status = [string]$_.Status
    Ipv4 = [string]$v4
    Gateway = [string]$gw
    HasStats = [bool]$st
    Rx = [long]$(if ($st) { [long]$st.ReceivedBytes } else { 0 })
    Tx = [long]$(if ($st) { [long]$st.SentBytes } else { 0 })
    Metric = [int]$(if ($if4) { $if4.InterfaceMetric } else { 0 })
  }
}
# Some drivers (common on cheap USB Wi-Fi dongles) publish no statistics object at
# all, which would leave Traffic Status pinned to zero. Those counters still appear
# in the raw performance class, keyed by interface description - use them as fallback.
$missing = @($ad | Where-Object { -not $_.HasStats })
if ($missing.Count -gt 0) {
  $perf = @(Get-CimInstance Win32_PerfRawData_Tcpip_NetworkInterface -ErrorAction SilentlyContinue)
  foreach ($a in $missing) {
    $key = ([string]$a.Desc) -replace '#','_'
    $hit = @($perf | Where-Object { (([string]$_.Name) -replace '#','_') -eq $key }) | Select-Object -First 1
    if ($hit) {
      $a.Rx = [long]$hit.BytesReceivedPersec
      $a.Tx = [long]$hit.BytesSentPersec
      $a.HasStats = $true
    }
  }
}
$peers = 0
try {
  $peers = @(Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
      Where-Object { $_.State -ne 'Unreachable' -and $_.IPAddress -notlike '224*' -and
                     $_.IPAddress -notlike '255*' -and $_.IPAddress -ne '0.0.0.0' }).Count
} catch { }
$vpnConn = 0
try {
  $vpnConn = @(Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue |
      Where-Object { [string]$_.ConnectionStatus -eq 'Connected' }).Count
} catch { }
[pscustomobject]@{ adapters = $ad; peers = [int]$peers; vpnConn = [int]$vpnConn } | ConvertTo-Json -Compress -Depth 6";

    private const string SnapshotScript = @"
$ad = @(Get-NetAdapter -ErrorAction SilentlyContinue | ForEach-Object {
  $i = $_.ifIndex
  $if4 = Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue
  $dns = @((Get-DnsClientServerAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
  [pscustomobject]@{
    IfIndex = [int]$i
    Name = [string]$_.Name
    Metric = [int]$(if ($if4) { $if4.InterfaceMetric } else { 0 })
    AutoMetric = [string]$(if ($if4) { $if4.AutomaticMetric } else { '' })
    Dns = [string]($dns -join ',')
  }
})
$routes = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object {
  [pscustomobject]@{ IfIndex = [int]$_.ifIndex; Metric = [int]$_.RouteMetric; NextHop = [string]$_.NextHop }
})
$vpn = @(Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue | ForEach-Object {
  [pscustomobject]@{ Name = [string]$_.Name; Server = [string]$_.ServerAddress }
})
$winHttp = ''
$raw = ((netsh winhttp show proxy) | Out-String)
if ($raw -match 'Proxy Server\(s\)\s*:\s*(.+)') { $winHttp = $Matches[1].Trim() }
if ($winHttp -like '*<none>*') { $winHttp = '' }
$inet = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
$inetEnable = 0; $inetServer = ''
if ($inet) {
  $inetEnable = [int]$(if ($null -ne $inet.ProxyEnable) { $inet.ProxyEnable } else { 0 })
  $inetServer = [string]$(if ($null -ne $inet.ProxyServer) { $inet.ProxyServer } else { '' })
}
[pscustomobject]@{
  takenAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
  adapters = $ad
  routes = $routes
  vpn = $vpn
  winHttp = [string]$winHttp
  inetEnable = [int]$inetEnable
  inetServer = [string]$inetServer
} | ConvertTo-Json -Compress -Depth 6";

    private const string RestoreScript = @"
$snap = '{{SNAPSHOT}}' | ConvertFrom-Json
$ok = 0
$fail = 0
foreach ($a in @($snap.adapters)) {
  $idx = [int]$a.IfIndex
  if ($idx -le 0) { continue }
  # Windows computes the effective default-route metric as InterfaceMetric +
  # RouteMetric, and only when AutomaticMetric is off. Put both back exactly as
  # they were captured.
  $m = [int]$a.Metric
  try {
    if ([string]$a.AutoMetric -eq 'Enabled' -or $m -le 0) {
      Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop
    } else {
      Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric $m -ErrorAction Stop
    }
    $ok++
  } catch { $fail++ }
}
if ('{{REVERT_PROXY}}' -eq '1') {
  $w = [string]$snap.winHttp
  try { if ($w) { netsh winhttp set proxy proxy-server=""$w"" | Out-Null } else { netsh winhttp reset proxy | Out-Null } } catch { }
  $p = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
  Set-ItemProperty -Path $p -Name ProxyEnable -Value ([int]$snap.inetEnable) -ErrorAction SilentlyContinue
  Set-ItemProperty -Path $p -Name ProxyServer -Value ([string]$snap.inetServer) -ErrorAction SilentlyContinue
}
# Report what actually happened. This script used to print a bare 'OK' no matter how many
# adapters it had failed on, so RestoreSnapshotAsync claimed success while the machine kept
# our metrics - and the next Share then captured those wrong numbers as the new 'original'.
'RESTORED=' + $ok
'FAILED=' + $fail";

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
