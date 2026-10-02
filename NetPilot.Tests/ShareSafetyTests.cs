using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The two rules that keep this feature from breaking the user's machine:
///   1. every mutation is preceded by a snapshot, and the restore reports what it really did;
///   2. a share that cannot work is refused *before* any system setting is touched.
///
/// The restore used to print a bare 'OK' no matter how many adapters it had failed on, so
/// the UI said "network state restored" while the machine stayed on our metrics - and the
/// next share then captured those wrong numbers as the new "original".
/// </summary>
public class ShareSafetyTests
{
    private const string InternetSettings =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>
    /// Restores the snapshot file afterwards. Several tests below write a synthetic one;
    /// the real file belongs to the running app and must survive the suite untouched.
    /// </summary>
    private sealed class SnapshotScope : IDisposable
    {
        private readonly string _path = MobileVpnService.SnapshotPath;
        private readonly bool _existed;
        private readonly byte[] _bytes;

        public SnapshotScope()
        {
            _existed = File.Exists(_path);
            if (_existed) _bytes = File.ReadAllBytes(_path);
        }

        public void Put(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, json);
        }

        public void Remove() { if (File.Exists(_path)) File.Delete(_path); }

        public void Dispose()
        {
            if (_existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllBytes(_path, _bytes);
            }
            else if (File.Exists(_path)) File.Delete(_path);
        }
    }

    /// <summary>
    /// The live WinINET settings as a JSON fragment, for a synthetic snapshot.
    ///
    /// The restore script reverts the proxy whenever the app believes it installed one (its
    /// own marker file on disk, or an in-process record). A test that writes a snapshot with
    /// made-up proxy fields therefore rewrites the user's browser proxy the moment it runs on
    /// a machine where sharing is active - which is exactly what happened here: a restore
    /// test blanked ProxyEnable while the desktop was riding the phone. Mirroring whatever
    /// is live makes that branch a no-op, so the suite cannot touch the proxy at all.
    /// </summary>
    private static string LiveInetJson()
    {
        int enable = 0;
        string server = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettings);
            if (key != null)
            {
                enable = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0);
                server = Convert.ToString(key.GetValue("ProxyServer") ?? "");
            }
        }
        catch { /* keep the defaults; the script tolerates empty values */ }

        return $"\"winHttp\":\"\",\"inetEnable\":{enable},\"inetServer\":\"{server}\"}}";
    }

    /// <summary>
    /// A snapshot that asks for an interface that cannot exist. Every Set-NetIPInterface
    /// inside the restore script will fail, so the script reports RESTORED=0 / FAILED=n -
    /// which is exactly the situation the old 'OK' hid.
    /// </summary>
    private static string UnusableSnapshot()
    {
        return "{\"takenAt\":\"unit-test\",\"adapters\":[" +
               "{\"IfIndex\":999999,\"Name\":\"unit-test-nope\",\"Metric\":50," +
               "\"AutoMetric\":\"Disabled\",\"Dns\":\"\"}]," +
               LiveInetJson();
    }

    // ------------------------------------------------------------------ counters

    [Theory]
    [InlineData("RESTORED=3\nFAILED=0", "RESTORED=", 3)]
    [InlineData("RESTORED=0\nFAILED=4", "RESTORED=", 0)]
    [InlineData("  FAILED=2  ", "FAILED=", 2)]
    [InlineData("CHANGED=1\nFAILED=9", "CHANGED=", 1)]
    public void PowerShellCounters_AreParsedFromTheTranscript(string output, string key, int expected)
    {
        Assert.Equal(expected, MobileVpnService.ReadCount(output, key));
    }

    [Fact]
    public void PowerShellCounters_ReportMinusOneWhenTheKeyIsAbsent()
    {
        // -1, not 0: the callers distinguish "the script said zero" from "the script never
        // said anything", and only the first means a genuine no-op.
        Assert.Equal(-1, MobileVpnService.ReadCount("something else", "RESTORED="));
        Assert.Equal(-1, MobileVpnService.ReadCount(null, "RESTORED="));
        Assert.Equal(-1, MobileVpnService.ReadCount("RESTORED=abc", "RESTORED="));
    }

    // ------------------------------------------------------------------ restore

    [Fact]
    public async Task Restore_WithoutASnapshot_SaysThereIsNothingToRestore()
    {
        using var snap = new SnapshotScope();
        snap.Remove();

        MvResult r = await MobileVpnService.Instance.RestoreSnapshotAsync();

        Assert.False(r.Ok);
        Assert.Equal("mv_snapshot_none", r.Key);
    }

    [Fact]
    public async Task Restore_WhoseAdaptersAllFail_ReportsFailure_NotSuccess()
    {
        using var snap = new SnapshotScope();
        snap.Put(UnusableSnapshot());
        string winHttpBefore = TestSupport.WinHttpProxy();

        MvResult r = await MobileVpnService.Instance.RestoreSnapshotAsync();

        Assert.False(r.Ok,
            "a restore that changed nothing was reported as a success - the UI would say the " +
            "network is back to normal while the machine still carries our metrics.");
        Assert.Equal("mv_restore_failed", r.Key);
        Assert.Equal(winHttpBefore, TestSupport.WinHttpProxy());
    }

    [Fact]
    public async Task Restore_PartialFailure_IsReportedAsPartial()
    {
        // One real adapter plus one that cannot exist. Whether the real one can actually be
        // re-metrored depends on elevation - Set-NetIPInterface needs administrator, and the
        // app always runs elevated (app.manifest). Both outcomes are correct, and the point
        // of the test is that neither is ever reported as a clean restore:
        //   elevated    -> RESTORED=1 FAILED=1 -> mv_restore_partial
        //   not elevated -> RESTORED=0 FAILED=2 -> mv_restore_failed
        var real = await MobileVpnService.Instance.DetectAsync(force: true);
        var up = real.Links.FirstOrDefault(l => l.IfIndex > 0 && !l.IsVpn);
        if (up == null) return;      // no usable adapter on this host: nothing to assert

        bool elevated = IsElevated();
        using var snap = new SnapshotScope();
        snap.Put("{\"takenAt\":\"unit-test\",\"adapters\":[" +
                 $"{{\"IfIndex\":{up.IfIndex},\"Name\":\"{up.Name}\",\"Metric\":{Math.Max(up.Metric, 1)}," +
                 "\"AutoMetric\":\"Disabled\",\"Dns\":\"\"}," +
                 "{\"IfIndex\":999999,\"Name\":\"unit-test-nope\",\"Metric\":50," +
                 "\"AutoMetric\":\"Disabled\",\"Dns\":\"\"}]," +
                 LiveInetJson());

        MvResult r = await MobileVpnService.Instance.RestoreSnapshotAsync();

        Assert.False(r.Ok, "a partial restore must not be announced as a clean one");
        Assert.Equal(elevated ? "mv_restore_partial" : "mv_restore_failed", r.Key);

        // The metric we handed the script was the one we just read, so this is a no-op.
        // Assert it anyway: the test must not be able to silently re-metric the machine.
        int metricNow = GetNetIPInterfaceMetric(up.Name);
        Assert.Equal(Math.Max(up.Metric, 1), metricNow);
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static int GetNetIPInterfaceMetric(string name)
    {
        var row = PowerShellOneLine(
            "$i = Get-NetIPInterface -InterfaceAlias '" + name.Replace("'", "''") + "' " +
            "-AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            "if ($i) { $i.InterfaceMetric } else { -1 }");
        return int.TryParse(row.Trim(), out int v) ? v : -1;
    }

    private static string PowerShellOneLine(string script)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                            script.Replace("\"", "\\\"") + "\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output;
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ share guards

    [Fact]
    public async Task Share_RouteMode_WithoutAnInterface_IsRefusedBeforeAnythingRuns()
    {
        using var snap = new SnapshotScope();
        string winHttpBefore = TestSupport.WinHttpProxy();

        MvResult r = await MobileVpnService.Instance.ShareAsync(null, MvMethod.Auto, allowVpnChange: true);

        Assert.False(r.Ok);
        Assert.Equal("mv_no_link", r.Key);
        Assert.Equal(winHttpBefore, TestSupport.WinHttpProxy());
    }

    [Fact]
    public async Task Share_ProxyMode_AgainstAnUnreachableProxy_IsRefusedAndChangesNothing()
    {
        using var snap = new SnapshotScope();

        string previous = MobileVpnService.Instance.PeerProxy;
        string winHttpBefore = TestSupport.WinHttpProxy();
        try
        {
            // Port 1 is privileged and never has a listener, so the probe must refuse.
            MobileVpnService.Instance.PeerProxy = "127.0.0.1:1";

            MvResult r = await MobileVpnService.Instance.ShareAsync(null, MvMethod.Proxy, allowVpnChange: true);

            Assert.False(r.Ok);
            Assert.Equal("mv_proxy_unreachable", r.Key);
            Assert.Equal(winHttpBefore, TestSupport.WinHttpProxy());
            Assert.False(MobileVpnService.Instance.IsSharing);
        }
        finally { MobileVpnService.Instance.PeerProxy = previous; }
    }

    [Fact]
    public async Task Share_ProxyMode_WithoutAProxyAtAll_IsRefused()
    {
        using var snap = new SnapshotScope();

        string previous = MobileVpnService.Instance.PeerProxy;
        try
        {
            MobileVpnService.Instance.PeerProxy = "";
            MvResult r = await MobileVpnService.Instance.ShareAsync(null, MvMethod.Proxy, allowVpnChange: true);

            Assert.False(r.Ok);
            Assert.Equal("mv_no_proxy", r.Key);
        }
        finally { MobileVpnService.Instance.PeerProxy = previous; }
    }

    // ------------------------------------------------------------------ classification

    [Theory]
    [InlineData("USB RNDIS Adapter", "Phone", false, "usb")]
    [InlineData("PdaNet Broadband Connection", "", false, "usb")]
    [InlineData("Android Device", "Ethernet 3", false, "usb")]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", "", false, "hotspot")]
    [InlineData("Local Area Connection* 12", "Hosted Network Adapter", false, "hotspot")]
    [InlineData("Hyper-V Virtual Ethernet Adapter", "", false, "other")]
    [InlineData("Npcap Loopback Adapter", "", false, "other")]
    [InlineData("Intel(R) Ethernet Connection", "Ethernet", false, "lan")]
    // Windows names its Internet Connection Sharing adapter "Local Area Connection* N",
    // which matches none of the hotspot keywords. Pinned here so the day somebody adds it
    // the change is deliberate: today such an adapter is discovered as a plain LAN link,
    // which still works because AnyUpLink falls back to "lan".
    [InlineData("", "Local Area Connection* 10", false, "lan")]
    // The "vpn" verdict is not made here: MobileVpnService feeds in the flag the shared
    // adapter classifier and its own IsVpnLike() produced, so a tunnel reaches
    // ClassifyLink with isVpn already true.
    [InlineData("WireGuard Tunnel", "", true, "vpn")]
    public void Links_AreClassifiedIntoTheTransferMethodTheAppSupports(
        string description, string name, bool isVpn, string expected)
    {
        Assert.Equal(expected, MobileVpnService.ClassifyLink(description, name, isVpn));
    }

    [Fact]
    public void AnyUpLink_PrefersThePhoneOverThePlainLan()
    {
        var state = new MvState
        {
            Links = new System.Collections.Generic.List<MvLink>
            {
                new() { IfIndex = 10, Name = "Ethernet", Kind = "lan", IsUp = true },
                new() { IfIndex = 11, Name = "USB", Kind = "usb", IsUp = true },
            },
        };

        Assert.Equal(11, state.AnyUpLink?.IfIndex);
    }
}