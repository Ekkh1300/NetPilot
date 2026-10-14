using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NetPilot.Tests;

/// <summary>
/// A real HTTP server that understands Range requests, built on a bare TcpListener.
///
/// Written by hand rather than using HttpListener, for two reasons. HttpListener needs a URL
/// reservation to bind outside an elevated process on Windows, which would make this test
/// depend on how the machine running it is configured. And this server has to do two things a
/// general-purpose one will not: report the source address of every connection it accepts -
/// which is the only way to observe that a socket really was bound to a specific network - and
/// answer slowly for chosen byte ranges, so a test can reproduce the "one slow network holds the
/// download up" case on demand.
/// </summary>
internal sealed class RangeTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _payload;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<IPAddress> _sources = new();
    private readonly ConcurrentBag<(long From, long To, int Ms)> _slowRules = new();
    private readonly ConcurrentBag<string> _servedRanges = new();

    /// <summary>When false, every request gets 200 and the whole body, Range header or not.</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>Value the server advertises as its version marker.</summary>
    public string ETag { get; set; } = "\"test-v1\"";

    private int _dropsRemaining;
    private int _dropsMade;

    /// <summary>How many requests were actually dropped - so a test can prove the failure it
    /// thinks it is reproducing really happened, rather than passing by coincidence.</summary>
    public int DropsMade => Volatile.Read(ref _dropsMade);

    /// <summary>
    /// Drop the next <paramref name="count"/> block requests instead of answering them.
    ///
    /// This is what a real network does all the time and a test server never does on its own:
    /// the connection simply goes away partway through. A downloader that treats every one as
    /// fatal finishes at whatever percentage it had reached, which is the failure this exists
    /// to reproduce. The one-byte probe is never dropped, since a dead probe is a dead server
    /// rather than a flaky one.
    /// </summary>
    public void DropBlockRequests(int count) => _dropsRemaining = count;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/{_name}";
    private readonly string _name = "payload.bin";

    /// <summary>Every local address a connection arrived from, in order.</summary>
    public IReadOnlyCollection<IPAddress> SourceAddresses => _sources.ToArray();

    /// <summary>The Range values every request asked for.</summary>
    public IReadOnlyCollection<string> RangesRequested => _servedRanges.ToArray();

    public RangeTestServer(long size)
    {
        _payload = new byte[size];
        for (long i = 0; i < size; i++) _payload[i] = (byte)((i * 31 + 7) % 251);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Make a byte range take <paramref name="milliseconds"/> to answer.</summary>
    public void SlowDown(long from, long to, int milliseconds) =>
        _slowRules.Add((from, to, milliseconds));

    /// <summary>Deterministic content, so a test can compare byte-for-byte without a fixture.</summary>
    public static byte[] PayloadOf(long size)
    {
        var b = new byte[size];
        for (long i = 0; i < size; i++) b[i] = (byte)((i * 31 + 7) % 251);
        return b;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { break; }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var c = client;
        try
        {
            _sources.Add(((IPEndPoint)client.Client.RemoteEndPoint).Address);

            using var stream = c.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            // Keep-alive: one connection serves many requests, exactly as a real download's
            // streams do. A server that closed after each request would test the wrong thing.
            while (!_stop.IsCancellationRequested)
            {
                var request = await ReadRequestAsync(reader);
                if (request == null) return;

                if (!await HandleOneAsync(request, stream, _stop.Token)) return;
            }
        }
        catch { }   // a client that hangs up mid-stream is normal here
    }

    private async Task<string> ReadRequestAsync(StreamReader reader)
    {
        var first = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(first)) return null;

        var headers = new List<string> { first };
        string line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) headers.Add(line);
        return string.Join("\n", headers);
    }

    /// <returns>false when the connection should be closed.</returns>
    private async Task<bool> HandleOneAsync(string request, Stream stream, CancellationToken ct)
    {
        var lines = request.Split('\n');
        var parts = lines[0].Split(' ');
        string range = null;
        foreach (var h in lines.Skip(1))
        {
            int colon = h.IndexOf(':');
            if (colon > 0 && h[..colon].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                range = h[(colon + 1)..].Trim();
        }

        bool keepAlive = !lines.Any(l => l.StartsWith("Connection: close", StringComparison.OrdinalIgnoreCase));
        bool headOnly = parts[0] == "HEAD";

        long from = 0, to = _payload.Length - 1;
        bool partial = false;
        if (SupportsRanges && range != null && range.StartsWith("bytes="))
        {
            var spec = range["bytes=".Length..].Split('-');
            if (spec[0].Length > 0) from = long.Parse(spec[0]);
            if (spec.Length > 1 && spec[1].Length > 0) to = Math.Min(long.Parse(spec[1]), _payload.Length - 1);
            partial = true;
            _servedRanges.Add($"{from}-{to}");
        }

        if (from > to)
        {
            await WriteAsync(stream,
                "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), ct);
            return keepAlive;
        }

        foreach (var (f, t, ms) in _slowRules)
            if (from >= f && to <= t) await Task.Delay(ms, ct);

        int length = (int)(to - from + 1);
        var header = partial
            ? $"HTTP/1.1 206 Partial Content\r\n" +
              $"Content-Range: bytes {from}-{to}/{_payload.Length}\r\n" +
              $"Content-Length: {length}\r\n"
            : $"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\n";

        header += $"ETag: {ETag}\r\n" +
                  "Last-Modified: Wed, 14 Oct 2026 12:00:00 GMT\r\n" +
                  "Content-Type: application/octet-stream\r\n";

        // The range a stream asked for is the whole reason the engine works, so it is recorded
        // and can be asserted on rather than assumed.
        if (partial) _servedRanges.Add($"asked {range}");

        header += keepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n";

        // The header always goes out, dropped or not. Leaving it inside the drop branch below
        // sends a body with no response in front of it, which the client reads as a status line
        // made of payload bytes and then waits on forever.
        await WriteAsync(stream, Encoding.ASCII.GetBytes(header), ct);

        // A connection that dies partway through a transfer, reproduced on demand.
        //
        // Deliberately written as "full header, half the body, hang up" rather than "hang up with
        // no response at all". An HTTP client silently retries the second kind on a fresh
        // connection - measured here: one exception costs the server four requests - so a test
        // built on it never reaches the downloader's own recovery and would pass whether or not
        // that recovery existed. Once the response has started the request cannot be repeated,
        // so the failure has to be survived by the code under test rather than around it.
        if (partial && !(from == 0 && to == 0) && Interlocked.Decrement(ref _dropsRemaining) >= 0)
        {
            Interlocked.Increment(ref _dropsMade);
            await stream.WriteAsync(_payload.AsMemory((int)from, length / 2), ct);
            await stream.FlushAsync(ct);
            return false;
        }

        if (!headOnly) await WriteAsync(stream, _payload.AsMemory((int)from, length), ct);
        return keepAlive;
    }

    private static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
    }
}