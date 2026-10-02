using System.Threading.Tasks;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Regression suite for the proxy reachability probe.
///
/// The probe is the last gate before the app writes a system-wide proxy into WinHTTP, so a
/// false positive takes the whole machine offline - and a false negative makes a perfectly
/// good phone look dead. Both have happened:
///
///   * the original implementation handed "localhost" to TcpClient.ConnectAsync, which tries
///     the resolver's addresses under ONE deadline. On a host whose IPv6 loopback answers
///     nothing the ::1 attempt burned the entire 1500 ms budget and 127.0.0.1 - which was
///     listening the whole time - was never tried. Measured here: probe("localhost:9936")
///     = false in 1519 ms while probe("127.0.0.1:9936") = true in 54 ms.
///   * the other direction, a dead address answering a bare TCP connect, is rejected by
///     making the far end speak "HTTP/".
/// </summary>
public class ProxyReachableTests
{
    [Fact]
    public async Task Probe_ByHostName_WithOnlyAnIpv4Listener_IsReachable()
    {
        // The exact shape that used to fail: the name resolves to ::1 first, but only the
        // IPv4 loopback is listening. Both addresses must get their own attempt.
        using var fake = TestSupport.StartFakeProxy();

        bool ok = await MobileVpnService.ProxyReachableAsync($"localhost:{fake.Port}");

        Assert.True(ok,
            $"'localhost:{fake.Port}' was reported unreachable although a listener answered " +
            "on 127.0.0.1 - the probe is dropping the IPv4 address again.");
        Assert.True(fake.Connections > 0, "the probe never opened a connection at all");
    }

    [Fact]
    public async Task Probe_ByIpv4Literal_IsReachable()
    {
        using var fake = TestSupport.StartFakeProxy();

        bool ok = await MobileVpnService.ProxyReachableAsync($"127.0.0.1:{fake.Port}");

        Assert.True(ok, "a directly addressed, answering proxy must be accepted");
    }

    [Fact]
    public async Task Probe_ClosedPort_IsUnreachable()
    {
        bool ok = await MobileVpnService.ProxyReachableAsync("127.0.0.1:1");

        Assert.False(ok, "nothing listens on port 1 - the probe must refuse this");
    }

    [Fact]
    public async Task Probe_HostThatCannotResolve_IsUnreachable()
    {
        // .invalid is reserved by RFC 2606 and can never resolve.
        bool ok = await MobileVpnService.ProxyReachableAsync("netpilot-no-such-host.invalid:9999");

        Assert.False(ok);
    }

    [Fact]
    public async Task Probe_TcpConnectButNoHttpAnswer_IsUnreachable()
    {
        // A middlebox that swallows the handshake accepts the connect and then stays silent.
        // The probe must not confuse "the socket opened" with "a proxy is there".
        var bare = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        bare.Start();
        int port = ((System.Net.IPEndPoint)bare.LocalEndpoint).Port;
        // Never accept: the handshake completes (backlog) but no HTTP answer ever arrives.
        try
        {
            bool ok = await MobileVpnService.ProxyReachableAsync($"127.0.0.1:{port}");
            Assert.False(ok, "a socket that never answers must not be accepted as a proxy");
        }
        finally { bare.Stop(); }
    }

    [Fact]
    public async Task Probe_GarbageInput_IsUnreachable()
    {
        Assert.False(await MobileVpnService.ProxyReachableAsync(""));
        Assert.False(await MobileVpnService.ProxyReachableAsync("   "));
        Assert.False(await MobileVpnService.ProxyReachableAsync("no-such-host.invalid"));
    }
}
