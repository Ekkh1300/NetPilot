using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>
/// One thing NetPilot can point at: an application, a user, or a whole system.
///
/// Windows identifies an app by its exe path. Linux can do that too, but nftables' native
/// matcher works on uid, which is both cheaper and the only thing that survives the process
/// restarting. macOS pf has neither, so it matches on the process path. Rather than pretend
/// they are the same, a rule carries all three and each backend uses what it can honour -
/// and says so, instead of silently pretending it applied something.
/// </summary>
public sealed class AppHandle
{
    /// <summary>Executable path, when known.</summary>
    public string Path = "";
    /// <summary>Unix uid, when known (Linux).</summary>
    public int Uid = -1;
    /// <summary>macOS signing identifier or bundle id, when known.</summary>
    public string BundleId = "";
    public string DisplayName = "";

    /// <summary>How well the backend can actually enforce a rule on this target.</summary>
    public RuleSupport Support = RuleSupport.None;

    public string Key => Path.Length > 0 ? Path : Uid >= 0 ? "uid:" + Uid : BundleId;
}

public enum RuleSupport
{
    /// <summary>This platform cannot enforce anything on this target.</summary>
    None = 0,
    /// <summary>Blocking works, shaping does not.</summary>
    Block = 1,
    /// <summary>Both blocking and rate shaping work.</summary>
    Full = 2,
}

/// <summary>
/// Everything the product does to a machine, expressed without reference to any operating
/// system. The Windows app, the macOS/Linux daemon and the tests all talk to this.
///
/// Two rules for anyone adding a member:
///   1. never throw for "the machine said no" - return a result with Ok = false and a reason;
///      a missing <c>nft</c> or a denied <c>pf</c> rule is an expected state, not a crash;
///   2. never silently do nothing - if a capability is missing, say so in the result, because
///      a limiter that looks enabled and enforces nothing is the worst possible outcome.
/// </summary>
public interface INetworkBackend : IDisposable
{
    /// <summary>"windows", "linux", "macos" - reported to the phone and shown in the UI.</summary>
    string Platform { get; }

    /// <summary>True when the process can change the firewall (Administrator / root).</summary>
    bool IsPrivileged { get; }

    /// <summary>Why the last privileged operation was refused, or "" if it was not.</summary>
    string LastPrivilegeError { get; }

    // ---------------- adapters / links ----------------

    /// <summary>Every interface, with counters, address, gateway and classification.</summary>
    Task<IReadOnlyList<MvLink>> GetLinksAsync(CancellationToken ct = default);

    /// <summary>DNS servers currently configured per interface.</summary>
    Task<IReadOnlyList<AdapterDnsState>> GetDnsAsync(CancellationToken ct = default);

    /// <summary>Applies a DNS configuration. Partial results are reported per interface.</summary>
    Task<MvResult> SetDnsAsync(string adapter, List<string> serversV4, CancellationToken ct = default);

    // ---------------- per-process traffic ----------------

    /// <summary>Live per-process throughput, best effort. Empty when the OS offers no cheap way.</summary>
    Task<IReadOnlyList<AppNetInfo>> GetProcessTrafficAsync(CancellationToken ct = default);

    // ---------------- firewall ----------------

    /// <summary>True when this backend can enforce per-target rules at all.</summary>
    RuleSupport Capability(AppHandle target);

    /// <summary>Blocks or unblocks a target. <paramref name="blocked"/> true = drop all traffic.</summary>
    Task<MvResult> SetBlockedAsync(AppHandle target, bool blocked, CancellationToken ct = default);

    // ---------------- bandwidth ----------------

    /// <summary>Upload limit in bytes/second (0 = unlimited). Returns what was actually applied.</summary>
    Task<MvResult> SetUploadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default);

    /// <summary>Download limit in bytes/second (0 = unlimited).</summary>
    Task<MvResult> SetDownloadLimitAsync(AppHandle target, long bytesPerSecond, CancellationToken ct = default);

    // ---------------- system proxy (used by the phone tunnel) ----------------

    /// <summary>Current system proxy as host:port, or "" when direct.</summary>
    Task<string> GetSystemProxyAsync(CancellationToken ct = default);

    /// <summary>Points the whole machine's traffic at a proxy. "" restores direct.</summary>
    Task<MvResult> SetSystemProxyAsync(string hostPort, CancellationToken ct = default);

    // ---------------- VPN ----------------

    /// <summary>
    /// Three states, not two. An interface that is up with no usable address is a VPN client
    /// that is installed and not tunnelling - reporting that as "active" blocks the phone
    /// tunnel for no reason, which is the false positive this exists to avoid.
    /// </summary>
    Task<(bool Active, bool Unknown)> GetPcVpnStateAsync(CancellationToken ct = default);

    // ---------------- snapshot ----------------

    /// <summary>
    /// Captures the state this backend is allowed to change. Returns "" when the platform has
    /// no restorable notion of it, which the caller must report rather than fake.
    /// </summary>
    Task<string> CaptureSnapshotAsync(CancellationToken ct = default);

    /// <summary>Puts back what CaptureSnapshotAsync took. False when there was nothing to restore.</summary>
    Task<MvResult> RestoreSnapshotAsync(CancellationToken ct = default);
}

/// <summary>One OS's implementation of the above.</summary>
public interface INetworkBackendFactory
{
    /// <summary>Returns the backend for the OS we are actually running on, or null.</summary>
    INetworkBackend TryCreate();
}