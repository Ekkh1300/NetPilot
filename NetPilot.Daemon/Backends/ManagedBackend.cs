using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;
using NetPilot.Services;

namespace NetPilot.Daemon.Backends;

/// <summary>
/// Link detection built on <see cref="NetworkInterface"/>, which .NET implements on Windows,
/// Linux and macOS alike.
///
/// It exists for two reasons.
///
/// First, it is the portable base: the Linux backend needs /sys and /proc for the privileged
/// paths, but its link list could have come from here, and on a platform where <c>ip</c> is
/// missing this is what still shows the machine's network.
///
/// Second, and more useful: because it needs no shell and no root, it runs on Windows. That
/// makes the desktop UI demonstrable on a machine that has no Linux or macOS at all - the
/// point being that an Avalonia interface is platform-neutral, so seeing it render and behave
/// correctly here is real evidence about the Linux and macOS builds, not a guess. Only the
/// native windowing underneath is Avalonia's own code.
///
/// It refuses every privileged operation by name rather than half-doing it. Nothing here can
/// block, shape or rewrite a proxy, and a backend that quietly did nothing while claiming
/// success is the one failure mode this whole design exists to prevent.
/// </summary>
public sealed class ManagedBackend : INetworkBackend
{
    public string Platform => "managed";

    public bool IsPrivileged { get; }

    public string LastPrivilegeError { get; private set; } = "";

    private readonly string _stateDir;

    private const string NotHere =
        "this operation needs the platform backend (nft/tc on Linux, networksetup on macOS) " +
        "or the Windows desktop app";

    public ManagedBackend(string stateDir)
    {
        _stateDir = stateDir;
        IsPrivileged = false;
    }

    // ============================ links ============================

    public Task<IReadOnlyList<MvLink>> GetLinksAsync(CancellationToken ct = default)
    {
        var links = new List<MvLink>();
        int index = 0;

        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return Task.FromResult<IReadOnlyList<MvLink>>(links); }

        foreach (var ni in all)
        {
            ct.ThrowIfCancellationRequested();

            string name = Safe(() => ni.Name);
            string desc = Safe(() => ni.Description);

            var link = new MvLink
            {
                IfIndex = index++,
                Name = name,
                Description = desc.Length > 0 ? desc : name,
                IsUp = SafeBool(() => ni.OperationalStatus == OperationalStatus.Up),
            };

            // .NET reports either a single address or None for a 0.0.0.0/unconfigured
            // interface; the guard below filters those, so an unassigned NIC is not offered
            // as a path to the phone.
            try
            {
                var ip = ni.GetIPProperties();
                var v4 = ip?.UnicastAddresses?
                    .Select(a => a.Address.ToString())
                    .Where(LinkClassifier.HasUsableIpv4) ?? Enumerable.Empty<string>();
                link.Ipv4 = string.Join(",", v4);
                var gw = ip?.GatewayAddresses?
                    .Select(g => g.Address.ToString()) ?? Enumerable.Empty<string>();
                link.Gateway = string.Join(",", gw);
            }
            catch { }

            // These throw on an interface that has gone away mid-enumeration, which is common
            // with virtual adapters - the driver reports them and then they are removed.
            try { link.Rx = ni.GetIPv4Statistics()?.BytesReceived ?? 0; } catch { }
            try { link.Tx = ni.GetIPv4Statistics()?.BytesSent ?? 0; } catch { }
            try { link.Metric = ni.GetIPProperties()?.GetIPv4Properties()?.Index ?? 0; } catch { }

            link.IsVpn = LinkClassifier.IsVpnLike(link.Description, link.Name);
            link.Kind = LinkClassifier.ClassifyLink(link.Description, link.Name, link.IsVpn);

            links.Add(link);
        }

        // The same three-state rule the other backends use, so the UI behaves identically:
        // an adapter that is up but holds no routable address is a client that is installed
        // and not tunnelling, which is neither confidently active nor confidently off.
        bool up = links.Any(l => l.IsVpn && l.IsUp && LinkClassifier.HasUsableIpv4(l.Ipv4));
        bool unconfirmed = links.Any(l => l.IsVpn && l.IsUp && !LinkClassifier.HasUsableIpv4(l.Ipv4));
        _vpn = up ? (true, false) : unconfirmed ? (false, true) : (false, false);

        return Task.FromResult<IReadOnlyList<MvLink>>(links);
    }

    private (bool Active, bool Unknown) _vpn = (false, false);

    private static string Safe(Func<string> get)
    {
        try { return get() ?? ""; } catch { return ""; }
    }

    /// <summary>Some drivers throw from OperationalStatus while an adapter is being removed,
    /// which is common with virtual NICs. Treated as down, since a NIC we cannot query is not
    /// one we should route a phone through.</summary>
    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); } catch { return false; }
    }

    // ============================ DNS ============================

    public Task<IReadOnlyList<AdapterDnsState>> GetDnsAsync(CancellationToken ct = default)
    {
        var states = new List<AdapterDnsState>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ip = ni.GetIPProperties();
                var v4 = ip?.UnicastAddresses?
                    .Select(a => a.Address.ToString()) ?? Enumerable.Empty<string>();
                // DNS is a property of the interface, and this is the portable part of it.
                // An interface that lists none is on DHCP, which is what IsDynamic means.
                var dns = new List<string>();
                if (ip?.DnsAddresses != null)
                {
                    foreach (var addr in ip.DnsAddresses)
                    {
                        string s = addr.ToString();
                        if (LinkClassifier.HasUsableIpv4(s)) dns.Add(s);
                    }
                }
                states.Add(new AdapterDnsState
                {
                    AdapterName = ni.Name,
                    IsDynamic = dns.Count == 0,
                    V4 = dns,
                });
            }
        }
        catch { }
        return Task.FromResult<IReadOnlyList<AdapterDnsState>>(states);
    }

    public Task<MvResult> SetDnsAsync(string adapter, List<string> serversV4, CancellationToken ct = default) =>
        Task.FromResult(MvResult.Fail("daemon_need_platform", NotHere));

    // ============================ traffic ============================

    public Task<IReadOnlyList<AppNetInfo>> GetProcessTrafficAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AppNetInfo>>(Array.Empty<AppNetInfo>());

    // ============================ privileged ============================

    public RuleSupport Capability(AppHandle target) => RuleSupport.None;

    public Task<MvResult> SetBlockedAsync(AppHandle target, bool blocked, CancellationToken ct = default) =>
        Task.FromResult(MvResult.Fail("daemon_need_platform", NotHere));

    public Task<MvResult> SetUploadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default) =>
        Task.FromResult(MvResult.Fail("daemon_need_platform", NotHere));

    public Task<MvResult> SetDownloadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default) =>
        Task.FromResult(MvResult.Fail("daemon_need_platform", NotHere));

    public Task<MvResult> SetSystemProxyAsync(string hostPort, CancellationToken ct = default) =>
        Task.FromResult(MvResult.Fail("daemon_need_platform", NotHere));

    public Task<string> GetSystemProxyAsync(CancellationToken ct = default) => Task.FromResult("");

    // ============================ VPN ============================

    public Task<(bool Active, bool Unknown)> GetPcVpnStateAsync(CancellationToken ct = default) =>
        Task.FromResult(_vpn);

    // ============================ snapshot ============================

    /// <summary>Nothing here changes the machine, so there is nothing to put back - and saying
    /// so is better than writing an empty snapshot that Restore would then report as success.</summary>
    public Task<string> CaptureSnapshotAsync(CancellationToken ct = default) => Task.FromResult("");

    public Task<MvResult> RestoreSnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult(MvResult.Fail("mv_snapshot_none"));

    public void Dispose() { }
}