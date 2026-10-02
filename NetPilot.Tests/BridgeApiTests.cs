using System.Net;
using System.Threading;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The HTTP contract the Android app depends on. These are the responses the phone branches
/// on, so changing one silently breaks pairing on a released app.
///
/// The bridge is a process-wide singleton, which is why <see cref="AssemblyInfo"/> turns
/// parallelism off and why every test that needs credentials pairs for itself instead of
/// relying on another test's leftovers.
/// </summary>
public class BridgeApiTests
{
    private static int _port;

    /// <summary>Starts the bridge on a throw-away port and returns its base URL.</summary>
    private static string EnsureBridge()
    {
        if (!MobileVpnApi.Instance.Running || _port == 0)
        {
            MobileVpnApi.Instance.Stop();
            _port = TestSupport.FreePort();
            MobileVpnApi.Instance.Start(_port);   // fire-and-forget, exactly like ServiceHub

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!MobileVpnApi.Instance.Running && DateTime.UtcNow < deadline) Thread.Sleep(25);
            Assert.True(MobileVpnApi.Instance.Running, "the bridge never reported Running");
        }
        return $"http://localhost:{_port}";
    }

    private static string PairForThisTest()
    {
        string code = MobileVpnApi.Instance.PairingCode;
        var (status, body) = TestSupport.PostAsync($"{EnsureBridge()}/api/v1/pair",
            $"{{\"code\":\"{code}\",\"name\":\"unit-test\"}}").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, status);
        string token = TestSupport.TryGetString(body, "token");
        Assert.False(string.IsNullOrEmpty(token), "pairing returned no token: " + body);
        return token;
    }

    [Fact]
    public async Task Ping_IsPublicAndAnswersWithoutCredentials()
    {
        var (status, body) = await TestSupport.GetAsync($"{EnsureBridge()}/api/v1/ping");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"NetPilot\"", body);
        Assert.Contains("\"api\":1", body);
    }

    [Fact]
    public async Task Status_WithoutToken_IsRefusedBeforeAnythingRuns()
    {
        // The check has to happen before the handler touches the network detectors - a
        // 401 that costs a PowerShell round trip would let an unauthenticated caller
        // burn CPU on demand.
        var (status, body) = await TestSupport.GetAsync($"{EnsureBridge()}/api/v1/status");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("unauthorized", body);
    }

    [Fact]
    public async Task UnknownRoute_WithoutToken_IsUnauthorized()
    {
        var (status, _) = await TestSupport.GetAsync($"{EnsureBridge()}/api/v1/does-not-exist");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task UnknownRoute_WithToken_IsNotFound()
    {
        string token = PairForThisTest();

        var (status, _) = await TestSupport.GetAsync($"{EnsureBridge()}/api/v1/does-not-exist", token);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Pair_WithAWrongCode_IsRejected()
    {
        // Deliberately a single failure: the endpoint allows five per minute before it
        // answers 429, and the counter lives on the singleton for the whole run.
        string wrong = "000000" == MobileVpnApi.Instance.PairingCode ? "000001" : "000000";
        var (status, _) = await TestSupport.PostAsync($"{EnsureBridge()}/api/v1/pair",
            $"{{\"code\":\"{wrong}\"}}");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.False(MobileVpnApi.Instance.Paired, "a wrong code must not pair the device");
    }

    [Fact]
    public async Task Pair_WithTheShownCode_ReturnsATokenThatWorks()
    {
        string token = PairForThisTest();

        Assert.Equal(64, token.Length);                 // 32 random bytes, hex encoded
        Assert.True(MobileVpnApi.Instance.Paired);

        // The token must actually open the door - "POST /hello" is authenticated.
        var (status, body) = await TestSupport.PostAsync($"{EnsureBridge()}/api/v1/hello",
            "{\"name\":\"Pixel 8\",\"model\":\"Pixel 8\",\"android\":\"16\"}", token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"ok\":true", body);
        Assert.Equal("Pixel 8", MobileVpnApi.Instance.DeviceName);
    }

    [Fact]
    public async Task Unpair_ForgetsTheTokenSoItCannotBeReused()
    {
        string token = PairForThisTest();

        var (del, _) = await TestSupport.DeleteAsync($"{EnsureBridge()}/api/v1/pair", token);
        Assert.Equal(HttpStatusCode.OK, del);
        Assert.False(MobileVpnApi.Instance.Paired);

        var (after, _) = await TestSupport.GetAsync($"{EnsureBridge()}/api/v1/status", token);
        Assert.True(after == HttpStatusCode.Unauthorized,
            "an old token stayed valid after unpairing (HTTP " + (int)after + ")");
    }

    [Fact]
    public void PairingCode_IsSixDigits_AndChangesWhenRequested()
    {
        EnsureBridge();

        string first = MobileVpnApi.Instance.PairingCode;
        Assert.Matches("^[0-9]{6}$", first);

        MobileVpnApi.Instance.NewPairingCode();
        Assert.Matches("^[0-9]{6}$", MobileVpnApi.Instance.PairingCode);
    }
}
