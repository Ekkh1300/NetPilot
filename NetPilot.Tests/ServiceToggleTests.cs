using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The "Stop Service" / "Start Service" button on the phone-tunnel page.
///
/// Users reported the button as dead. The cause was not the click: <c>Stop</c> closed the
/// listener, Windows kept the http.sys URL group for a moment, and the next <c>Start</c>
/// failed with "address already in use". The catch swallowed it, so the page just sat at
/// "stopped" with no explanation - a stop-then-start looked exactly like a button that does
/// nothing.
///
/// These tests drive the same calls the command makes and assert the round trip actually
/// serves again.
/// </summary>
public class ServiceToggleTests : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public void Dispose() => MobileVpnApi.Instance.Stop();

    private static string Base(int port) => $"http://localhost:{port}/api/v1/ping";

    private static async Task<bool> Answers(int port)
    {
        try
        {
            using var res = await Http.GetAsync(Base(port));
            return (int)res.StatusCode == 200;
        }
        catch { return false; }
    }

    [Fact]
    public async Task StopThenStart_ServesAgain()
    {
        int port = TestSupport.FreePort();

        MobileVpnApi.Instance.Start(port);
        Assert.True(MobileVpnApi.Instance.Running);
        Assert.True(await WaitFor(() => Answers(port)), "the bridge did not answer after Start");

        MobileVpnApi.Instance.Stop();
        Assert.False(MobileVpnApi.Instance.Running);
        Assert.False(await Answers(port), "the bridge still answered after Stop");

        // The regression: this is what the button does, and it used to fail silently.
        MobileVpnApi.Instance.Start(port);
        Assert.True(MobileVpnApi.Instance.Running,
            "Start after Stop failed: " + MobileVpnApi.Instance.LastError);
        Assert.True(await WaitFor(() => Answers(port)),
            "the bridge did not answer after being restarted");
    }

    [Fact]
    public async Task RepeatedToggles_NeverGetStuck()
    {
        // Users click the toggle repeatedly; every cycle has to come back up.
        int port = TestSupport.FreePort();
        for (int i = 0; i < 3; i++)
        {
            if (!MobileVpnApi.Instance.Running) MobileVpnApi.Instance.Start(port);
            Assert.True(MobileVpnApi.Instance.Running, $"cycle {i}: " + MobileVpnApi.Instance.LastError);
            Assert.True(await WaitFor(() => Answers(port)), $"cycle {i} never served");

            MobileVpnApi.Instance.Stop();
            Assert.False(MobileVpnApi.Instance.Running, $"cycle {i}: Stop left it running");
        }
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        int port = TestSupport.FreePort();
        MobileVpnApi.Instance.Start(port);
        MobileVpnApi.Instance.Stop();
        MobileVpnApi.Instance.Stop();          // must not throw
        Assert.False(MobileVpnApi.Instance.Running);
    }

    [Fact]
    public void Start_WhileRunning_IsANoOp()
    {
        int port = TestSupport.FreePort();
        MobileVpnApi.Instance.Start(port);
        Assert.True(MobileVpnApi.Instance.Running);
        MobileVpnApi.Instance.Start(port);     // must not throw or restart underneath the phone
        Assert.True(MobileVpnApi.Instance.Running);
        Assert.Equal("", MobileVpnApi.Instance.LastError);
    }

    private static async Task<bool> WaitFor(Func<Task<bool>> ok, int seconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await ok()) return true;
            await Task.Delay(150);
        }
        return false;
    }
}
