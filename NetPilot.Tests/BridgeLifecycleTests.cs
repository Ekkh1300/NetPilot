using System;
using System.Net;
using System.Threading;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Startup/shutdown behaviour of the bridge.
///
/// These tests exist because of a bug that was invisible to every other check in the
/// project: <c>MobileVpnApi.Start</c> used to block the thread that ServiceHub calls it
/// from (the UI thread) on a PowerShell child with <c>.GetResult()</c>. The listener was
/// up, http.sys had the process attached, the firewall rule existed - and the accept loop
/// was never started, so every request from the phone hung until it timed out. netstat,
/// the rule, and the port all looked perfectly healthy.
///
/// The invariant is therefore stated in terms of what a caller can observe: starting the
/// bridge must hand control back to its caller, and requests must be answered while that
/// caller is doing nothing but pumping its own message loop.
/// </summary>
public class BridgeLifecycleTests
{
    [Fact]
    public void Start_FromAUiThread_ReturnsImmediately_AndServesWhileThatThreadPumps()
    {
        int port = TestSupport.FreePort();
        MobileVpnApi.Instance.Stop();

        var pump = new PumpSynchronizationContext();
        var startReturned = new ManualResetEventSlim(false);
        var uiFinished = new ManualResetEventSlim(false);
        var answered = new ManualResetEventSlim(false);

        var ui = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                // Exactly what ServiceHub.Init does during Application startup.
                MobileVpnApi.Instance.Start(port);
                startReturned.Set();

                // Now behave like a message loop: no other thread work, only continuations
                // posted to this context, until somebody has managed to talk to us.
                pump.PumpUntil(TimeSpan.FromSeconds(15), () => answered.IsSet);
            }
            catch (Exception) { startReturned.Set(); }
            finally { uiFinished.Set(); }
        })
        { IsBackground = true, Name = "fake-ui-thread" };
        ui.Start();

        // (1) Start() must not block its caller. The old implementation never returned
        //     here once the wildcard bind succeeded, because it waited for PowerShell.
        bool returned = startReturned.Wait(TimeSpan.FromSeconds(5));
        MobileVpnApi.Instance.Stop();
        Assert.True(returned,
            "MobileVpnApi.Start() blocked the thread that called it for over 5 s. " +
            "It must be fire-and-forget: the UI thread cannot wait for a child process.");

        // (2) And the accept loop must actually be running, which is what the phone sees.
        //     Restart (Stop() above released the port) and probe from this thread while
        //     the fake UI thread pumps. The Host header has to be "localhost": HttpListener
        //     matches it against the registered prefix, and a non-elevated start registers
        //     "http://localhost:port/" - a request that says 127.0.0.1 would be refused
        //     by the HTTP stack before it ever reached the app.
        MobileVpnApi.Instance.Start(port);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string url = $"http://localhost:{port}/api/v1/ping";
        while (DateTime.UtcNow < deadline && !answered.IsSet)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connect = client.ConnectAsync("127.0.0.1", port);
                if (connect.Wait(500) && client.Connected)
                {
                    using var stream = client.GetStream();
                    byte[] req = System.Text.Encoding.ASCII.GetBytes(
                        "GET /api/v1/ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
                    stream.Write(req, 0, req.Length);
                    stream.ReadTimeout = 2000;
                    var buf = new byte[64];
                    int n = stream.Read(buf, 0, buf.Length);
                    string head = System.Text.Encoding.ASCII.GetString(buf, 0, Math.Max(n, 0));
                    if (head.StartsWith("HTTP/1.", StringComparison.Ordinal) &&
                        head.Contains("200")) answered.Set();
                }
            }
            catch { /* listener not up yet */ }
            Thread.Sleep(200);
        }

        uiFinished.Wait(TimeSpan.FromSeconds(20));
        MobileVpnApi.Instance.Stop();

        Assert.True(answered.IsSet,
            "The port was open but nothing answered: the accept loop never started, which is " +
            "the exact symptom the phone reports as 'PC unreachable / No answer in time'.");
    }

    [Fact]
    public void FirewallStep_MustFinishWhileItsCallerOnlyPumps()
    {
        // The deadlock needs a single-threaded context to show up at all - that is what a
        // UI thread is - so the test installs one and refuses to do anything except pump.
        // If any part of the firewall path ever blocks the caller again, this loop stops
        // running, the task can never resume, and the assertion below fails.
        var pump = new PumpSynchronizationContext();
        bool finished = false;
        bool timedOutInside = false;

        var ui = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(pump);
            // ApiPort, not a random one: if this host has no rule yet, the call creates it,
            // and a rule for a throw-away port would leave the real bridge blocked while
            // telling the app the rule already exists.
            var task = MobileVpnApi.EnsureInboundRuleAsync(MobileVpnService.ApiPort);
            finished = pump.PumpUntil(TimeSpan.FromSeconds(45), () => task.IsCompleted);
            if (!finished) timedOutInside = task.Status == TaskStatus.Running;
        })
        { IsBackground = true, Name = "fake-ui-thread" };
        ui.Start();

        Assert.True(ui.Join(TimeSpan.FromSeconds(70)), "the fake UI thread never returned");
        Assert.False(timedOutInside,
            "EnsureInboundRuleAsync did not complete while its caller pumped: it is waiting on " +
            "the very thread whose message loop can only run after it finishes (deadlock).");
        Assert.True(finished, "the firewall step did not complete within 45 s");
    }

    [Fact]
    public void Stop_ReportsStopped_AndCanBeStartedAgain()
    {
        int port = TestSupport.FreePort();

        MobileVpnApi.Instance.Start(port);
        SpinWait.SpinUntil(() => MobileVpnApi.Instance.Running, 10_000);
        Assert.True(MobileVpnApi.Instance.Running);

        MobileVpnApi.Instance.Stop();
        Assert.False(MobileVpnApi.Instance.Running);

        // A restart must work: the app is started, stopped from the tray and started again
        // in normal use, and the second Start() re-creates the listener from scratch.
        MobileVpnApi.Instance.Start(port);
        SpinWait.SpinUntil(() => MobileVpnApi.Instance.Running, 10_000);
        Assert.True(MobileVpnApi.Instance.Running, "the bridge could not be restarted");

        MobileVpnApi.Instance.Stop();
        Assert.False(MobileVpnApi.Instance.Running);
    }
}
