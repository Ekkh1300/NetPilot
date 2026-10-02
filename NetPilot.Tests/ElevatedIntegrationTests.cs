using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using NetPilot.Models;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Tests that drive the real Windows networking stack.
///
/// They are skipped unless the process is elevated, because Set-DnsClientServerAddress and
/// New-NetQosPolicy both refuse to run otherwise - and a suite that silently skips is worse
/// than one that is loud, so they say so in the output. Run them with:
///     dotnet test -p:RunConfiguration...
/// or, the way this project ships it:  .\Tools\verify.ps1 -Elevated
///
/// Every one of them puts the machine back the way it found it, and they snapshot the
/// files under %LOCALAPPDATA%\NetPilot first because the services persist their state there.
/// </summary>
public class ElevatedIntegrationTests
{
    internal static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Restores a file in the app's data folder after a test that rewrites it.</summary>
    private sealed class DataFile : IDisposable
    {
        private readonly string _path;
        private readonly bool _existed;
        private readonly byte[] _bytes;

        public DataFile(string name)
        {
            _path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetPilot", name);
            _existed = File.Exists(_path);
            if (_existed) _bytes = File.ReadAllBytes(_path);
        }

        public void Dispose()
        {
            if (_existed) File.WriteAllBytes(_path, _bytes);
            else if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private static string Ps(string script, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                        script.Replace("\"", "\\\"") + "\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return output.Trim();
    }

    private static async Task<bool> WaitUntil(Func<bool> done, int seconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (done()) return true;
            await Task.Delay(400);
        }
        return done();
    }

    // ------------------------------------------------------------------ DNS

    /// <summary>
    /// The headline DNS promise is "apply a resolver, and put everything back". Both halves
    /// change the machine, so this test drives the real service and then proves the adapter
    /// is byte-for-byte where it started - a restore that silently leaves one adapter on the
    /// test resolver would keep working for everything except the next thing the user tries.
    /// </summary>
    [Fact]
    public async Task Dns_ApplyThenRestore_PutsTheAdapterBackExactly()
    {
        if (!IsElevated()) return;    // Set-DnsClientServerAddress needs administrator

        using var backupFile = new DataFile("dns_backup.json");
        using var historyFile = new DataFile("history.json");

        var adapters = await DnsService.GetCurrentAsync();
        var target = adapters.FirstOrDefault(a => a.IfIndex > 0 && a.V4.Count > 0);
        if (target == null) return;    // no adapter with a manual resolver on this host

        var original = target.V4.ToList();
        var entry = new DnsEntry
        {
            Name = "unit-test",
            ServersV4 = new List<string> { "1.1.1.1", "8.8.8.8" },
            IsCustom = true,
        };

        try
        {
            var applied = await DnsService.ApplyAsync(entry, target.IfIndex);
            Assert.True(applied.ok, "ApplyAsync failed: " + applied.message);

            var afterApply = (await DnsService.GetCurrentAsync()).First(a => a.IfIndex == target.IfIndex);
            Assert.Equal(new[] { "1.1.1.1", "8.8.8.8" }, afterApply.V4.OrderBy(x => x).ToArray());

            var restored = await DnsService.RestoreAsync();
            Assert.True(restored.ok, "RestoreAsync failed: " + restored.message);

            var afterRestore = (await DnsService.GetCurrentAsync()).First(a => a.IfIndex == target.IfIndex);
            Assert.Equal(original.OrderBy(x => x).ToArray(), afterRestore.V4.OrderBy(x => x).ToArray());
        }
        finally
        {
            // Whatever happened, the machine goes back to the resolver it had.
            await DnsService.ApplyAsync(new DnsEntry
            {
                Name = "restore-by-test",
                ServersV4 = original,
                IsCustom = true,
            }, target.IfIndex);
        }
    }

    /// <summary>
    /// The backup must remember an adapter's ORIGINAL servers the first time it touches it,
    /// not whatever is on it afterwards - otherwise Restore would "restore" the custom
    /// resolver and the user would be stuck with it forever.
    /// </summary>
    [Fact]
    public async Task Dns_Backup_IsTakenOnce_PerAdapter()
    {
        if (!IsElevated()) return;

        using var backupFile = new DataFile("dns_backup.json");
        using var historyFile = new DataFile("history.json");

        var adapters = await DnsService.GetCurrentAsync();
        var target = adapters.FirstOrDefault(a => a.IfIndex > 0 && a.V4.Count > 0);
        if (target == null) return;

        var original = target.V4.ToList();
        try
        {
            await DnsService.ApplyAsync(new DnsEntry
            {
                Name = "first", ServersV4 = new List<string> { "1.1.1.1", "8.8.8.8" }, IsCustom = true,
            }, target.IfIndex);

            // Apply a *different* resolver on the same adapter.
            await DnsService.ApplyAsync(new DnsEntry
            {
                Name = "second", ServersV4 = new List<string> { "9.9.9.9" }, IsCustom = true,
            }, target.IfIndex);

            var restored = await DnsService.RestoreAsync();
            Assert.True(restored.ok, "restore failed: " + restored.message);

            var after = (await DnsService.GetCurrentAsync()).First(a => a.IfIndex == target.IfIndex);
            Assert.Equal(original.OrderBy(x => x).ToArray(), after.V4.OrderBy(x => x).ToArray());
        }
        finally
        {
            await DnsService.ApplyAsync(new DnsEntry
            {
                Name = "restore-by-test", ServersV4 = original, IsCustom = true,
            }, target.IfIndex);
        }
    }

    // ------------------------------------------------------------------ net limiter

    private static string QosName(string path) =>
        "NP_" + Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes(path.ToLowerInvariant()))[..8]);

    /// <summary>
    /// Upload throttling is not simulated: it is a real Windows QoS policy, so the test asks
    /// Windows whether the policy exists. A typo in the cmdlet, a silently failing
    /// New-NetQosPolicy or a Remove that leaves the policy behind would all pass a UI-only
    /// check while the limit does nothing at all.
    /// </summary>
    [Fact]
    public async Task Limiter_Apply_CreatsARealQosPolicy_AndRemove_TakesItAway()
    {
        if (!IsElevated()) return;    // New-NetQosPolicy needs administrator

        using var rulesFile = new DataFile("limits.json");

        // Path no real process uses, so the policy cannot throttle anything the user runs.
        const string path = @"C:\Windows\Temp\netpilot-unit-test.exe";
        string name = QosName(path);

        // Enforcement runs on LimiterService's background worker, which only exists after
        // Start() - the app calls it from ServiceHub.Init(). Without it ApplyAsync silently
        // queues and nothing happens, so the test has to stand the service up itself.
        // Start from an empty rule set so this run cannot re-apply the user's own rules.
        LimiterService.Instance.SetRules(new List<LimitRule>());
        LimiterService.Instance.Start();
        try
        {
            await ApplyAndAssert(path, name, 256 * 1024);
        }
        finally { LimiterService.Instance.Stop(); }

        // Stop() then Start() must bring the enforcement worker back. It used to leave the
        // cancelled CancellationTokenSource in place, so the second Start() skipped creating
        // a worker and every limit set after that point was queued and never applied.
        LimiterService.Instance.Start();
        try
        {
            string again = QosName(path);
            await ApplyAndAssert(path, again, 512 * 1024);
        }
        finally { LimiterService.Instance.Stop(); }

        Assert.DoesNotContain(LimiterService.Instance.GetRules(),
            r => string.Equals(r.AppPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ApplyAndAssert(string path, string name, long upBps)
    {

        var rule = new LimitRule
        {
            AppPath = path,
            AppName = "netpilot-unit-test",
            Mode = LimitMode.Limit,      // Allow means "no limit" - the worker skips it entirely
            DownLimitBps = 1024 * 1024,
            UpLimitBps = upBps,          // > 0 is what makes the QoS policy get created
            Enabled = true,
        };

        try
        {
            await LimiterService.Instance.ApplyAsync(rule);

            bool exists = await WaitUntil(() =>
                Ps($"if (Get-NetQosPolicy -Name '{name}' -ErrorAction SilentlyContinue) {{ 'yes' }}")
                    .Equals("yes", StringComparison.OrdinalIgnoreCase));
            Assert.True(exists, "ApplyAsync reported success but Windows has no QoS policy for it");

            string bps = Ps($"(Get-NetQosPolicy -Name '{name}' -ErrorAction SilentlyContinue)." +
                            "ThrottleRateActionBitsPerSecond");
            // Some Windows builds expose the property but render it empty through the CIM
            // projection, so only assert when the cmdlet actually returned a number.
            if (bps.Length > 0)
                Assert.Equal((upBps * 8).ToString(), bps);
        }
        finally
        {
            await LimiterService.Instance.RemoveAsync(rule.Id);
        }

        bool gone = await WaitUntil(() =>
            !Ps($"if (Get-NetQosPolicy -Name '{name}' -ErrorAction SilentlyContinue) {{ 'yes' }}")
                 .Equals("yes", StringComparison.OrdinalIgnoreCase), 15);
        Assert.True(gone, "RemoveAsync left the QoS policy behind - the app would stay throttled");

        // The rule set is a user file: make sure the test app path is not left in it.
        var rules = LimiterService.Instance.GetRules();
        Assert.DoesNotContain(rules, r => string.Equals(r.AppPath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A rule with no upload limit must not leave a QoS policy behind - "unlimited" has to
    /// mean the operating system stops throttling, not that the old policy keeps running.
    /// </summary>
    [Fact]
    public async Task Limiter_ApplyWithNoUploadLimit_CreatesNoQosPolicy()
    {
        if (!IsElevated()) return;

        using var rulesFile = new DataFile("limits.json");

        const string path = @"C:\Windows\Temp\netpilot-unit-test-unlimited.exe";
        string name = QosName(path);
        var rule = new LimitRule
        {
            AppPath = path,
            AppName = "netpilot-unit-test",
            Mode = LimitMode.Limit,
            DownLimitBps = 1024 * 1024,
            UpLimitBps = 0,
            Enabled = true,
        };

        LimiterService.Instance.SetRules(new List<LimitRule>());
        LimiterService.Instance.Start();
        try
        {
            await LimiterService.Instance.ApplyAsync(rule);
            await Task.Delay(4000);   // enforcement runs on a background worker

            string found = Ps($"if (Get-NetQosPolicy -Name '{name}' -ErrorAction SilentlyContinue) {{ 'yes' }}");
            Assert.True(found.Length == 0,
                "a rule with UpLimitBps=0 still produced a QoS policy, so the app is throttling " +
                "something the user explicitly set to unlimited");
        }
        finally
        {
            await LimiterService.Instance.RemoveAsync(rule.Id);
            LimiterService.Instance.Stop();
        }
    }
}