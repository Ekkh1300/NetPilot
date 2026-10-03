using System.Collections.Generic;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The download shaper's decision, shared by all three platforms so the same rule behaves the
/// same wherever it runs.
///
/// It shapes by dropping, which is why it is portable: Windows toggles a firewall rule,
/// Linux and macOS toggle an nftables rule. The arithmetic is here, so a change to it is
/// tested once instead of drifting per platform.
/// </summary>
public class TokenBucketTests
{
    private static readonly (string Key, double DownBps)[] Nothing = System.Array.Empty<(string, double)>();

    [Fact]
    public void TrafficUnderTheLimit_DoesNotBlock()
    {
        var b = new TokenBucket();
        bool blocked = b.IsOverBudget("/usr/bin/app", 1000, seconds: 1,
            new[] { ("/usr/bin/app", 400d) });      // 400 B in 1 s against a 1000 B/s limit
        Assert.False(blocked);
    }

    [Fact]
    public void TrafficOverTheLimit_Blocks()
    {
        var b = new TokenBucket();
        bool blocked = b.IsOverBudget("/usr/bin/app", 1000, seconds: 1,
            new[] { ("/usr/bin/app", 4000d) });
        Assert.True(blocked);
    }

    /// <summary>The cap is what stops a throttled download from banking credit and then
    /// bursting later at several times its limit.</summary>
    [Fact]
    public void CreditIsCappedAtTwiceTheLimit()
    {
        var b = new TokenBucket();
        // Nothing consumed for ten seconds at 1000 B/s: credit would be 10000 without a cap.
        for (int i = 0; i < 10; i++)
            Assert.False(b.IsOverBudget("app", 1000, seconds: 1, Nothing));

        // With the cap at 2x, a 3000 B/s transfer must still be refused.
        Assert.True(b.IsOverBudget("app", 1000, seconds: 1, new[] { ("app", 3000d) }));
    }

    [Fact]
    public void ABurstUpToTwiceTheLimit_IsAllowed()
    {
        var b = new TokenBucket();
        b.IsOverBudget("app", 1000, seconds: 10, Nothing);      // fills the bucket to 2x
        Assert.False(b.IsOverBudget("app", 1000, seconds: 1, new[] { ("app", 1500d) }));
    }

    [Fact]
    public void TheBucketNeverGoesNegative()
    {
        var b = new TokenBucket();
        b.IsOverBudget("app", 1000, seconds: 1, new[] { ("app", 99999d) });
        Assert.Equal(0, b.Peek("app"));
        Assert.True(b.Peek("app") >= 0);
    }

    /// <summary>Chrome alone runs five processes on one binary. Charging only the first lets
    /// the rule pass roughly five times its limit - the reason traffic is summed per app.</summary>
    [Fact]
    public void AllProcessesOfOneAppShareOneBucket()
    {
        var b = new TokenBucket();
        bool blocked = b.IsOverBudget("chrome", 1000, seconds: 1, new[]
        {
            ("chrome", 300d),      // five processes, each just under the limit on its own
            ("chrome", 300d),
            ("chrome", 300d),
            ("chrome", 300d),
            ("chrome", 300d),
        });
        Assert.True(blocked);
    }

    [Fact]
    public void OtherAppsTrafficIsNotChargedToThisApp()
    {
        var b = new TokenBucket();
        bool blocked = b.IsOverBudget("app", 1000, seconds: 1, new[]
        {
            ("app", 100d),
            ("something-else", 900000d),      // busy neighbour must not throttle us
        });
        Assert.False(blocked);
    }

    [Fact]
    public void AnUnlimitedRuleNeverBlocks()
    {
        var b = new TokenBucket();
        Assert.False(b.IsOverBudget("app", 0, seconds: 1, new[] { ("app", 1e9d) }));
    }

    [Fact]
    public void AppsAreTrackedIndependently()
    {
        var b = new TokenBucket();
        b.IsOverBudget("a", 1000, seconds: 1, new[] { ("a", 100000d) });   // a is drained
        Assert.False(b.IsOverBudget("b", 1000, seconds: 1, new[] { ("b", 100d) }));
    }

    /// <summary>Matching is case-insensitive because the same app is named differently by the
    /// OS (a .app bundle path vs the executable) and must land in the same bucket.</summary>
    [Fact]
    public void MatchingIgnoresCase()
    {
        var b = new TokenBucket();
        b.IsOverBudget("C:\\Program\\App.EXE", 1000, seconds: 1, new[] { ("c:\\program\\app.exe", 50000d) });
        Assert.True(b.IsOverBudget("C:\\Program\\App.EXE", 1000, seconds: 1, new[] { ("c:\\program\\app.exe", 50000d) }));
    }

    [Fact]
    public void ForgetDropsABucketEntirely()
    {
        var b = new TokenBucket();
        b.IsOverBudget("app", 1000, seconds: 5, Nothing);
        Assert.True(b.Peek("app") > 0);
        b.Forget("app");
        Assert.Equal(0, b.Peek("app"));
    }

    [Fact]
    public void ClearDropsEveryBucket()
    {
        var b = new TokenBucket();
        b.IsOverBudget("a", 1000, seconds: 5, Nothing);
        b.IsOverBudget("b", 1000, seconds: 5, Nothing);
        b.Clear();
        Assert.Equal(0, b.Peek("a"));
        Assert.Equal(0, b.Peek("b"));
    }
}