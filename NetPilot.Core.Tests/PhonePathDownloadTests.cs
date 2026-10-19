using System;
using System.Collections.Generic;
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
/// A phone in miniature: an HTTP proxy on loopback whose CONNECT responses behave the way
/// ProxyServer.kt on the handset does. The bonded download tests use it to prove that a
/// BondNetwork built over a proxy - rather than bound to an adapter - downloads real bytes
/// through real ranges.
/// </summary>
public sealed class LoopbackPhoneProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    public LoopbackPhoneProxy()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(AcceptLoopAsync, _stop.Token);
    }

    public int Port { get; }
    private long _connects;
    public int ConnectCount => (int)Interlocked.Read(ref _connects);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }

            _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using var c = client;
            var stream = c.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            string first = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(first)) return;

            var headers = new List<string>();
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) headers.Add(line);

            // The download engine downloads over plain http, so the request arrives in
            // absolute form (GET http://host:port/path) - exactly what ProxyServer.kt relays
            // natively. CONNECT is what an HTTPS request opens with.
            string[] parts = first.Split(' ');
            if (parts.Length < 2 || !first.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await RelayAbsoluteFormAsync(parts, headers, stream);
                return;
            }

            var authority = parts[1].Split(':');
            int port = authority.Length == 2 ? int.Parse(authority[1]) : 80;

            var target = new TcpClient(AddressFamily.InterNetwork);
            try { await target.ConnectAsync(authority[0], port, _stop.Token); }
            catch { return; }

            byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
            await stream.WriteAsync(ok, 0, ok.Length, _stop.Token);
            Interlocked.Increment(ref _connects);
            var up = target.GetStream();
            await PipeAsync(stream, up, _stop.Token);
            await PipeAsync(up, stream, _stop.Token);
            try { up.Close(); } catch { }
        }
        catch { }
    }

    /// <summary>Forwards an absolute-form request to its origin in origin form, then relays the
    /// reply until the origin closes. One request per connection, like the phone's own relay.</summary>
    private async Task RelayAbsoluteFormAsync(string[] parts, List<string> headers, Stream client)
    {
        if (parts.Length < 2 || !parts[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;

        string rest = parts[1].Substring("http://".Length);
        int slash = rest.IndexOf('/');
        string authority = slash >= 0 ? rest.Substring(0, slash) : rest;
        string path = slash >= 0 ? rest.Substring(slash) : "/";

        var hostPort = authority.Split(':');
        int port = hostPort.Length == 2 ? int.Parse(hostPort[1]) : 80;

        var target = new TcpClient(AddressFamily.InterNetwork);
        try { await target.ConnectAsync(hostPort[0], port, _stop.Token); }
        catch { return; }

        try
        {
            var up = target.GetStream();
            var rebuilt = new StringBuilder(256);
            rebuilt.Append(parts[0]).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            foreach (string h in headers)
            {
                string name = h.Substring(0, Math.Max(0, h.IndexOf(':'))).ToLowerInvariant();
                if (name is "proxy-connection" or "connection" or "keep-alive") continue;
                rebuilt.Append(h).Append("\r\n");
            }
            rebuilt.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(rebuilt.ToString());
            await up.WriteAsync(headBytes, 0, headBytes.Length, _stop.Token);
            Interlocked.Increment(ref _connects);

            // The response all the way back; then both ends close, which is the contract
            // Connection: close made.
            var buf = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    int n = await up.ReadAsync(buf, 0, buf.Length, _stop.Token);
                    if (n <= 0) break;
                    await client.WriteAsync(buf, 0, n, _stop.Token);
                    await client.FlushAsync(_stop.Token);
                }
            }
            catch { }
            try { up.Close(); } catch { }
        }
        catch { }
        finally { try { target.Dispose(); } catch { } }
    }

    private static async Task PipeAsync(Stream src, Stream dst, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await src.ReadAsync(buf, 0, buf.Length, ct);
                if (n <= 0) return;
                await dst.WriteAsync(buf, 0, n, ct);
                // no flush here: NetworkStream without buffering flushes on every write anyway
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { _stop.Cancel(); _listener.Stop(); } catch { }
    }
}

/// <summary>
/// The bonded download over the phone path - the whole mechanism, end to end: a
/// <see cref="BondNetwork"/> that dials through a proxy rather than binding an adapter
/// address must download exactly the bytes the server generated, in order, through the
/// proxy's CONNECT tunnels.
/// </summary>
public sealed class PhonePathDownloadTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
                                                "netpilot-phone-" + Guid.NewGuid().ToString("N"));

    public PhonePathDownloadTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task AssertFileMatchesAsync(string path, byte[] expected)
    {
        Assert.True(File.Exists(path), $"{path} was never finished");
        var actual = await File.ReadAllBytesAsync(path);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"bytes diverged at offset {i}: expected {expected[i]}, got {actual[i]}");
        }
    }

    [Fact]
    public async Task DownloadGoesThroughTheProxyAndArrivesByteExact()
    {
        using var server = new RangeTestServer(8 * MB);
        using var phone = new LoopbackPhoneProxy();

        var networks = new[] { BondNetwork.ViaProxy("phone", "127.0.0.1", phone.Port, 4) };
        var dest = Path.Combine(_dir, "phone-path.bin");
        var dl = new BondedDownload(server.Url, dest, networks);

        await dl.RunAsync();

        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(8 * MB));
        Assert.True(phone.ConnectCount > 0, "the download never dialled the phone proxy");
    }
}
