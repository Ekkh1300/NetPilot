using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Core.Logging;

namespace NetPilot.Services;

/// <summary>
/// The local endpoint that gives the machine two internets as one.
///
/// Windows apps do not know about phones and adapters; they know proxies. This class is an
/// HTTP/SOCKS5 proxy listening on loopback, and for every connection it accepts it decides
/// which path that connection leaves through: the machine's own internet (the default route,
/// untouched) or the paired phone's HTTP proxy (its mobile data). One connection goes one way,
/// the next the other, so an app opening fifteen connections for one page splits them across
/// both links by itself - aggregation at connection granularity, which asks nothing from any
/// application.
///
/// The loopback protocol is sniffed rather than configured: SOCKS5 opens with byte 0x05, HTTP
/// with an ASCII method, so one port serves clients of both kinds and no setting anywhere has
/// to say which is which.
///
/// Failure behaviour matters more than the split. If the phone path dies (handset left the
/// network, tunnel broken), connections marked for it would fail - including ones that could
/// have gone direct. So when a dial through the phone refuses, that same connection is retried
/// over direct in the same breath, and the phone path is marked down for a bounded window
/// during which everything goes direct. Nothing re-opens the path but real traffic: when the
/// window expires, the next natural connection tries the phone again - a probe that costs
/// nothing and cannot wedge anything. The same retry runs the other way when direct fails and
/// the phone is up, so a client connection is never broken because its first choice was
/// unreachable.
///
/// There is deliberately no packet-level magic here: no TUN driver, no route surgery, nothing
/// to install. That also draws the honest boundary of what this aggregates - the apps that
/// honour the machine's proxy settings, which on Windows means nearly everything except games
/// and a few native clients. The UI says exactly that, in the same words.
/// </summary>
public sealed class AggregateProxy : IDisposable
{
    public static AggregateProxy Instance { get; } = new();

    private AggregateProxy() { }

    /// <summary>Test constructor: isolated counters and turns, so tests of one behaviour do
    /// not inherit the connection history of another. The singleton keeps its role in the app.</summary>
    internal AggregateProxy(bool _)
    {
    }

    // ------------------------------------------------------------------ limits

    /// <summary>Concurrent connections accepted. Windows' own proxy client (WinINET) opens a
    /// handful, browsers around a dozen; this is a ceiling against a runaway client opening
    /// thousands, not a budget for real use.</summary>
    private const int MaxOpenConnections = 256;

    /// <summary>How long the phone path stays out after it proved unreachable. Short enough
    /// that a phone which re-joins the network within an app session is found again without
    /// anyone restarting anything; long enough that it is not retried on every new connection
    /// while it is genuinely gone.</summary>
    private static readonly TimeSpan PeerDownWindow = TimeSpan.FromSeconds(20);

    private const int DialTimeoutMs = 8_000;
    private const int HeadLimitBytes = 32 * 1024;

    // ------------------------------------------------------------------ live state

    private IPEndPoint _peer;                      // the phone's HTTP proxy
    private DateTime _peerDownUntil = DateTime.MinValue;
    private long _turns;                           // one increment decides per new connection

    private TcpListener _listener;
    private CancellationTokenSource _cts;
    private Task _acceptLoop;
    private int _openConnections;

    // Per-path counters, written with Interlocked from the relay pumps, read from the UI.
    // Down = bytes that reached an app from a target (downloads); up = the rest.
    private long _directDown, _directUp, _peerDown, _peerUp;
    private long _directConnections, _peerConnections, _rejectedConnections;

    public bool Running { get; private set; }

    /// <summary>The port bound on 127.0.0.1, or 0 while not running.</summary>
    public int Port { get; private set; }

    /// <summary>"127.0.0.1:port", the value the proxy settings get pointed at; "" when off.</summary>
    public string Endpoint => Running && Port > 0 ? $"127.0.0.1:{Port}" : "";

    /// <summary>True while the phone path is believed reachable. When it is not, this proxy
    /// sends everything direct - which has to be visible on the page, or "both internets,
    /// everywhere" becomes a one-internet promise nobody keeps.</summary>
    public bool PeerHealthy => _peer != null && _peerDownUntil <= DateTime.UtcNow;

    public long DirectDown => Interlocked.Read(ref _directDown);
    public long DirectUp => Interlocked.Read(ref _directUp);
    public long PeerDown => Interlocked.Read(ref _peerDown);
    public long PeerUp => Interlocked.Read(ref _peerUp);
    public long DirectConnections => Interlocked.Read(ref _directConnections);
    public long PeerConnections => Interlocked.Read(ref _peerConnections);

    /// <summary>Connections refused because the ceiling was reached; a climbing number
    /// means a client is misbehaving, so it is counted rather than silently shed.</summary>
    public long RejectedConnections => Interlocked.Read(ref _rejectedConnections);

    /// <summary>Set once the phone path has ever been found dead during this run, so the
    /// page can say "phone path down, going direct" rather than a bare green light.</summary>
    public bool PeerEverDown { get; private set; }

    /// <summary>The address this proxy was told the phone lives on, for the status line.</summary>
    public string PeerText => _peer == null ? "" : $"{_peer.Address}:{_peer.Port}";

    // ------------------------------------------------------------------ start / stop

    /// <summary>
    /// Starts listening on 127.0.0.1. <paramref name="peerProxy"/> is the paired phone's
    /// "host:port", the exact shape <see cref="MobileVpnService.PeerProxy"/> carries.
    /// True when the listener bound. The phone is NOT probed here: the first real connection
    /// is the probe, so a phone that went offline in between does not stop the proxy from
    /// binding - direct must keep working even when the phone path cannot.
    /// </summary>
    public bool Start(string peerProxy)
    {
        if (Running)
        {
            if (string.Equals(PeerTextWithoutScheme(), (peerProxy ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            Stop();
        }

        IPEndPoint peer = ResolvePeer(peerProxy);
        if (peer == null)
        {
            Log.Warn("combine", $"refused a phone path that is not an address: {Redactor.Apply(peerProxy ?? "")}");
            return false;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start(64);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _peer = peer;
            _peerDownUntil = DateTime.MinValue;
            PeerEverDown = false;
            _turns = 0;
            _directDown = _directUp = _peerDown = _peerUp = 0;
            _directConnections = _peerConnections = _rejectedConnections = 0;
            _openConnections = 0;

            _cts = new CancellationTokenSource();
            Running = true;
            var token = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);

            Log.Info("combine", $"combine proxy bound on 127.0.0.1:{Port}; phone path {peer}");
            return true;
        }
        catch (Exception ex)
        {
            Running = false;
            Port = 0;
            Log.Error("combine", "could not bind the combine proxy", ex);
            return false;
        }
    }

    private string PeerTextWithoutScheme()
    {
        string s = PeerText;
        return s;
    }

    private static IPEndPoint ResolvePeer(string peerProxy)
    {
        string s = (peerProxy ?? "").Trim();
        if (s.Length == 0 || !MobileVpnService.IsValidProxyAddress(s)) return null;

        int schemeAt = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeAt >= 0) s = s.Substring(schemeAt + 3);
        int lastColon = s.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == s.Length - 1) return null;

        string host = s.Substring(0, lastColon);
        if (!int.TryParse(s.Substring(lastColon + 1), out int port) || port < 1 || port > 65535) return null;

        try
        {
            var addr = Dns.GetHostAddresses(host)
                          .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return addr == null ? null : new IPEndPoint(addr, port);
        }
        catch { return null; }
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;

        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        _listener = null;
        _peer = null;
        Port = 0;
        Log.Info("combine", "combine proxy stopped");
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ accept loop

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (IOException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn("combine", "accept failed; the proxy keeps listening: " + ex.Message);
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { }
                continue;
            }

            if (Interlocked.Increment(ref _openConnections) > MaxOpenConnections)
            {
                Interlocked.Decrement(ref _openConnections);
                Interlocked.Increment(ref _rejectedConnections);
                Log.Warn("combine", $"connection refused: {MaxOpenConnections} are already open");
                try { client.Close(); } catch { }
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, ct).ConfigureAwait(false); }
                catch (Exception ex) { Log.Debug("combine", "connection ended: " + ex.Message); }
                finally
                {
                    try { client.Close(); } catch { }
                    Interlocked.Decrement(ref _openConnections);
                }
            }, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ connection entry

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        client.ReceiveTimeout = IdleSocketTimeoutMs;
        client.SendTimeout = IdleSocketTimeoutMs;
        var stream = client.GetStream();

        // One byte decides the protocol. SOCKS5 greetings begin 0x05; HTTP begins with an
        // ASCII method character. Read the byte rather than peek: both readers replay it.
        int first = stream.ReadByte();
        if (first < 0) return;

        if (first == 0x05)
        {
            await ServeSocks5Async(stream, ct).ConfigureAwait(false);
        }
        else
        {
            // replay the sniffed byte into the head reader: the request line read here begins
            // with the method's first character, and losing it turned "CONNECT" into "ONNECT",
            // which then fell through to the 400 path and broke every HTTP client using the proxy
            await ServeHttpAsync(stream, ct, (byte)first).ConfigureAwait(false);
        }
    }

    private const int IdleSocketTimeoutMs = 120_000;

    // ------------------------------------------------------------------ path choice

    /// <summary>
    /// True when this connection's first choice is the phone.
    ///
    /// Strict alternation while both paths are healthy, and always false while the phone is
    /// down or none is known. Alternation rather than random: page loads arrive as bursts of
    /// two to eight connections from one client, and a strict alternate keeps both paths in
    /// play on every burst. A random walk lets whole bursts stamp through one path.
    /// </summary>
    private bool WantsPeerFirst()
    {
        if (_peer == null || !PeerHealthy) return false;
        return Interlocked.Increment(ref _turns) % 2 == 0;
    }

    private void MarkPeerDown(string why)
    {
        PeerEverDown = true;
        _peerDownUntil = DateTime.UtcNow + PeerDownWindow;
        Log.Warn("combine", $"phone path down for {PeerDownWindow.TotalSeconds:0}s: {why}; " +
                            "this connection and the next ones all go direct");
    }

    // ------------------------------------------------------------------ HTTP proxying

    /// <summary>
    /// Serves one HTTP-proxy connection for its whole lifetime: a CONNECT is turned into a
    /// blind tunnel that ends the connection when it ends, absolute-form requests are
    /// forwarded and answered, and the loop then reads the next request because the client
    /// may keep its socket open (keep-alive).
    /// </summary>
    private async Task ServeHttpAsync(NetworkStream client, CancellationToken ct, byte sniffedFirstByte)
    {
        // The byte the protocol sniffer consumed is replayed into the very first head read;
        // after the first request it has already been consumed, so the replay happens once.
        int? pendingFirst = sniffedFirstByte;

        while (!ct.IsCancellationRequested)
        {
            string head = await ReadHeadAsync(client, ct, pendingFirst).ConfigureAwait(false);
            pendingFirst = null;
            if (head is null) return;

            string[] lines = head.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            string first = lines[0].Trim();
            string[] parts = first.Split(' ');
            if (parts.Length < 2) { Respond(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"); return; }

            string method = parts[0].ToUpperInvariant();
            string target = parts[1];

            List<string> headers = new();
            foreach (string raw in lines.Skip(1))
            {
                if (raw.Length == 0) break;
                headers.Add(raw);
            }

            if (method == "CONNECT")
            {
                if (!await ServeConnectAsync(client, target, ct).ConfigureAwait(false)) return;
                return;   // a tunnel owns the connection to its end
            }

            (string host, int port, string path) targetParts = ParseTarget(method, target, headers);
            if (targetParts.host is null)
            {
                Respond(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n");
                return;
            }

            if (!await ForwardRequestAsync(client, method, targetParts, headers, ct).ConfigureAwait(false))
                return;   // the failure was announced on the wire already

            // next keep-alive request or end of stream, whichever comes first
        }
    }

    /// <summary>CONNECT host:port - choose a path, dial the target through it, then pipe.</summary>
    private async Task<bool> ServeConnectAsync(NetworkStream client, string target, CancellationToken ct)
    {
        (string host, int port) = SplitAuthority(target, defaultPort: 443);
        if (host.Length == 0 || port <= 0)
        {
            Respond(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n");
            return false;
        }

        var dial = await DialTargetAsync(host, port, ct).ConfigureAwait(false);
        if (dial == null)
        {
            Respond(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n");
            return false;
        }

        try
        {
            Respond(client, "HTTP/1.1 200 Connection established\r\n\r\n");
            await RelayBothAsync(client, dial.Value.Remote, dial.Value.ViaPeer, ct).ConfigureAwait(false);
        }
        finally
        {
            try { dial.Value.Remote.Close(); } catch { }
        }
        return true;
    }

    /// <summary>
    /// A plain HTTP request: one fresh upstream per request, connected per its chosen path,
    /// told "Connection: close", and its response relayed until the upstream ends - then the
    /// loop offers the same downstream socket back for the next request.
    ///
    /// The copy-until-close framing is the part a byte-forwarding proxy cannot get wrong:
    /// with close requested, chunked, content-length and close-delimited responses all end
    /// with the upstream's own EOF, which is exactly what the code waits for. Framing
    /// attributes pass through untouched; nothing is terminated, decoded or re-wrapped.
    /// </summary>
    private async Task<bool> ForwardRequestAsync(NetworkStream client, string method,
                                                 (string host, int port, string path) target,
                                                 List<string> headers, CancellationToken ct)
    {
        if (!target.path.StartsWith('/'))
        {
            Respond(client, "HTTP/1.1 501 Unsupported\r\nConnection: close\r\n\r\n");
            Log.Warn("combine", "a request body this relay cannot forward was refused rather than mangled");
            return false;
        }

        var dial = await DialTargetAsync(target.host, target.port, ct).ConfigureAwait(false);
        if (dial == null)
        {
            Respond(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n");
            return false;
        }

        NetworkStream up = dial.Value.Remote;
        try
        {
            // Rebuild the request. The phone's proxy natively takes absolute-form, so that is
            // what it gets; a direct dial speaks to the origin instead and takes origin-form.
            var rebuilt = new StringBuilder(256);
            rebuilt.Append(method).Append(' ')
                   .Append(dial.Value.ViaPeer
                       ? $"http://{Authority(target.host, target.port)}{target.path}"
                       : target.path)
                   .Append(" HTTP/1.1\r\n");

            foreach (string h in headers)
            {
                string name = h.Substring(0, Math.Max(0, h.IndexOf(':'))).ToLowerInvariant();
                if (name is "proxy-connection" or "connection" or "keep-alive"
                         or "proxy-authorization" or "proxy-authenticate") continue;
                if (name == "te") continue;
                rebuilt.Append(h.Trim()).Append("\r\n");
            }
            rebuilt.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(rebuilt.ToString());
            await up.WriteAsync(headBytes, 0, headBytes.Length, ct).ConfigureAwait(false);

            // A request body arrives right after the blank line when Content-Length says so.
            long bodyLen = ParseContentLength(headers);
            if (bodyLen > 0)
            {
                var buffer = new byte[64 * 1024];
                long left = bodyLen;
                while (left > 0)
                {
                    int n = await client.ReadAsync(buffer, 0,
                        (int)Math.Min(buffer.Length, left), ct).ConfigureAwait(false);
                    if (n <= 0) { Log.Debug("combine", "client sent a shorter body than its Content-Length"); return false; }
                    await up.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                    left -= n;
                    Count(dial.Value.ViaPeer, up: true, n);
                }
            }

            // The response: copy until the upstream closes. Counted the same way as tunnels.
            await RelayResponseAsync(up, client, dial.Value.ViaPeer, ct).ConfigureAwait(false);
            try { await client.FlushAsync(ct).ConfigureAwait(false); } catch { }
        }
        finally
        {
            try { up.Close(); } catch { }
        }
        return true;
    }

    /// <summary>absolute-form http://host[:port]/path -> origin parts; origin-form -> host from the Host header.</summary>
    private (string host, int port, string path) ParseTarget(string method, string target,
                                                             List<string> headers)
    {
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return SplitAbsolute(target, defaultPort: 80);

        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SplitAbsolute(target, defaultPort: 443);

        // Some clients hand an origin-form target even through a proxy. It is still servable:
        // the Host header carries the authority the request line left out.
        if (target.StartsWith('/'))
        {
            foreach (string h in headers)
                if (h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    (string host, int port) = SplitAuthority(h.Substring(5).Trim(), defaultPort: 80);
                    if (host.Length > 0) return (host, port, target);
                }
        }
        return (null, 0, null);
    }

    private static (string host, int port, string path) SplitAbsolute(string target, int defaultPort)
    {
        string rest = target.Substring(target.IndexOf("://", StringComparison.Ordinal) + 3);
        int slash = rest.IndexOf('/');
        string authority = slash >= 0 ? rest.Substring(0, slash) : rest;
        string path = slash >= 0 ? rest.Substring(slash) : "/";

        (string host, int port) = SplitAuthority(authority, defaultPort);
        return host.Length == 0 ? (null, 0, null) : (host, port, path);
    }

    /// <summary>host"[:port]" - the last colon splits, tolerating a bracketed IPv6 literal.</summary>
    private static (string host, int port) SplitAuthority(string authority, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(authority)) return ("", 0);

        string host = authority;
        int port = defaultPort;

        int bracket = authority.IndexOf(']');
        int lastColon = authority.LastIndexOf(':');
        if (lastColon > 0 && lastColon > bracket)
        {
            string maybePort = authority.Substring(lastColon + 1);
            if (int.TryParse(maybePort, out int p) && p > 0 && p < 65536)
            {
                host = authority.Substring(0, lastColon);
                port = p;
            }
        }
        return (host, port);
    }

    private static string Authority(string host, int port)
        => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";

    private static long ParseContentLength(List<string> headers)
    {
        foreach (string h in headers)
        {
            int colon = h.IndexOf(':');
            if (colon <= 0) continue;
            if (h.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(h.Substring(colon + 1).Trim(), out var len) && len > 0) return len;
                return 0;
            }
        }
        return 0;
    }

    /// <summary>Reads a request head to its blank line. Bounded; returns null on EOF or size overflow.
    /// <paramref name="replay"/> is pushed in front of the stream - the byte the protocol sniffer
    /// took, so the method never arrives missing its first letter.</summary>
    private static async Task<string> ReadHeadAsync(NetworkStream client, CancellationToken ct, int? replay)
    {
        var ms = new MemoryStream(1024);
        if (replay is int b0) ms.WriteByte((byte)b0);
        byte[] one = new byte[1];

        while (ms.Length < HeadLimitBytes)
        {
            int n;
            try { n = await client.ReadAsync(one, 0, 1, ct).ConfigureAwait(false); }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            if (n <= 0) return null;   // client closed before sending anything complete

            byte b = one[0];
            ms.WriteByte(b);

            if (b == (byte)'\n')
            {
                var a = ms.ToArray();
                if (a.Length >= 4 && a[^1] == (byte)'\n' && a[^2] == (byte)'\r' && a[^3] == (byte)'\n' && a[^4] == (byte)'\r')
                    return Encoding.ASCII.GetString(a);
                if (a.Length >= 2 && a[^1] == (byte)'\n' && a[^2] == (byte)'\n')
                    return Encoding.ASCII.GetString(a);
            }
        }
        return null;   // a header that big is not one we are willing to keep in memory
    }

    private static void Respond(NetworkStream client, string text)
    {
        try
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            client.Write(b, 0, b.Length);
            client.Flush();
        }
        catch { /* the client already went away */ }
    }

    // ------------------------------------------------------------------ SOCKS5

    private async Task ServeSocks5Async(NetworkStream client, CancellationToken ct)
    {
        // Greeting: version byte was consumed by the sniffer; read method count + methods.
        int nmethods = client.ReadByte();
        if (nmethods <= 0 || nmethods > 32) return;

        var methods = new byte[nmethods];
        if (!await ReadExactlyAsync(client, methods, methods.Length, ct).ConfigureAwait(false)) return;

        // Only unauthenticated access is offered: the listener is on loopback only, so the
        // proxy is reachable by processes on this machine and by nobody else.
        if (!methods.Contains((byte)0x00))
        {
            Respond(client, new[] { (byte)0x05, (byte)0xFF });
            return;
        }

        Respond(client, new[] { (byte)0x05, (byte)0x00 });

        // Request: ver (0x05, known), cmd, reserved, address type
        var cmd = new byte[4];
        if (!await ReadExactlyAsync(client, cmd, 4, ct).ConfigureAwait(false)) return;
        if (cmd[1] != 0x01)   // CONNECT; UDP ASSOCIATE comes with no client in this repo
        {
            Respond(client, new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            return;
        }

        string host;
        switch (cmd[3])
        {
            case 0x01:
                {
                    var a = new byte[4];
                    if (!await ReadExactlyAsync(client, a, 4, ct).ConfigureAwait(false)) return;
                    host = $"{a[0]}.{a[1]}.{a[2]}.{a[3]}";
                    break;
                }
            case 0x03:
                {
                    int len = client.ReadByte();
                    if (len <= 0 || len > 255) return;
                    var a = new byte[len];
                    if (!await ReadExactlyAsync(client, a, len, ct).ConfigureAwait(false)) return;
                    host = Encoding.ASCII.GetString(a);
                    break;
                }
            case 0x04:
                {
                    var a = new byte[16];
                    if (!await ReadExactlyAsync(client, a, 16, ct).ConfigureAwait(false)) return;
                    host = new IPAddress(a).ToString();
                    break;
                }
            default: return;
        }

        var pr = new byte[2];
        if (!await ReadExactlyAsync(client, pr, 2, ct).ConfigureAwait(false)) return;
        int port = (pr[0] << 8) | pr[1];
        if (port <= 0 || host.Length == 0) return;

        var dial = await DialTargetAsync(host, port, ct).ConfigureAwait(false);
        if (dial == null)
        {
            // general failure, per RFC 1928
            Respond(client, new byte[] { 0x05, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            return;
        }

        try
        {
            // Success; the bind address is irrelevant to these clients (127.0.0.1:0 is the
            // conventional filler, and every client ignores it).
            Respond(client, new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            await RelayBothAsync(client, dial.Value.Remote, dial.Value.ViaPeer, ct).ConfigureAwait(false);
        }
        finally
        {
            try { dial.Value.Remote.Close(); } catch { }
        }
    }

    private static void Respond(NetworkStream client, byte[] bytes)
    {
        try { client.Write(bytes, 0, bytes.Length); client.Flush(); } catch { }
    }

    // ------------------------------------------------------------------ dialing

    /// <summary>
    /// Opens a connection to <paramref name="host"/>:<paramref name="port"/> on whichever path
    /// comes first, and falls back to the other path in the same breath when the first refuses.
    /// Returns null only when every available path refused - the caller then says so once,
    /// honestly, to the client.
    /// </summary>
    private async Task<(NetworkStream Remote, bool ViaPeer)?> DialTargetAsync(string host, int port,
                                                                              CancellationToken ct)
    {
        bool preferPeer = WantsPeerFirst();
        (bool viaPeer, string why)[] attempts = _peer == null
            ? new[] { (false, "direct") }
            : preferPeer
                ? new[] { (true, "phone"), (false, "direct") }
                : new[] { (false, "direct"), (true, "phone") };

        foreach (var (viaPeer, _) in attempts)
        {
            if (ct.IsCancellationRequested) return null;

            NetworkStream remote = viaPeer
                ? await DialPeerAsync(host, port, ct).ConfigureAwait(false)
                : await DialDirectAsync(host, port, ct).ConfigureAwait(false);

            if (remote != null) return (remote, viaPeer);
        }
        return null;
    }

    /// <summary>Dials an origin host from this machine, over the machine's own default route.</summary>
    /// <remarks>Never "marked down": the default route belongs to the whole machine and heals
    /// on its own, so one refused dial claims nothing about it. Rejections are spoken to the
    /// caller in the usual way.</remarks>
    private async Task<NetworkStream> DialDirectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(DialTimeoutMs);
                await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);

                Interlocked.Increment(ref _directConnections);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                return null;
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Dials an origin host through the paired phone: TCP to the phone's proxy, then CONNECT
    /// host:port, then wait for its 200. Anything else - connect refused, slow handshake, a
    /// 502 from its side of the tunnel - marks the whole phone path down for
    /// <see cref="PeerDownWindow"/>, because every one of those failures points at the same
    /// single fact: the tunnel is not carrying traffic right now.
    /// </summary>
    private async Task<NetworkStream> DialPeerAsync(string host, int port, CancellationToken ct)
    {
        var peer = _peer;
        if (peer == null || !PeerHealthy) return null;

        Socket socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(DialTimeoutMs);
                await socket.ConnectAsync(peer, timeout.Token).ConfigureAwait(false);
            }

            var ns = new NetworkStream(socket, ownsSocket: true);

            byte[] connect = Encoding.ASCII.GetBytes(
                $"CONNECT {Authority(host, port)} HTTP/1.1\r\nHost: {Authority(host, port)}\r\n\r\n");
            await ns.WriteAsync(connect, 0, connect.Length, ct).ConfigureAwait(false);

            if (!await PhoneAcceptedAsync(ns, ct).ConfigureAwait(false))
            {
                ns.Close();
                MarkPeerDown($"its proxy answered {host}:{port} without 200");
                return null;
            }

            Interlocked.Increment(ref _peerConnections);
            return ns;
        }
        catch (OperationCanceledException)
        {
            if (socket != null) { try { socket.Dispose(); } catch { } }
            MarkPeerDown($"proxy connect to {host}:{port} timed out");
            return null;
        }
        catch (Exception)
        {
            if (socket != null) { try { socket.Dispose(); } catch { } }
            MarkPeerDown($"proxy connect to {host}:{port} failed");
            return null;
        }
    }

    /// <summary>
    /// Reads the phone's reply to its first line and drains the rest of the response head.
    /// The head's bytes would corrupt the client's stream if re-sent (`200 Connection
    /// established` is not the phone's body - connect replies carry nothing worth keeping),
    /// so they are consumed here rather than forwarded. Bounded, because a phone that
    /// misbehaves is fixed by timing out rather than by buffering forever.
    /// </summary>
    private static async Task<bool> PhoneAcceptedAsync(NetworkStream ns, CancellationToken ct)
    {
        var buf = new MemoryStream(64);
        byte[] one = new byte[1];

        while (buf.Length < 16 * 1024)
        {
            int n = await ns.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
            if (n <= 0) return false;
            buf.WriteByte(one[0]);

            var a = buf.ToArray();
            if (a.Length >= 4 && a[^1] == (byte)'\n' && a[^2] == (byte)'\r' && a[^3] == (byte)'\n' && a[^4] == (byte)'\r')
            {
                // "HTTP/1.x 2xx" or a plain "HTTP/1.1 200" on the first line is acceptance;
                // everything else - 502, 403, whatever a tunnel full of verdicts says twice -
                // marks the phone path down and the connection goes direct.
                string text = Encoding.ASCII.GetString(a, 0, a.Length);
                int firstEnd = text.IndexOf('\n');
                string firstLine = firstEnd > 0 ? text.Substring(0, firstEnd).Trim() : text;
                if (firstLine.StartsWith("HTTP/", StringComparison.Ordinal))
                    return firstLine.Contains(" 2", StringComparison.Ordinal) &&
                           !firstLine.Contains(" 5", StringComparison.Ordinal);
                return false;
            }
            if (a.Length >= 2 && a[^1] == (byte)'\n' && a[^2] == (byte)'\n')
            {
                string text = Encoding.ASCII.GetString(buf.ToArray());
                int firstEnd = text.IndexOf(('\n'));
                string firstLine = firstEnd > 0 ? text.Substring(0, firstEnd).Trim() : text;
                return firstLine.StartsWith("HTTP/", StringComparison.Ordinal) &&
                       firstLine.Contains(" 2", StringComparison.Ordinal);
            }
        }
        return false;
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream s, byte[] buffer, int count,
                                                     CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await s.ReadAsync(buffer, got, count - got, ct).ConfigureAwait(false);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    // ------------------------------------------------------------------ relaying

    /// <summary>Increments the right counter for a path and a direction. Cheap enough to call
    /// this in the pump's inner loop: an Interlocked.Add on a hot path is nanoseconds.</summary>
    private void Count(bool viaPeer, bool up, int bytes)
    {
        if (viaPeer)
        {
            if (up) Interlocked.Add(ref _peerUp, bytes);
            else Interlocked.Add(ref _peerDown, bytes);
        }
        else
        {
            if (up) Interlocked.Add(ref _directUp, bytes);
            else Interlocked.Add(ref _directDown, bytes);
        }
    }

    /// <summary>
    /// Pipes a settled tunnel both ways until either side hangs up, then closes both. The
    /// first direction to end is the real end; the other direction's read is cancelled by
    /// closing the streams and both pumps are allowed their exit.
    /// </summary>
    private async Task RelayBothAsync(NetworkStream client, NetworkStream remote, bool viaPeer,
                                      CancellationToken ct)
    {
        Task up = PumpAsync(client, remote, viaPeer, up: true, ct);
        Task down = PumpAsync(remote, client, viaPeer, up: false, ct);

        await Task.WhenAny(up, down).ConfigureAwait(false);

        try { client.Close(); } catch { }
        try { remote.Close(); } catch { }

        try { await Task.WhenAll(up, down).ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Relays one response: upstream to downstream until the upstream ends, counted as
    /// downstream bytes of the path it rode. Request bytes were counted as they were written.
    /// </summary>
    private async Task RelayResponseAsync(NetworkStream source, NetworkStream sink, bool viaPeer,
                                          CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (n <= 0) break;
                try { await sink.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false); }
                catch { break; }
                Count(viaPeer, up: false, n);
            }
        }
        catch { /* end of stream by any means is the end */ }
        finally
        {
            try { sink.Flush(); } catch { }
        }
    }

    /// <summary>One direction of a tunnel, reading until the source closes and counting bytes
    /// to the path this tunnel rode.</summary>
    private async Task PumpAsync(NetworkStream source, NetworkStream sink, bool viaPeer, bool up,
                                 CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (n <= 0) return;

                await sink.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                Count(viaPeer, up, n);
            }
        }
        catch { /* either side hanging up, on any error, is how a tunnel ends */ }
        finally
        {
            try { sink.Flush(); } catch { }
        }
    }
}
