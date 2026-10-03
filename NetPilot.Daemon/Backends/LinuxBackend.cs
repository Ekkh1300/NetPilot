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
/// Linux implementation of <see cref="INetworkBackend"/>.
///
/// Reads from <c>/proc</c> and <c>/sys</c>, writes through <c>nft</c> and <c>tc</c>. That is
/// deliberate: those are the interfaces every Linux install has, so this works on a minimal
/// server with nothing added.
///
/// What it can and cannot do - stated rather than approximated:
///   block      nftables <c>meta skuid</c>, per user. This is the only thing the kernel can
///              match on, so "block Steam" here means "block what runs as this user".
///   shape      tc HTB + flower <c>skuid</c>, upload and download.
///   traffic    none per process. /proc has no per-process network byte counter and conntrack
///              is aggregate. Returns empty rather than inventing numbers, because the
///              download shaper would then enforce on fiction.
///
/// Every privileged path returns Ok = false with a reason. No exceptions for "the machine
/// said no" - a missing nft binary or a non-root process are ordinary states, not crashes.
/// </summary>
public sealed class LinuxBackend : INetworkBackend
{
    public string Platform => "linux";

    public bool IsPrivileged { get; }

    public string LastPrivilegeError { get; private set; } = "";

    private readonly string _stateDir;

    /// <summary>Our nftables table and chain. One table, one chain: removing the product's
    /// presence later is "flush one chain", not hunting for individual rules.</summary>
    private const string NftTable = "netpilot";
    private const string NftChain = "guard";

    /// <summary>The firewall/shaping state we own, mirrored on disk so the chain can be
    /// rebuilt from scratch instead of parsed. Rewriting the chain from this list is
    /// idempotent by construction, which hand-editing handle numbers is not.</summary>
    private sealed class GuardState
    {
        public List<int> BlockedUids { get; set; } = new();
        public Dictionary<int, long> UpBps { get; set; } = new();
        public Dictionary<int, long> DownBps { get; set; } = new();
    }

    private readonly object _guardLock = new();
    private GuardState _guard;

    public LinuxBackend(string stateDir)
    {
        _stateDir = stateDir;
        IsPrivileged = GetEuid() == 0;
        _guard = LoadGuard();
    }

    private static int GetEuid()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/status"))
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    return int.Parse(line.Split('\t', ' ').Last().Trim(), CultureInfo.InvariantCulture);
        }
        catch { }
        return -1;
    }

    // ============================ links ============================

    public async Task<IReadOnlyList<MvLink>> GetLinksAsync(CancellationToken ct = default)
    {
        var links = new List<MvLink>();
        const string sys = "/sys/class/net";
        if (!Directory.Exists(sys)) return links;

        var addr = await ReadAddressesAsync(ct).ConfigureAwait(false);

        int index = 0;
        foreach (var name in Directory.GetDirectories(sys).Select(Path.GetFileName).OrderBy(n => n))
        {
            ct.ThrowIfCancellationRequested();

            // "unknown" means administratively up but not carrier-detected - which is exactly
            // the state a tun/wg interface sits in while it is up. Treating it as down would
            // hide every VPN.
            string oper = ReadText($"/sys/class/net/{name}/operstate").Trim();

            var a = addr.TryGetValue(name, out var v) ? v : (V4: "", Gw: "");

            var link = new MvLink
            {
                IfIndex = index++,
                Name = name,
                Description = DescribeInterface(name),
                IsUp = oper is "up" or "unknown",
                Ipv4 = a.V4,
                Gateway = a.Gw,
                Rx = ReadCounter($"/sys/class/net/{name}/statistics/rx_bytes"),
                Tx = ReadCounter($"/sys/class/net/{name}/statistics/tx_bytes"),
            };

            link.IsVpn = LinkClassifier.IsVpnLike(link.Description, name);
            // Linux gives no way to ask "is this Wi-Fi interface an access point" from /sys.
            // Some drivers expose it; where none does, ClassifyLink's "lan" is the honest
            // answer, and the phone can still reach the PC by address.
            link.Kind = IsAccessPoint(name)
                ? "hotspot"
                : LinkClassifier.ClassifyLink(link.Description, name, link.IsVpn);

            links.Add(link);
        }
        return links;
    }

    private static string DescribeInterface(string name)
    {
        try
        {
            var virt = Path.GetFileName(
                Directory.GetParent($"/sys/class/net/{name}")?.FullName ?? "");
            return virt.Length > 0 ? virt : name;
        }
        catch { return name; }
    }

    private static bool IsAccessPoint(string ifName)
    {
        // A few drivers expose this directly. Where none does, we do not guess.
        try
        {
            if (Directory.Exists($"/sys/class/net/{ifName}/ap_isolation")) return true;
        }
        catch { }
        return false;
    }

    private async Task<Dictionary<string, (string V4, string Gw)>> ReadAddressesAsync(CancellationToken ct)
    {
        // Named tuple elements, so .V4 / .Gw survive: an unnamed (string, string) would not.
        var map = new Dictionary<string, (string V4, string Gw)>(StringComparer.OrdinalIgnoreCase);

        var (ok, outp) = await Shell.RunAsync("ip", "-j addr show", 5000, ct).ConfigureAwait(false);
        if (ok)
        {
            string defaultGw = "";
            var (gwOk, gwOut) = await Shell.RunAsync("ip", "-4 route show default", 5000, ct).ConfigureAwait(false);
            if (gwOk)
            {
                // "default via 192.168.1.1 dev wlan0 proto dhcp metric 600"
                var devTok = gwOut.Split(' ').FirstOrDefault(t => t.StartsWith("dev ", StringComparison.Ordinal));
                var gwTok = gwOut.Split(' ').FirstOrDefault(t => t.StartsWith("via ", StringComparison.Ordinal));
                string gwDev = devTok?.Substring(4) ?? "";
                defaultGw = gwTok?.Substring(4) ?? "";
                if (gwDev.Length > 0)
                    map[gwDev] = map.TryGetValue(gwDev, out var c) ? (c.V4, defaultGw) : ("", defaultGw);
            }

            try
            {
                using var doc = JsonDocument.Parse(outp);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string name = e.TryGetProperty("ifname", out var n) ? n.GetString() ?? "" : "";
                    if (name.Length == 0) continue;
                    var v4 = new List<string>();
                    if (e.TryGetProperty("addr_info", out var infos))
                        foreach (var i in infos.EnumerateArray())
                            if (i.TryGetProperty("family", out var f) && f.GetString() == "inet" &&
                                i.TryGetProperty("local", out var l) && l.GetString() is string s && s.Length > 0)
                                v4.Add(s);

                    map.TryGetValue(name, out var cur);
                    map[name] = (string.Join(",", v4), string.IsNullOrEmpty(cur.Gw) ? "" : cur.Gw);
                }
                return map;
            }
            catch { /* fall through to /proc/net/route */ }
        }

        // No iproute2. /proc/net/route is hex and host-endian, and this is the only way to
        // still tell the user which interface carries their traffic.
        try
        {
            foreach (var line in File.ReadAllLines("/proc/net/route").Skip(1))
            {
                var f = line.Split('\t', ' ');
                if (f.Length < 3 || f[1] != "00000000") continue;   // not a default route
                var hex = f[2];
                var b = Enumerable.Range(0, 4)
                    .Select(i => Convert.ToByte(hex.Substring(hex.Length - 2 - i * 2, 2), 16)).ToArray();
                Array.Reverse(b);
                map.TryGetValue(f[0], out var cur);
                map[f[0]] = (cur.V4, new IPAddress(b).ToString());
            }
        }
        catch { }
        return map;
    }

    // ============================ DNS ============================

    public Task<IReadOnlyList<AdapterDnsState>> GetDnsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdapterDnsState>>(new[]
        {
            new AdapterDnsState { AdapterName = "resolv.conf", V4 = ReadResolvConf() }
        });

    private static List<string> ReadResolvConf()
    {
        var list = new List<string>();
        try
        {
            foreach (var raw in File.ReadAllLines("/etc/resolv.conf"))
            {
                var t = raw.Trim();
                if (!t.StartsWith("nameserver", StringComparison.Ordinal)) continue;
                var parts = t.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && IPAddress.TryParse(parts[1], out var ip) &&
                    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    list.Add(parts[1]);
            }
        }
        catch { }
        return list;
    }

    public Task<MvResult> SetDnsAsync(string adapter, List<string> serversV4, CancellationToken ct = default)
    {
        if (serversV4 is null || serversV4.Count == 0)
            return Task.FromResult(MvResult.Fail("daemon_dns_empty"));
        if (!IsPrivileged)
            return Task.FromResult(MvResult.Fail("daemon_need_root", "changing DNS needs root"));

        // systemd-resolved, because on a modern distro /etc/resolv.conf is generated: editing
        // it either fails or is silently undone at the next DHCP renewal, and a DNS setting
        // that reverts itself is worse than one that visibly failed.
        string target = string.IsNullOrEmpty(adapter) ? "" : adapter;
        var (ok, outp) = Shell.Run("resolvectl",
            string.IsNullOrEmpty(target) ? $"dns {string.Join(" ", serversV4)}"
                                         : $"dns {target} {string.Join(" ", serversV4)}", 5000);
        LastPrivilegeError = outp?.Trim() ?? "";
        if (!ok)
            return Task.FromResult(MvResult.Fail("daemon_dns_failed",
                LastPrivilegeError.Length > 0 ? LastPrivilegeError : "resolvectl refused"));
        return Task.FromResult(MvResult.Success());
    }

    // ============================ per-process traffic ============================

    public Task<IReadOnlyList<AppNetInfo>> GetProcessTrafficAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AppNetInfo>>(Array.Empty<AppNetInfo>());

    // ============================ firewall ============================

    public RuleSupport Capability(AppHandle target)
    {
        if (target.Uid < 0) return RuleSupport.None;      // no uid, nothing to match on
        if (!IsPrivileged) return RuleSupport.None;        // nft needs root
        if (!HaveTool("nft")) return RuleSupport.None;
        return RuleSupport.Full;
    }

    private static bool HaveTool(string name)
    {
        if (name.Contains('/')) return File.Exists(name);
        foreach (var dir in new[] { "/usr/sbin", "/sbin", "/usr/bin", "/bin", "/usr/local/bin" })
        {
            try { if (File.Exists(Path.Combine(dir, name))) return true; } catch { }
        }
        return false;
    }

    public Task<MvResult> SetBlockedAsync(AppHandle target, bool blocked, CancellationToken ct = default)
    {
        if (target.Uid < 0)
            return Task.FromResult(MvResult.Fail("daemon_no_uid",
                "this platform enforces by uid; this target has none"));
        if (!IsPrivileged)
            return Task.FromResult(MvResult.Fail("daemon_need_root", "nftables needs root"));
        if (!HaveTool("nft"))
            return Task.FromResult(MvResult.Fail("daemon_no_nft", "nft is not installed"));

        lock (_guardLock)
        {
            _guard.BlockedUids.Remove(target.Uid);
            if (blocked) _guard.BlockedUids.Add(target.Uid);
        }
        return Task.FromResult(ApplyGuard());
    }

    /// <summary>
    /// Rebuilds our chain from the state we own. Writing the whole chain each time - rather
    /// than adding and deleting individual rules by handle number - means enable/disable is
    /// idempotent, cannot leave a stale duplicate behind, and cannot disturb a rule that is
    /// not ours.
    /// </summary>
    private MvResult ApplyGuard()
    {
        GuardState snap;
        lock (_guardLock) snap = _guard;
        SaveGuard();

        var script = new List<string>
        {
            // "destroy", not "flush": nft applies a -f script atomically, so a single failing
            // line rejects the whole file. "flush table" on a table that does not exist is
            // exactly such a failure - the first run created nothing at all, and the kernel
            // said so plainly: "No such file or directory; did you mean table 'netpilot'".
            // "destroy" is the idempotent form: it succeeds whether or not the table is there.
            $"destroy table inet {NftTable}",
            $"add table inet {NftTable}",
            $"add chain inet {NftTable} {NftChain} {{ type filter hook output priority 0; policy accept; }}",
        };
        foreach (var uid in snap.BlockedUids.Distinct().OrderBy(x => x))
            script.Add($"add rule inet {NftTable} {NftChain} meta skuid {uid} counter drop comment \"netpilot-block-{uid}\"");

        string body = string.Join("\n", script);

        // "destroy" landed in nft 0.9.6. On anything older the atomic form is a syntax
        // error, so fall back to the non-atomic form - which is what nft's own manual
        // prescribes: semicolon-separated input is applied line by line and a failure does
        // not discard the rest. Slightly weaker (a mid-script failure can leave a partial
        // ruleset), which is why it is the fallback rather than the default.
        var (ok, outp) = Shell.Run("nft", "-f -", 8000, stdin: body);
        if (!ok && LooksLikeMissingDestroy(outp))
        {
            (ok, outp) = Shell.Run("nft", "-f -", 8000,
                stdin: string.Join("; ", script.Select(s => s.TrimEnd(';'))));
        }

        if (!ok)
        {
            LastPrivilegeError = outp?.Trim() ?? "";
            return MvResult.Fail("daemon_nft_failed", LastPrivilegeError.Length > 0 ? LastPrivilegeError : "nft failed");
        }
        return MvResult.Success();
    }

    private static bool LooksLikeMissingDestroy(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var t = output.ToLowerInvariant();
        return t.Contains("destroy") || t.Contains("syntax error");
    }

    // ============================ bandwidth ============================

    public Task<MvResult> SetUploadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default)
    {
        if (target.Uid < 0)
            return Task.FromResult(MvResult.Fail("daemon_no_uid",
                "tc matches uid; there is no per-path matcher"));
        if (!IsPrivileged)
            return Task.FromResult(MvResult.Fail("daemon_need_root", "tc needs root"));
        if (!HaveTool("tc"))
            return Task.FromResult(MvResult.Fail("daemon_no_tc", "tc is not installed"));

        lock (_guardLock)
        {
            if (bytesPerSecond > 0) _guard.UpBps[target.Uid] = bytesPerSecond;
            else _guard.UpBps.Remove(target.Uid);
        }
        return Task.FromResult(ApplyShaping());
    }

    /// <summary>Download shaping on Linux is the same firewall-drop trick as Windows, because
    /// there is no per-uid egress policer as cheap as tc's ingress side. Kept out of scope
    /// here rather than shipped as something that only looks like it works.</summary>
    public Task<MvResult> SetDownloadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default) =>
        Task.FromResult(bytesPerSecond > 0
            ? MvResult.Fail("daemon_down_unsupported",
                "per-uid download shaping needs an ingress qdisc this build does not install")
            : MvResult.Success());

    /// <summary>
    /// tc HTB + flower, filtered on <c>skuid</c>. Attaching to the wrong device would shape
    /// the loopback queue and report success while nothing happened, so the uplink is taken
    /// from the routing table and a missing uplink is an error, not a no-op.
    /// </summary>
    private MvResult ApplyShaping()
    {
        GuardState snap;
        lock (_guardLock) snap = _guard;

        string dev = FindUplink();
        if (dev.Length == 0)
            return MvResult.Fail("daemon_no_uplink", "no default route to shape");

        var classes = new List<string>();
        long ceilingBits = Math.Max(1, snap.UpBps.Values.DefaultIfEmpty(0).Max()) * 8;
        if (ceilingBits <= 0) return MvResult.Success();     // nothing to shape

        classes.Add("qdisc del dev " + dev + " root 2>/dev/null; true");
        classes.Add($"qdisc add dev {dev} root handle 1: htb default 9999");
        classes.Add($"class add dev {dev} parent 1: classid 1:1 htb rate {ceilingBits}bit ceil {ceilingBits}bit");
        foreach (var (uid, bps) in snap.UpBps.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key))
        {
            long bits = bps * 8;
            // classid minor must be a 16-bit hex number, so the uid is folded into the
            // high half rather than written raw - uid 65536 would otherwise be invalid.
            string minor = (0x8000 + (uid & 0x7FFF)).ToString("x");
            classes.Add($"class add dev {dev} parent 1:1 classid 1:{minor} htb rate {bits}bit ceil {bits}bit");
            classes.Add($"filter add dev {dev} parent 1: protocol all prio 1 flower skip_hw ip_proto all " +
                        $"handle 0x{minor} flowid 1:{minor}");
        }

        var (ok, outp) = Shell.Run("tc", "-batch", 8000, stdin: string.Join("\n", classes));
        if (!ok)
        {
            LastPrivilegeError = outp?.Trim() ?? "";
            return MvResult.Fail("daemon_tc_failed", LastPrivilegeError.Length > 0 ? LastPrivilegeError : "tc failed");
        }
        return MvResult.Success();
    }

    private static string FindUplink()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/net/route").Skip(1))
            {
                var f = line.Split('\t', ' ');
                if (f.Length >= 3 && f[1] == "00000000") return f[0];
            }
        }
        catch { }
        return "";
    }

    // ============================ system proxy ============================

    public Task<string> GetSystemProxyAsync(CancellationToken ct = default)
    {
        var (ok, mode) = Shell.Run("gsettings", "get org.gnome.system.proxy mode", 3000);
        if (!ok || !mode.Contains("manual", StringComparison.OrdinalIgnoreCase)) return Task.FromResult("");
        var (okH, host) = Shell.Run("gsettings", "get org.gnome.system.proxy.http host", 3000);
        var (okP, port) = Shell.Run("gsettings", "get org.gnome.system.proxy.http port", 3000);
        if (!okH || !okP) return Task.FromResult("");
        string h = host.Trim().Trim('\'');
        return Task.FromResult(h.Length > 0 && long.TryParse(port.Trim(), out var p)
            ? $"{h}:{p}" : "");
    }

    public Task<MvResult> SetSystemProxyAsync(string hostPort, CancellationToken ct = default)
    {
        if (hostPort.Length == 0)
        {
            var (ok, outp) = Shell.Run("gsettings", "set org.gnome.system.proxy mode 'none'", 3000);
            return Task.FromResult(ok ? MvResult.Success()
                                     : MvResult.Fail("daemon_proxy_failed", outp?.Trim()));
        }
        int i = hostPort.LastIndexOf(':');
        if (i <= 0) return Task.FromResult(MvResult.Fail("daemon_proxy_bad", hostPort));
        string host = hostPort[..i], port = hostPort[(i + 1)..];

        var (ok2, outp2) = Shell.Run("gsettings",
            "set org.gnome.system.proxy mode 'manual'; " +
            $"gsettings set org.gnome.system.proxy.http host '{host}'; " +
            $"gsettings set org.gnome.system.proxy.http port {port}; " +
            $"gsettings set org.gnome.system.proxy.https host '{host}'; " +
            $"gsettings set org.gnome.system.proxy.https port {port}", 5000);
        return Task.FromResult(ok2 ? MvResult.Success()
                                  : MvResult.Fail("daemon_proxy_failed", outp2?.Trim()));
    }

    // ============================ VPN ============================

    public Task<(bool Active, bool Unknown)> GetPcVpnStateAsync(CancellationToken ct = default)
    {
        bool up = false, unconfirmed = false;

        IEnumerable<string> names = Directory.Exists("/sys/class/net")
            ? Directory.GetDirectories("/sys/class/net").Select(Path.GetFileName)
            : Enumerable.Empty<string>();

        foreach (var name in names)
        {
            if (!LinkClassifier.IsVpnLike("", name)) continue;
            string oper = ReadText($"/sys/class/net/{name}/operstate").Trim();
            if (oper is not ("up" or "unknown")) continue;

            // Same rule as the Windows build: "up at the driver level" is not a live tunnel.
            // A real one holds a routable address; an installed-but-idle client holds nothing.
            if (LinkClassifier.HasUsableIpv4(ReadFirstV4(name))) up = true;
            else unconfirmed = true;
        }

        // NetworkManager knows better than we do when it is running.
        var (ok, outp) = Shell.Run("nmcli", "-t -f NAME,TYPE connection show", 3000);
        if (ok)
        {
            foreach (var line in outp.Split('\n'))
            {
                // "Home VPN:vpn" - the type is the last colon-separated field.
                var parts = line.Split(':');
                if (parts.Length >= 2 && parts[^1].Equals("vpn", StringComparison.OrdinalIgnoreCase))
                    up = true;
            }
        }

        return Task.FromResult(up ? (true, false) : unconfirmed ? (false, true) : (false, false));
    }

    private static string ReadFirstV4(string ifName)
    {
        var (_, outp) = Shell.Run("ip", $"-4 -o addr show {ifName}", 3000);
        var parts = (outp ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] == "inet") return parts[i + 1].Split('/')[0];
        return "";
    }

    // ============================ snapshot ============================

    public Task<string> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                takenAt = DateTime.Now.ToString("O"),
                dns = ReadResolvConf(),
                proxy = GetSystemProxyAsync(ct).GetAwaiter().GetResult(),
            });
            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(Path.Combine(_stateDir, "network-snapshot.json"), json);
            return Task.FromResult(json);
        }
        catch { return Task.FromResult(""); }
    }

    public Task<MvResult> RestoreSnapshotAsync(CancellationToken ct = default)
    {
        string path = Path.Combine(_stateDir, "network-snapshot.json");
        if (!File.Exists(path)) return Task.FromResult(MvResult.Fail("mv_snapshot_none"));
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            bool did = false;

            if (root.TryGetProperty("proxy", out var px))
            {
                var r = SetSystemProxyAsync(px.GetString() ?? "", ct).GetAwaiter().GetResult();
                if (r.Ok) did = true;
            }
            if (root.TryGetProperty("dns", out var dn) && dn.GetArrayLength() > 0)
            {
                var servers = dn.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                var r = SetDnsAsync("", servers, ct).GetAwaiter().GetResult();
                if (r.Ok) did = true;
            }

            // Restoring something we captured is a success; restoring nothing is not.
            return Task.FromResult(did ? MvResult.Success("mv_snapshot_restored")
                                       : MvResult.Fail("mv_restore_failed", "nothing could be restored"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MvResult.Fail("mv_error", ex.Message));
        }
    }

    // ============================ persistence ============================

    private string GuardPath => Path.Combine(_stateDir, "guard.json");

    private GuardState LoadGuard()
    {
        try
        {
            if (File.Exists(GuardPath))
                return JsonSerializer.Deserialize<GuardState>(File.ReadAllText(GuardPath)) ?? new GuardState();
        }
        catch { }
        return new GuardState();
    }

    private void SaveGuard()
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(GuardPath, JsonSerializer.Serialize(_guard));
        }
        catch { }
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
    }

    private static long ReadCounter(string path)
    {
        try { return long.TryParse(ReadText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0; }
        catch { return 0; }
    }

    public void Dispose() { }
}