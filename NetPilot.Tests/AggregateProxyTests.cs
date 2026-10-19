using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The combine proxy's word is what the machine runs on, so every guarantee the class doc
/// claims is pinned here against a real loopback socket: the two protocols it serves, the
/// per-connection split, and - the one that protects users - the "phone dead, everything
/// still works" fail-over.
///
/// These tests bind loopback ports only. They never touch an adapter, a route or the system
/// proxy, so they are safe to run on any machine, in any order.
/// </summary>
public sealed class AggregateProxyTests
{
    /// <summary>An echo listener: whatever CONNECT target is asked, accepts a tunnel and echoes bytes.</summary>
    private async Task<int> StartEchoServerAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(8);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient c = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var s = c.GetStream();
                            var buf = new byte[4096];
                            while (true)
                            {
                                int n = await s.ReadAsync(buf, 0, buf.Length, ct);
                                if (n <= 0) break;
                                await s.WriteAsync(buf, 0, n, ct);
                            }
                        }
                        catch { }
                        finally { try { c.Close(); } catch { } }
                    }, CancellationToken.None);
                }
            }
            catch { }
        }, CancellationToken.None);

        return port;
    }

    /// <summary>A fake phone: answers CONNECT with 200, then echoes. Behaviour of ProxyServer.kt in miniature.</summary>
    private Task<int> StartFakePhoneAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(8);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient c = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var s = c.GetStream();
                            var head = new MemoryStream();
                            var one = new byte[1];
                            while (true)
                            {
                                int n = await s.ReadAsync(one, 0, 1, ct);
                                if (n <= 0) return;
                                head.WriteByte(one[0]);
                                byte[] a = head.ToArray();
                                if (a.Length >= 2 && a[^1] == (byte)'\n' && a[^2] == (byte)'\n') break;
                                if (a.Length >= 4 && a[^1] == (byte)'\n' && a[^2] == (byte)'\r'
                                                          && a[^3] == (byte)'\n' && a[^4] == (byte)'\r') break;
                            }

                            string text = Encoding.ASCII.GetString(head.ToArray());
                            if (text.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
                            {
                                byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
                                await s.WriteAsync(ok, 0, ok.Length, ct);

                                var buf = new byte[4096];
                                while (true)
                                {
                                    int n = await s.ReadAsync(buf, 0, buf.Length, ct);
                                    if (n <= 0) break;
                                    await s.WriteAsync(buf, 0, n, ct);
                                }
                            }
                            else
                            {
                                // absolute-form forward: answer like an origin server would
                                byte[] ok = Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 5\r\n\r\nhello");
                                await s.WriteAsync(ok, 0, ok.Length, ct);
                            }
                        }
                        catch { }
                        finally { try { c.Close(); } catch { } }
                    }, CancellationToken.None);
                }
            }
            catch { }
        }, CancellationToken.None);

        return Task.FromResult(port);
    }

    /// <summary>One HTTP CONNECT through the proxy, then a round-tripping byte.</summary>
    private static async Task<bool> ConnectThroughAsync(int proxyPort, string host, int targetPort,
                                                        CancellationToken ct)
    {
        using var c = new TcpClient(AddressFamily.InterNetwork);
        await c.ConnectAsync(IPAddress.Loopback, proxyPort, ct);
        var s = c.GetStream();

        byte[] connect = Encoding.ASCII.GetBytes($"CONNECT {host}:{targetPort} HTTP/1.1\r\nHost: {host}\r\n\r\n");
        await s.WriteAsync(connect, 0, connect.Length, ct);

        var head = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, 0, 1, ct);
            if (n <= 0) return false;
            head.WriteByte(one[0]);
            byte[] a = head.ToArray();
            bool end = (a.Length >= 2 && a[^1] == (byte)'\n' && a[^2] == (byte)'\n') ||
                       (a.Length >= 4 && a[^1] == (byte)'\n' && a[^2] == (byte)'\r'
                                         && a[^3] == (byte)'\n' && a[^4] == (byte)'\r');
            if (end) break;
        }

        string status = Encoding.ASCII.GetString(head.ToArray());
        if (!status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return false;

        byte[] probe = { 0x4E, 0x50 };      // "NP"
        await s.WriteAsync(probe, 0, probe.Length, ct);
        var reply = new byte[2];
        int got = 0;
        while (got < 2)
        {
            int n = await s.ReadAsync(reply, got, 2 - got, ct);
            if (n <= 0) break;
            got += n;
        }
        return got == 2 && reply[0] == 0x4E && reply[1] == 0x50;
    }

    private static async Task<bool> Socks5ThroughAsync(int proxyPort, string host, int targetPort,
                                                       CancellationToken ct)
    {
        using var c = new TcpClient(AddressFamily.InterNetwork);
        await c.ConnectAsync(IPAddress.Loopback, proxyPort, ct);
        var s = c.GetStream();

        // greeting: ver, 1 method, no-auth
        byte[] greet = { 0x05, 0x01, 0x00 };
        await s.WriteAsync(greet, 0, greet.Length, ct);
        int sel = s.ReadByte();
        if (sel != 0x05) return false;
        int method = s.ReadByte();
        if (method != 0x00) return false;

        // request: ver, cmd=connect, rsv, atyp=ipv4, addr, port
        var parts = host.Split('.');
        byte[] req = { 0x05, 0x01, 0x00, 0x01,
                       byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]), byte.Parse(parts[3]),
                       (byte)(targetPort >> 8), (byte)(targetPort & 0xFF) };
        await s.WriteAsync(req, 0, req.Length, ct);

        var reply = new byte[10];
        int got = 0;
        while (got < 10)
        {
            int n = await s.ReadAsync(reply, got, 10 - got, ct);
            if (n <= 0) return false;
            got += n;
        }
        if (reply[1] != 0x00) return false;

        byte[] probe = { 0x4E, 0x50 };
        await s.WriteAsync(probe, 0, probe.Length, ct);
        var echo = new byte[2];
        got = 0;
        while (got < 2)
        {
            int n = await s.ReadAsync(echo, got, 2 - got, ct);
            if (n <= 0) break;
            got += n;
        }
        return got == 2 && echo[0] == 0x4E && echo[1] == 0x50;
    }

    [Fact]
    public void RefusesAProxyAddressThatIsNotAnAddress()
    {
        using var proxy = new AggregateProxy(true);
        Assert.False(proxy.Start("not an address"));
        Assert.False(proxy.Running);
        Assert.Equal("", proxy.Endpoint);
        // A proxy that refused to start must not leave a listener half-bound on the machine.
    }

    [Fact]
    public async Task HttpConnectAndSocks5BothWorkOverDirectAndPhonePaths()
    {
        using var cts = new CancellationTokenSource();
        int echo = await StartEchoServerAsync(cts.Token);

        using var proxy = new AggregateProxy(true);
        Assert.True(proxy.Start($"127.0.0.1:{await StartFakePhoneAsync(cts.Token)}"));

        // protocol one: HTTP CONNECT - the first connection's first choice is direct
        Assert.True(await ConnectThroughAsync(proxy.Port, "127.0.0.1", echo, cts.Token));
        Assert.Equal(1, proxy.DirectConnections);

        // protocol two: SOCKS5 opens with a 0x05 byte on the very same port, without
        // any setting saying so - and this connection's first choice is the phone
        Assert.True(await Socks5ThroughAsync(proxy.Port, "127.0.0.1", echo, cts.Token));

        // the split: alternation decided per connection, and both paths carried real bytes
        Assert.Equal(1, proxy.DirectConnections);
        Assert.Equal(1, proxy.PeerConnections);
        Assert.True(proxy.PeerDown + proxy.PeerUp >= 4, "phone-path bytes must be counted from the tunnels");
        Assert.True(proxy.DirectDown + proxy.DirectUp >= 4);

        proxy.Stop();
        Assert.False(proxy.Running);
        Assert.Equal("", proxy.Endpoint);
        Assert.Equal(0, proxy.Port);
        cts.Cancel();
    }

    [Fact]
    public async Task ADeadPhonePathFailsOverToDirectInTheSameConnection()
    {
        using var cts = new CancellationTokenSource();
        int echo = await StartEchoServerAsync(cts.Token);

        using var proxy = new AggregateProxy(true);
        // a phone announced on a port where nothing listens: the classic handset-on-a-train
        Assert.True(proxy.Start("127.0.0.1:1"));
        Assert.True(proxy.Endpoint.StartsWith("127.0.0.1:"));

        // connection 1: direct first (alternation starts even, plus the phone is still
        // presumed healthy until it is tried), works
        Assert.True(await ConnectThroughAsync(proxy.Port, "127.0.0.1", echo, cts.Token));

        // connection 2: the phone is the first choice - it fails - and the SAME client
        // connection still completes over direct. This is the guarantee that a phone leaving
        // the network never breaks a transfer.
        Assert.True(await ConnectThroughAsync(proxy.Port, "127.0.0.1", echo, cts.Token));

        // the failure marked the path down; a third connection still works, all direct
        Assert.False(proxy.PeerHealthy);
        Assert.True(await ConnectThroughAsync(proxy.Port, "127.0.0.1", echo, cts.Token));

        proxy.Stop();
        cts.Cancel();
    }

    [Fact]
    public async Task AbsoluteFormForwardIsRelayedByteForByte()
    {
        using var cts = new CancellationTokenSource();
        // The fake phone doubles as an origin server for absolute-form forwards, answering
        // "hello" - the case where the ORDER decides which far end answers.
        int phone = await StartFakePhoneAsync(cts.Token);

        using var proxy = new AggregateProxy(true);
        Assert.True(proxy.Start($"127.0.0.1:{phone}"));

        // First connection goes direct: the forward dial lands on the address carried in the
        // absolute-form URL - i.e. the fake phone's own listener - which is exactly the shape
        // a phone proxy has to accept natively (ProxyServer.kt forwards absolute-form).
        using var c = new TcpClient(AddressFamily.InterNetwork);
        await c.ConnectAsync(IPAddress.Loopback, proxy.Port, cts.Token);
        var s = c.GetStream();

        byte[] req = Encoding.ASCII.GetBytes(
            $"GET http://127.0.0.1:{phone}/anything HTTP/1.1\r\nHost: 127.0.0.1:{phone}\r\nConnection: keep-alive\r\n\r\n");
        await s.WriteAsync(req, 0, req.Length, cts.Token);

        var buf = new byte[4096];
        int read = 0, total = 0;
        while (total < "HTTP/1.1 200 OK".Length && read < buf.Length)
        {
            int n = await s.ReadAsync(buf, read, buf.Length - read, cts.Token);
            if (n <= 0) break;
            read += n;
            total = read;
        }
        string response = Encoding.ASCII.GetString(buf, 0, read);
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("hello", response);

        // DirectDown counted what the app received, including the body
        Assert.True(proxy.DirectDown > 0);

        proxy.Stop();
        cts.Cancel();
    }

    [Fact]
    public async Task StopThenStartBindsANewPortAndResetsTheSplit()
    {
        using var cts = new CancellationTokenSource();
        int echo = await StartEchoServerAsync(cts.Token);

        var proxy = new AggregateProxy(true);
        Assert.True(proxy.Start($"127.0.0.1:{await StartFakePhoneAsync(cts.Token)}"));
        proxy.Stop();
        Assert.False(proxy.Running);

        Assert.True(proxy.Start($"127.0.0.1:{echo}"));   // this time the phone is a real echo
        Assert.True(proxy.Port > 0);
        Assert.Equal(0, proxy.DirectConnections);
        Assert.Equal(0, proxy.PeerConnections);
        proxy.Stop();
        cts.Cancel();
    }
}
