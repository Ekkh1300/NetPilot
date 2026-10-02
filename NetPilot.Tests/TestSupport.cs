using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetPilot.Tests;

/// <summary>Shared plumbing for the suite. Nothing here touches the system's real state.</summary>
internal static class TestSupport
{
    /// <summary>A TcpListener that answers every request the way the proxy probe expects:
    /// an HTTP status line starting with "HTTP/". Fails the test if nothing ever connects.</summary>
    public static FakeEndpoint StartFakeProxy()
    {
        var ep = new FakeEndpoint(IPAddress.Loopback, 0);
        ep.Start();
        return ep;
    }

    /// <summary>A port that is free right now. Not reserved afterwards, which is fine for
    /// tests that bind it immediately.</summary>
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<(HttpStatusCode status, string body)> GetAsync(string url, string token = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await Http.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex) { return (0, ex.Message); }
    }

    public static async Task<(HttpStatusCode status, string body)> PostAsync(string url, string json, string token = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json ?? "", Encoding.UTF8, "application/json");
        try
        {
            using var res = await Http.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex) { return (0, ex.Message); }
    }

    public static async Task<(HttpStatusCode status, string body)> DeleteAsync(string url, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await Http.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex) { return (0, ex.Message); }
    }

    /// <summary>Reads a top-level string property out of a small JSON response.</summary>
    public static string TryGetString(string json, string prop)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(prop, out var v) &&
                v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString();
        }
        catch { /* not JSON: the caller turns null into an assertion message */ }
        return null;
    }

    /// <summary>Same shape the desktop reads out of "netsh winhttp show proxy" - used to prove
    /// a refused share did not touch the machine.</summary>
    public static string WinHttpProxy()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "winhttp show proxy",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("Proxy Server(s)", StringComparison.Ordinal)) return t;
                if (t.StartsWith("Direct access", StringComparison.Ordinal)) return t;
            }
            return output.Trim();
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }
}

/// <summary>
/// A loopback endpoint that speaks just enough HTTP to satisfy
/// <c>MobileVpnService.ProxyReachableAsync</c>: read a request, answer with a status line.
/// </summary>
internal sealed class FakeEndpoint : IDisposable
{
    private readonly TcpListener _listener;
    private Thread _thread;
    private volatile bool _stop;

    public int Port { get; private set; }
    public int Connections;

    public FakeEndpoint(IPAddress address, int port) => _listener = new TcpListener(address, port);

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _thread = new Thread(Loop) { IsBackground = true, Name = "fake-proxy" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); }
            catch { return; }

            Interlocked.Increment(ref Connections);
            try
            {
                using var s = c.GetStream();
                var buf = new byte[4096];
                s.Read(buf, 0, buf.Length);
                byte[] reply = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                s.Write(reply, 0, reply.Length);
                s.Flush();
            }
            catch { /* the probe gave up first */ }
            finally { c.Close(); }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
    }
}

/// <summary>
/// The stand-in for a UI thread: continuations are queued and only run while the owning
/// thread is inside <see cref="PumpOnce"/>. That is exactly the shape that made a blocking
/// <c>.GetResult()</c> on the caller a deadlock - the awaiting side needs this thread, and
/// this thread is waiting for the awaiting side.
/// </summary>
internal sealed class PumpSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>> _queue = new();

    public int Posted => _queue.Count;

    public override void Post(SendOrPostCallback d, object state) =>
        _queue.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));

    // Inline: a synchronous wait from another thread must never be routed through this
    // queue, or the test harness itself would deadlock.
    public override void Send(SendOrPostCallback d, object state) => d(state);

    public bool PumpOnce(int waitMs)
    {
        if (_queue.TryDequeue(out var item))
        {
            item.Key(item.Value);
            return true;
        }
        if (waitMs > 0) Thread.Sleep(waitMs);
        return false;
    }

    /// <summary>Runs the "message loop" until <paramref name="done"/> or the budget runs out.</summary>
    public bool PumpUntil(TimeSpan budget, Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < budget)
        {
            if (done()) return true;
            PumpOnce(25);
        }
        return done();
    }
}
