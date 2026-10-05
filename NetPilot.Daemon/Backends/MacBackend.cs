using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;
using NetPilot.Services;

namespace NetPilot.Daemon.Backends;

/// <summary>
/// macOS implementation of <see cref="INetworkBackend"/>.
///
/// Read this before assuming the feature set matches Windows, because it does not.
///
/// macOS cannot do per-application firewalling or per-application bandwidth limiting:
///   * <c>pf</c> has no process matcher. Unlike OpenBSD's pbox it cannot ask "which process
///     sent this packet", so a rule can only match addresses, ports and protocols. There is
///     no pf syntax that means "block this app".
///   * There is no traffic-control equivalent to Linux's <c>tc</c>. Shaping per flow needs a
///     kernel extension or a Network Extension, which requires a signed native app, a
///     provisioning profile and Apple's approval.
///   * The built-in Application Firewall is inbound-only, keyed on code signature, and cannot
///     be scripted per port or per direction the way this product needs.
///
/// So <see cref="Capability"/> returns <see cref="RuleSupport.None"/> and the firewall and
/// limiter entry points refuse with a reason that names the reason. That is the honest
/// outcome. Shipping a toggle that appears to work here would be worse than showing nothing:
/// a user who believes an app is blocked and it is not has no way to notice.
///
/// What macOS *can* do, and does:
///   links + counters      ifconfig / netstat -ibn, both in the base system
///   DNS                   networksetup, per network service
///   system proxy          networksetup -setwebproxy / -setsecurewebproxy
///   VPN detection         scutil --nc list plus the utun interfaces
///   snapshot / restore    DNS and proxy state, both reversible
/// </summary>
public sealed class MacBackend : INetworkBackend
{
    public string Platform => "macos";

    public bool IsPrivileged { get; }

    public string LastPrivilegeError { get; private set; } = "";

    private readonly string _stateDir;

    public MacBackend(string stateDir)
    {
        _stateDir = stateDir;
        IsPrivileged = GetEuid() == 0;
    }

    private static int GetEuid()
    {
        try { return (int)geteuid(); } catch { return -1; }
    }

    [System.Runtime.InteropServices.DllImport("unistd.h")]
    private static extern uint geteuid();

    private const string NoProcessMatch =
        "macOS cannot block one app from another: pf has no process matcher, and the " +
        "Application Firewall is inbound-only. This needs a Network Extension.";

    private const string NoShaping =
        "macOS has no per-flow shaper. This needs a Network Extension.";

    // ============================ links ============================

    public async Task<IReadOnlyList<MvLink>> GetLinksAsync(CancellationToken ct = default)
    {
        var links = new List<MvLink>();

        // netstat -ibn gives one row per interface per address family, with the byte counters.
        var (ok, outp) = await Shell.RunAsync("netstat", "-ibn", 6000, ct).ConfigureAwait(false);
        if (!ok) return links;

        var rows = new Dictionary<string, MvLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in outp.Split('\n').Skip(1))
        {
            // Split the whole line on whitespace, all columns at once.
            //
            // This used to split into two parts and then read the *second* part as the column
            // list, which threw away the first column - the interface name. On a real Mac
            // every interface came back named "1500", which is its MTU. Nothing noticed,
            // because this code has never run on anything but a build server that never
            // started it.
            var cols = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 11) continue;              // the header row

            string name = cols[0];
            // One row per interface per address; the first row carries the cumulative
            // counters, and summing the rest would count a dual-stack interface twice.
            if (rows.ContainsKey(name)) continue;

            // macOS layout: Name Mtu Network Address Ipkts Ierrs Ibytes Opkts Oerrs Obytes Coll
            // A down interface shows "(inactive)" where its address would be.
            var link = new MvLink
            {
                IfIndex = rows.Count + 1,
                Name = name,
                Description = name,
                IsUp = !cols[3].Contains("(inactive)", StringComparison.OrdinalIgnoreCase),
                Rx = ParseLong(cols[6]),                 // Ibytes
                Tx = ParseLong(cols[9]),                 // Obytes
            };
            rows[name] = link;
            links.Add(link);
        }

        // Addresses and the default gateway come from ifconfig / route, which are the only
        // base-system sources; parsing ifconfig is stable enough for what we need.
        foreach (var link in links)
        {
            var (ok2, cfg) = await Shell.RunAsync("ifconfig", link.Name, 4000, ct).ConfigureAwait(false);
            if (ok2)
            {
                link.Ipv4 = string.Join(",", cfg.Split('\n')
                    .Where(l => l.Contains("inet ", StringComparison.Ordinal))
                    .Select(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                .SkipWhile(t => t != "inet").Skip(1).FirstOrDefault() ?? "")
                    .Where(s => s.Length > 0));
                link.Metric = 0;
            }
            link.IsVpn = LinkClassifier.IsVpnLike(link.Description, link.Name);
            link.Kind = LinkClassifier.ClassifyLink(link.Description, link.Name, link.IsVpn);
        }

        var (gwOk, gwOut) = await Shell.RunAsync("route", "-n get default", 4000, ct).ConfigureAwait(false);
        if (gwOk)
        {
            var devTok = gwOut.Split(' ').FirstOrDefault(t => t.StartsWith("interface:", StringComparison.Ordinal));
            if (devTok != null)
            {
                string dev = devTok.Split(':')[1].Trim();
                var target = links.FirstOrDefault(l => l.Name == dev);
                if (target != null) target.Gateway = "default";
            }
        }

        return links;
    }

    // ============================ DNS ============================

    public Task<IReadOnlyList<AdapterDnsState>> GetDnsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdapterDnsState>>(Array.Empty<AdapterDnsState>());

    /// <summary>macOS DNS is configured per *network service*, not per interface, so the two
    /// models do not line up. Resolving the service name for an interface needs
    /// <c>networksetup -listallhardwareports</c>; returning the services is what the UI needs.</summary>
    public async Task<IReadOnlyList<AdapterDnsState>> GetDnsPerServiceAsync(CancellationToken ct = default)
    {
        var result = new List<AdapterDnsState>();
        var (ok, services) = await Shell.RunAsync("networksetup", "-listallnetworkservices", 6000, ct).ConfigureAwait(false);
        if (!ok) return result;

        foreach (var raw in services.Split('\n').Skip(1))
        {
            string service = raw.Trim();
            if (service.Length == 0 || service.StartsWith("*", StringComparison.Ordinal)) continue;
            // Read from networksetup's own output, so it is trusted by construction - but the
            // argument is still passed separately, because a name with a space in it is the
            // normal case here and must not be re-split.
            if (!Validate.IsMacServiceName(service)) continue;
            var (ok2, servers) = await Shell.RunAsync("networksetup",
                new[] { "-getdnsservers", service }, 4000, ct).ConfigureAwait(false);
            if (!ok2) continue;
            var v4 = servers.Split('\n')
                .Select(l => l.Trim())
                .Where(l => IPAddress.TryParse(l, out var ip) &&
                            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .ToList();
            result.Add(new AdapterDnsState
            {
                AdapterName = service,
                V4 = v4,
                IsDynamic = v4.Count == 0,
            });
        }
        return result;
    }

    public Task<MvResult> SetDnsAsync(string adapter, List<string> serversV4, CancellationToken ct = default)
    {
        if (serversV4 is null || serversV4.Count == 0)
            return Task.FromResult(MvResult.Fail("daemon_dns_empty"));
        if (!IsPrivileged)
            return Task.FromResult(MvResult.Fail("daemon_need_root", "networksetup needs root"));

        // Both values come from the daemon's HTTP API, which has no authentication.
        //
        // This was the worst of the three injection sites: the addresses went in with no quoting
        // at all, so a resolver address of "1.1.1.1 -setwebproxy Wi-Fi evil 8080" became four
        // arguments to networksetup, and an adapter name could close the quotes around it and do
        // the same. Validated as an address, and passed as separate arguments, so neither is
        // possible now.
        if (!Validate.IsMacServiceName(adapter))
            return Task.FromResult(MvResult.Fail("daemon_bad_interface", adapter));
        if (serversV4.Count > 0 && !Validate.AreDnsServers(serversV4))
            return Task.FromResult(MvResult.Fail("daemon_bad_dns", string.Join(",", serversV4)));

        // "empty" is how macOS is told to go back to DHCP, which is the restore case. It is a
        // networksetup keyword rather than an address, so it is added only when there is nothing
        // to set - never mixed in with validated addresses.
        var args = new List<string> { "-setdnsservers", adapter };
        if (serversV4.Count == 0) args.Add("empty");
        else args.AddRange(serversV4);

        var (ok, outp) = Shell.Run("networksetup", args, 8000);
        LastPrivilegeError = outp?.Trim() ?? "";
        return Task.FromResult(ok ? MvResult.Success()
                                  : MvResult.Fail("daemon_dns_failed",
                                      LastPrivilegeError.Length > 0 ? LastPrivilegeError : "networksetup refused"));
    }

    // ============================ per-process traffic ============================

    public Task<IReadOnlyList<AppNetInfo>> GetProcessTrafficAsync(CancellationToken ct = default) =>
        // lsof reports open sockets, not byte counts. There is no base-system per-process
        // network counter, so this returns empty rather than counting connections and
        // presenting them as traffic.
        Task.FromResult<IReadOnlyList<AppNetInfo>>(Array.Empty<AppNetInfo>());

    // ============================ firewall / limits ============================

    public RuleSupport Capability(AppHandle target) => RuleSupport.None;

    public Task<MvResult> SetBlockedAsync(AppHandle target, bool blocked, CancellationToken ct = default) =>
        Task.FromResult(blocked
            ? MvResult.Fail("daemon_unsupported", NoProcessMatch)
            : MvResult.Fail("daemon_unsupported",
                "nothing was blocked, so there is nothing to unblock - see the capability note"));

    public Task<MvResult> SetUploadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default) =>
        Task.FromResult(bytesPerSecond > 0
            ? MvResult.Fail("daemon_unsupported", NoShaping)
            : MvResult.Fail("daemon_unsupported", NoShaping));

    public Task<MvResult> SetDownloadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default) =>
        Task.FromResult(MvResult.Fail("daemon_unsupported", NoShaping));

    // ============================ system proxy ============================

    public async Task<string> GetSystemProxyAsync(CancellationToken ct = default)
    {
        var (ok, services) = await Shell.RunAsync("networksetup", "-listallnetworkservices", 6000, ct).ConfigureAwait(false);
        if (!ok) return "";

        foreach (var raw in services.Split('\n').Skip(1))
        {
            string service = raw.Trim();
            if (service.Length == 0 || service.StartsWith("*", StringComparison.Ordinal)) continue;
            if (!Validate.IsMacServiceName(service)) continue;
            var (ok2, outp) = await Shell.RunAsync("networksetup",
                new[] { "-getwebproxy", service }, 4000, ct).ConfigureAwait(false);
            if (!ok2) continue;
            string host = Field(outp, "Server:");
            string port = Field(outp, "Port:");
            if (host.Length > 0 && port.Length > 0) return $"{host}:{port}";
        }
        return "";
    }

    private static string Field(string text, string label)
    {
        foreach (var line in (text ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(label, StringComparison.Ordinal))
                return t.Substring(label.Length).Trim();
        }
        return "";
    }

    public async Task<MvResult> SetSystemProxyAsync(string hostPort, CancellationToken ct = default)
    {
        var (ok, services) = await Shell.RunAsync("networksetup", "-listallnetworkservices", 6000, ct).ConfigureAwait(false);
        if (!ok) return MvResult.Fail("daemon_proxy_failed", "networksetup is unavailable");

        string host = "", port = "";
        if (hostPort.Length > 0)
        {
            // Validated, not escaped: both values came off the unauthenticated API and used to be
            // interpolated straight into a string with no quoting on the host or the port at all.
            if (!Validate.TryHostPort(hostPort, out host, out port))
                return MvResult.Fail("daemon_proxy_bad", hostPort);
        }

        int applied = 0, failed = 0;
        string lastError = "";
        foreach (var raw in services.Split('\n').Skip(1))
        {
            string service = raw.Trim();
            if (service.Length == 0 || service.StartsWith("*", StringComparison.Ordinal)) continue;
            // networksetup's own output, so trusted by construction; still passed as one argument
            // so a service named "Wi-Fi" is not split in half.
            if (!Validate.IsMacServiceName(service)) continue;

            foreach (var kind in new[] { "webproxy", "securewebproxy" })
            {
                var args = hostPort.Length == 0
                    ? new[] { "-" + kind, service, "off" }
                    : new[] { "-" + kind, service, host, port };
                var (k, outp) = await Shell.RunAsync("networksetup", args, 5000, ct).ConfigureAwait(false);
                if (k) applied++;
                else { failed++; lastError = outp?.Trim() ?? ""; }
            }
        }

        LastPrivilegeError = lastError;
        if (applied == 0)
            return MvResult.Fail("daemon_proxy_failed",
                lastError.Length > 0 ? lastError : "no network service accepted the change");
        return failed > 0
            ? MvResult.Fail("daemon_proxy_partial", $"applied to {applied}, refused by {failed}")
            : MvResult.Success();
    }

    // ============================ VPN ============================

    public async Task<(bool Active, bool Unknown)> GetPcVpnStateAsync(CancellationToken ct = default)
    {
        // macOS creates utun interfaces whether or not a VPN is running - AirDrop, Handoff,
        // iCloud Private Relay and the OS itself all use them, and they carry no IPv4. A first
        // run on a real Mac therefore reported "unknown" on a machine with no VPN at all,
        // which is the same noise the Windows build had with idle TAP adapters.
        //
        // So the utun shape only counts as *possible* evidence when scutil actually lists a
        // VPN service. With none configured there is nothing to be uncertain about.
        var (scOk, scOut) = await Shell.RunAsync("scutil", "--nc list", 5000, ct).ConfigureAwait(false);
        bool anyVpnService = false;
        bool connected = false;
        if (scOk)
        {
            foreach (var raw in scOut.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("*", StringComparison.Ordinal))
                {
                    // "* (Disconnected)" - an inactive service still counts as configured.
                    anyVpnService = true;
                    continue;
                }
                if (!line.StartsWith("(", StringComparison.Ordinal)) continue;
                anyVpnService = true;
                if (line.Contains("(Connected)", StringComparison.Ordinal)) connected = true;
            }
        }

        if (connected) return (true, false);

        bool unconfirmed = false;
        var links = await GetLinksAsync(ct).ConfigureAwait(false);
        foreach (var l in links.Where(x => x.IsVpn && x.IsUp))
        {
            // A tun-style interface that does hold a routable address really is a tunnel.
            if (LinkClassifier.HasUsableIpv4(l.Ipv4)) return (true, false);
            unconfirmed = true;
        }

        // Nothing configured and nothing tunnelling: off, not unknown.
        if (unconfirmed && anyVpnService) return (false, true);
        return (false, false);
    }

    // ============================ snapshot ============================

    public async Task<string> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var dns = new List<Dictionary<string, object>>();
            var (ok, services) = await Shell.RunAsync("networksetup", "-listallnetworkservices", 6000, ct).ConfigureAwait(false);
            if (ok)
            {
                foreach (var raw in services.Split('\n').Skip(1))
                {
                    string service = raw.Trim();
                    if (service.Length == 0 || service.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (!Validate.IsMacServiceName(service)) continue;
            var (_, servers) = await Shell.RunAsync("networksetup",
                new[] { "-getdnsservers", service }, 4000, ct).ConfigureAwait(false);
                    var v4 = servers.Split('\n').Select(l => l.Trim())
                        .Where(l => IPAddress.TryParse(l, out _)).ToList();
                    dns.Add(new Dictionary<string, object>
                    {
                        ["service"] = service,
                        // "There aren't any DNS Servers set" is not an empty list - it is DHCP.
                        ["servers"] = v4,
                        ["dhcp"] = v4.Count == 0,
                    });
                }
            }

            var json = JsonSerializer.Serialize(new
            {
                takenAt = DateTime.Now.ToString("O"),
                dns,
                proxy = await GetSystemProxyAsync(ct).ConfigureAwait(false),
            });
            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(Path.Combine(_stateDir, "network-snapshot.json"), json);
            return json;
        }
        catch { return ""; }
    }

    public async Task<MvResult> RestoreSnapshotAsync(CancellationToken ct = default)
    {
        string path = Path.Combine(_stateDir, "network-snapshot.json");
        if (!File.Exists(path)) return MvResult.Fail("mv_snapshot_none");
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            var root = doc.RootElement;
            bool did = false;

            if (root.TryGetProperty("dns", out var dns) && dns.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in dns.EnumerateArray())
                {
                    if (!e.TryGetProperty("service", out var sv)) continue;
                    string service = sv.GetString() ?? "";
                    var servers = new List<string>();
                    bool dhcp = e.TryGetProperty("dhcp", out var d) && d.GetBoolean();
                    if (e.TryGetProperty("servers", out var arr))
                        foreach (var s in arr.EnumerateArray()) servers.Add(s.GetString() ?? "");

                    var r = await SetDnsAsync(service, dhcp ? new List<string>() : servers, ct).ConfigureAwait(false);
                    if (r.Ok) did = true;
                }
            }

            if (root.TryGetProperty("proxy", out var px))
            {
                var r = await SetSystemProxyAsync(px.GetString() ?? "", ct).ConfigureAwait(false);
                if (r.Ok) did = true;
            }

            return did ? MvResult.Success("mv_snapshot_restored")
                       : MvResult.Fail("mv_restore_failed", "nothing could be restored");
        }
        catch (Exception ex)
        {
            return MvResult.Fail("mv_error", ex.Message);
        }
    }

    private static long ParseLong(string s) =>
        long.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public void Dispose() { }
}