using System.Text.Json;
using NetPilot.Core.Api;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Core.Tests;

/// <summary>
/// The wire format the Android phone and every desktop build have to agree on.
///
/// These tests exist because of a bug that only running the daemon revealed: <c>MvLink</c>
/// and <c>MvResult</c> are declared with public fields, and <c>System.Text.Json</c> ignores
/// fields unless told otherwise. The bridge answered <c>/status</c> with a list of empty
/// objects. It compiled, it started, and it printed a healthy banner - the defect was
/// invisible until a real machine asked it a question.
///
/// So the serializer is now part of the contract, and pinned here rather than left to
/// whichever call site happens to remember the options.
/// </summary>
public class WireContractTests
{
    /// <summary>The one that bit us. If this ever fails again, /status is returning nothing useful.</summary>
    [Fact]
    public void ALinkSerializesWithItsFields()
    {
        var link = new MvLink
        {
            IfIndex = 7,
            Name = "wlan0",
            Description = "wlan0",
            Kind = "lan",
            IsUp = true,
            Ipv4 = "192.168.1.20",
            Gateway = "192.168.1.1",
            Rx = 1234,
            Tx = 5678,
            Metric = 600,
        };

        var json = Serialize(link);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("wlan0", root.GetProperty("name").GetString());
        Assert.Equal("lan", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("isUp").GetBoolean());
        Assert.Equal("192.168.1.20", root.GetProperty("ipv4").GetString());
        Assert.Equal(1234, root.GetProperty("rx").GetInt64());
    }

    /// <summary>The phone reads Ok to decide whether a rule was applied. A dropped field would
    /// make every failure look like a success.</summary>
    [Fact]
    public void AResultSerializesItsOutcome()
    {
        var ok = MvResult.Success("mv_done");
        var failed = MvResult.Fail("daemon_need_root", "nftables needs root");

        using var okDoc = JsonDocument.Parse(Serialize(ok));
        Assert.True(okDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("mv_done", okDoc.RootElement.GetProperty("key").GetString());

        using var failDoc = JsonDocument.Parse(Serialize(failed));
        Assert.False(failDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("daemon_need_root", failDoc.RootElement.GetProperty("key").GetString());
        Assert.Equal("nftables needs root", failDoc.RootElement.GetProperty("detail").GetString());
    }

    /// <summary>A refusal that loses its reason is as useless as no refusal at all - on macOS
    /// the reason ("pf has no process matcher") is the entire message.</summary>
    [Fact]
    public void ARefusalKeepsItsReason()
    {
        var refusal = MvResult.Fail("daemon_unsupported",
            "macOS cannot block one app from another: pf has no process matcher");
        using var doc = JsonDocument.Parse(Serialize(refusal));
        Assert.Contains("process matcher", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public void ATargetKeepsItsIdentity()
    {
        var target = new AppHandle { Uid = 501, Path = "", DisplayName = "uid 501" };
        using var doc = JsonDocument.Parse(Serialize(target));
        Assert.Equal(501, doc.RootElement.GetProperty("uid").GetInt32());
        Assert.Equal("uid:501", doc.RootElement.GetProperty("key").GetString());
    }

    /// <summary>Enums travel as numbers, matching what the Windows app already writes to
    /// limits.json. Asserting a string here would be me inventing a second format rather
    /// than pinning the existing one - the number is what a rule file from the desktop app
    /// deserializes into.</summary>
    [Fact]
    public void LimitsSurviveTheirEnum()
    {
        var rule = new NetPilot.Models.LimitRule
        {
            AppPath = "/usr/bin/firefox",
            Mode = NetPilot.Models.LimitMode.Block,
            DownLimitBps = 0,
            UpLimitBps = 262144,
        };
        using var doc = JsonDocument.Parse(Serialize(rule));
        Assert.Equal((int)NetPilot.Models.LimitMode.Block,
            doc.RootElement.GetProperty("mode").GetInt32());
        Assert.Equal(262144, doc.RootElement.GetProperty("upLimitBps").GetInt64());
    }

    [Theory]
    [InlineData(NetPilot.Models.LimitMode.Allow, 0)]
    [InlineData(NetPilot.Models.LimitMode.Limit, 1)]
    [InlineData(NetPilot.Models.LimitMode.Block, 2)]
    public void EveryLimitModeRoundTrips(NetPilot.Models.LimitMode mode, int expected)
    {
        var rule = new NetPilot.Models.LimitRule { AppPath = "x", Mode = mode };
        using var doc = JsonDocument.Parse(Serialize(rule));
        Assert.Equal(expected, doc.RootElement.GetProperty("mode").GetInt32());
        Assert.Equal(mode, JsonSerializer.Deserialize<NetPilot.Models.LimitRule>(
            Serialize(rule), Options)!.Mode);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ADownLimitOfZeroMeansUnlimitedNotMissing()
    {
        var rule = new NetPilot.Models.LimitRule { AppPath = "x", DownLimitBps = 0 };
        using var doc = JsonDocument.Parse(Serialize(rule));
        Assert.True(doc.RootElement.TryGetProperty("downLimitBps", out var v));
        Assert.Equal(0, v.GetInt64());
    }

    // ---------------- the endpoint contract ----------------

    /// <summary>The phone hard-codes these. A changed path is a breaking protocol change, so
    /// the set is pinned rather than left to drift.</summary>
    [Fact]
    public void TheEndpointSetIsExactlyWhatThePhoneExpects()
    {
        Assert.Equal("GET /api/v1/ping", ApiContract.Ping);
        Assert.Equal("POST /api/v1/pair", ApiContract.Pair);
        Assert.Equal("DELETE /api/v1/pair", ApiContract.Unpair);
        Assert.Equal("GET /api/v1/status", ApiContract.Status);
        Assert.Equal("POST /api/v1/hello", ApiContract.Hello);
        Assert.Equal("POST /api/v1/report", ApiContract.Report);
        Assert.Equal("POST /api/v1/share", ApiContract.Share);
        Assert.Equal("POST /api/v1/stop", ApiContract.Stop);
        Assert.Equal("POST /api/v1/backup", ApiContract.Backup);
        Assert.Equal("POST /api/v1/restore", ApiContract.Restore);
    }

    [Fact]
    public void PortsMatchTheWindowsBuild()
    {
        // The phone has these hard-coded, so a change here silently breaks every existing install.
        Assert.Equal(8787, ApiContract.BridgePort);
        Assert.Equal(8788, ApiContract.ProxyPort);
        Assert.Equal(1, ApiContract.Version);
    }

    /// <summary>Kept in step with MobileVpnService.ApiPort, which the Windows build uses
    /// directly rather than through this class.</summary>
    [Fact]
    public void ThePortsCannotDiverge() =>
        Assert.Equal(8787, ApiContract.BridgePort);

    private static string Serialize(object payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
}