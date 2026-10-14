using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Core.Logging;

namespace NetPilot.Services;

/// <summary>
/// One network the download may go out over, and the local address that identifies it.
///
/// The operating system sends all of a machine's traffic down one default connection and leaves
/// the rest idle. Nothing in Windows will split a single download across two adapters for you,
/// and no setting exists to ask it to. The only way is to open the sockets yourself and bind
/// each one to a specific local address before connecting - the routing table then sends that
/// socket out through whichever adapter owns that address, which is the whole mechanism.
///
/// It needs no virtual adapter, no kernel extension and no administrator rights, because it is
/// ordinary sockets from an ordinary process.
/// </summary>
public sealed class BondNetwork
{
    public const int DefaultStreams = 8;

    public string Name { get; }
    public IPAddress LocalAddress { get; }
    public int Streams { get; }
    public long MaxBytesPerSecond { get; }

    /// <exception cref="ArgumentOutOfRangeException">A network with no streams has nothing to do.</exception>
    public BondNetwork(string name, IPAddress localAddress,
                       int streams = DefaultStreams, long maxBytesPerSecond = 0)
    {
        if (streams < 1)
            throw new ArgumentOutOfRangeException(nameof(streams),
                $"{name}: a network needs at least one stream to take any work");
        if (localAddress == null)
            throw new ArgumentNullException(nameof(localAddress), $"{name}: no address to bind to");

        Name = name;
        LocalAddress = localAddress;
        Streams = streams;
        MaxBytesPerSecond = maxBytesPerSecond;
    }

    public override string ToString() => $"{Name} ({LocalAddress}) x{Streams}";
}

/// <summary>What the server said about the file before any of it was downloaded.</summary>
public sealed class RemoteFile
{
    public long Size { get; init; }
    public bool SupportsRanges { get; init; }

    /// <summary>Server version markers. A resume that ignores these joins two different files
    /// into one corrupt one, so they are recorded on the first request and re-checked before
    /// any byte is resumed.</summary>
    public string ETag { get; init; }
    public string LastModified { get; init; }

    /// <summary>The name the server suggested, from Content-Disposition, for a filename.</summary>
    public string SuggestedName { get; init; }

    public bool CanBond => SupportsRanges && Size > 0;
}

/// <summary>Live totals for a running download, raised as they change.</summary>
public sealed class BondProgress
{
    public long BytesDone { get; init; }
    public long TotalBytes { get; init; }
    public double Fraction => TotalBytes <= 0 ? 0 : (double)BytesDone / TotalBytes;
    public IReadOnlyDictionary<string, long> BytesPerNetwork { get; init; }
}

/// <summary>
/// Downloads one file over several networks at the same time.
///
/// Each stream binds to one network's own local address, asks for one block with a Range
/// request, writes the bytes at that block's offset and takes the next block from the shared
/// queue. A network that finishes its blocks sooner takes more of them, so the split follows
/// what each network can actually do without anyone having to measure and apportion it up
/// front - and if a network slows down or drops, the others keep draining the queue.
///
/// Two things this deliberately does not do:
///   It does not accelerate other applications. Only files this class is asked to fetch are
///   bonded; nothing else on the machine changes where its traffic goes.
///   It does not bond two networks that share one uplink. Wi-Fi and Ethernet plugged into the
///   same router are one connection wearing two hats, and adding them together gains nothing.
/// </summary>
public sealed class BondedHttp
{
    /// <summary>Range header, which is the whole mechanism in one line.</summary>
    private static string Range(long from, long to) => $"bytes={from}-{to}";

    internal static HttpClient HandlerFor(IPAddress bindTo, long maxBytesPerSecond)
    {
        var handler = new SocketsHttpHandler
        {
            // Bind before connecting: this is what makes the OS route the socket out of the
            // right adapter. For HTTPS the TLS handshake happens after this returns, so it is
            // covered too - the socket is already on the network by then.
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    socket.Bind(new IPEndPoint(bindTo, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 256,
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Asks the server about the file by requesting a single byte of it.
    ///
    /// A 206 answer proves the server honours Range requests and carries the size in its
    /// Content-Range. A 200 means it ignored the range and sent the whole file instead, which
    /// is an ordinary single-stream download - and, importantly, not something to fight. The
    /// version markers come back here too, because they are needed before a resume decides
    /// whether resuming is safe at all.
    /// </summary>
    public static async Task<RemoteFile> ProbeAsync(string url, IPAddress bindTo, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"'{url}' is not an http or https address", nameof(url));

        using var client = HandlerFor(bindTo, 0);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                         .ConfigureAwait(false);

        long size = 0;
        bool ranges = false;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ranges = true;
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange?.HasLength == true) size = contentRange.Length ?? 0;
            // Drain the single byte so the connection can be reused rather than torn down.
            await response.Content.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
        }
        else
        {
            size = response.Content.Headers.ContentLength ?? 0;
        }

        string name = null;
        var disposition = response.Content.Headers.ContentDisposition;
        if (disposition != null) name = disposition.FileNameStar ?? disposition.FileName;
        name = name?.Trim('"', ' ');

        return new RemoteFile
        {
            Size = size,
            SupportsRanges = ranges,
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified?.ToString("R"),
            SuggestedName = string.IsNullOrWhiteSpace(name) ? null : name,
        };
    }
}

/// <summary>
/// One file, fetched over every network at once.
///
/// The streams share one queue of blocks rather than each taking a fixed slice of the file.
/// That single decision is what makes bonding work on networks of unequal speed: a fast network
/// finishes its block sooner, so it is free sooner, so it takes the next block, so it ends up
/// carrying more of the file - with nobody having to measure the networks and divide the work
/// in advance. Divide the file evenly instead and the download is only as fast as the slowest
/// network, which defeats the entire point.
/// </summary>
public sealed class BondedDownload
{
    /// <summary>Consecutive server refusals before a stream gives its block back and stands down.</summary>
    private const int BackoffThreshold = 3;

    /// <summary>
    /// How many blocks one stream will fail before it gives up on that connection.
    ///
    /// Bounded so a stream cannot spin forever against a server that is refusing everything, and
    /// small enough that a genuinely dead connection is handed to the others quickly. It is a
    /// retry count rather than a timeout on purpose: a slow block is not a failed block, and a
    /// download over a mobile hotspot must not be killed for taking the time it needs.
    /// </summary>
    private const int MaxStreamRetries = 12;

    private readonly string _url;
    private readonly string _destination;
    private readonly IReadOnlyList<BondNetwork> _networks;
    private readonly long _blockSize;
    private readonly long _streamsPerNetwork;

    /// <summary>Raised from the streams, so it arrives on a thread-pool thread. Dispatch to the
    /// UI before touching anything visual.</summary>
    public event Action<BondProgress> Progress;

    /// <summary>Set when a network has refused enough requests to be left alone.</summary>
    public event Action<string, string> NetworkDropped;   // network name, why

    public BondedDownload(string url, string destination, IReadOnlyList<BondNetwork> networks,
                          long blockSize = BlockPlan.DefaultBlockSize)
    {
        if (networks == null || networks.Count == 0)
            throw new ArgumentException("a bonded download needs at least one network", nameof(networks));

        _url = url;
        _destination = destination;
        _networks = networks.ToArray();
        _blockSize = blockSize;
        _streamsPerNetwork = _networks.Sum(n => (long)n.Streams);
    }

    /// <summary>
    /// Fetches the file and returns what the server said about it.
    ///
    /// Returns rather than throws when the server does not support ranges: the file is still
    /// downloaded, just by one stream over the default route, which is the only honest answer
    /// available for a server that will not serve slices.
    /// </summary>
    public async Task<RemoteFile> RunAsync(CancellationToken ct = default)
    {
        var remote = await BondedHttp.ProbeAsync(_url, _networks[0].LocalAddress, ct)
                                     .ConfigureAwait(false);

        if (!remote.CanBond)
        {
            await SingleStreamAsync(remote, ct).ConfigureAwait(false);
            return remote;
        }

        var plan = BlockPlan.For(remote.Size, _blockSize);
        using var staging = StagingFile.Open(_destination, remote.Size, remote.ETag, remote.LastModified,
                                             out bool resume);
        var queue = new BlockQueue(resume ? plan.MissingFrom(b => staging.IsWritten(b.Index)) : plan.Blocks);

        var perNetwork = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streams = new List<Task>();
        foreach (var network in _networks)
            for (int i = 0; i < network.Streams; i++)
                streams.Add(Task.Run(() => StreamAsync(remote, network, queue, staging, perNetwork,
                                                       linked.Token), linked.Token));

        try
        {
            await Task.WhenAll(streams).ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
        }

        if (!queue.IsComplete && !ct.IsCancellationRequested)
            throw new IOException($"the download stopped with {queue.RemainingCount} of {queue.BlockCount} " +
                                  "blocks still missing and no connection able to fetch them");

        if (ct.IsCancellationRequested) return remote;   // staging file stays, for a resume

        staging.Finish(_destination);
        Report(perNetwork, plan.TotalBytes, plan.TotalBytes);
        return remote;
    }

    private void Report(System.Collections.Concurrent.ConcurrentDictionary<string, long> perNetwork,
                        long done, long total)
    {
        Progress?.Invoke(new BondProgress
        {
            BytesDone = done,
            TotalBytes = total,
            BytesPerNetwork = new Dictionary<string, long>(perNetwork),
        });
    }

    /// <summary>
    /// One stream: claim a block, fetch it over its own network, write it at its offset, repeat.
    ///
    /// When the queue runs dry but something is still in flight, the stream races the largest
    /// in-flight block instead of exiting. That is the difference between a download that ends
    /// at the speed of whichever network happened to draw the last block, and one that ends when
    /// the fastest of the two finishes it.
    /// </summary>
    private async Task StreamAsync(RemoteFile remote, BondNetwork network, BlockQueue queue,
                                   StagingFile staging,
                                   System.Collections.Concurrent.ConcurrentDictionary<string, long> perNetwork,
                                   CancellationToken ct)
    {
        using var client = BondedHttp.HandlerFor(network.LocalAddress, network.MaxBytesPerSecond);
        using var throttle = new Throttle(network.MaxBytesPerSecond);
        var refused = 0;
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            Block block;
            bool racing;
            if (queue.TryClaim(out block)) racing = false;
            else if (queue.TryClaimSlowestInFlight(out block)) racing = true;
            else break;                       // nothing waiting and nothing in flight: done

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(block.Offset, block.LastByte);

                using var response = await client.SendAsync(request,
                                                            HttpCompletionOption.ResponseHeadersRead, ct)
                                                 .ConfigureAwait(false);

                // 403, 429 and 503 are a server saying "too many of you". Carrying on makes it
                // worse for everyone, so the block goes back and this stream stands down.
                if (response.StatusCode is HttpStatusCode.Forbidden
                                          or HttpStatusCode.TooManyRequests
                                          or HttpStatusCode.ServiceUnavailable)
                {
                    queue.Release(block);
                    refused++;
                    if (refused >= BackoffThreshold) { NetworkDropped?.Invoke(network.Name, response.StatusCode.ToString()); break; }
                    await Task.Delay(TimeSpan.FromSeconds(refused), ct).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    // The server ignored the range. Refusing the block is right; retrying the
                    // same request is not going to produce a different answer.
                    queue.Release(block);
                    throw new IOException($"{network.Name}: expected 206 for {block}, got " +
                                          $"{(int)response.StatusCode}");
                }

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[block.Length];
                await body.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);

                await staging.WriteAsync(block.Index, block.Offset, buffer, ct).ConfigureAwait(false);
                refused = 0;

                // Only the stream that actually paid the block out counts it. When two streams
                // race the same block the loser's bytes land in the file too - they are identical,
                // so writing them twice is harmless - but counting them twice would make the
                // per-network totals exceed the file, and a total that is larger than what was
                // downloaded is worse than no total at all.
                if (queue.Complete(block))
                    perNetwork.AddOrUpdate(network.Name, block.Length, (_, have) => have + block.Length);

                Report(perNetwork, queue.BytesDone, remote.Size);
                await throttle.WaitAsync(block.Length, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { queue.Release(block); throw; }
            catch when (racing)
            {
                // A raced block is optional by definition: the other stream may still finish it,
                // and if nothing does the outer loop will find it back in the queue.
                queue.Release(block);
            }
            catch (Exception ex)
            {
                // A block failed. It goes back into the queue and this stream tries again after a
                // pause, because the usual cause is a connection that dropped and would come back.
                // A real network does this constantly - a laptop lid, a phone hotspot moving, a
                // wifi roam - and treating every one as fatal ends the download at whatever
                // percentage it had reached, which is a failure nobody asked for and nothing on
                // screen explains.
                queue.Release(block);

                if (++failures >= MaxStreamRetries)
                {
                    Log.Warn("bond", $"{network.Name}: giving up after {failures} failed blocks", ex);
                    break;
                }

                // Capped, because the backoff multiplies by the failure count and an uncapped one
                // turns a bad minute into a twenty-second wait per attempt.
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200 * failures, 1000)), ct)
                           .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A server that will not serve ranges gets one stream over the default route, because that
    /// is what it is willing to do. Bonding it would mean requesting the whole file several
    /// times and keeping one copy.
    /// </summary>
    private async Task SingleStreamAsync(RemoteFile remote, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct)
                                         .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var staging = StagingFile.Open(_destination, remote.Size, remote.ETag, remote.LastModified,
                                             out _);
        using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        long offset = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await RandomAccess.WriteAsync(staging.Handle, buffer.AsMemory(0, read), offset, ct)
                               .ConfigureAwait(false);
            offset += read;
            Progress?.Invoke(new BondProgress { BytesDone = offset, TotalBytes = remote.Size });
        }
        staging.Finish(_destination);
    }
}

/// <summary>
/// Holds a network to its speed limit.
///
/// A token bucket rather than a sleep-per-chunk, because the chunk sizes vary: charging the
/// bytes actually sent against the budget is what makes a limit mean the same thing whatever
/// the block size happens to be.
/// </summary>
internal sealed class Throttle : IDisposable
{
    private readonly long _bytesPerSecond;
    private long _tokens;
    private long _lastMs = Environment.TickCount64;

    public Throttle(long bytesPerSecond) => _bytesPerSecond = bytesPerSecond;

    public async ValueTask WaitAsync(long bytes, CancellationToken ct)
    {
        if (_bytesPerSecond <= 0) return;

        long now = Environment.TickCount64;
        long elapsed = Math.Max(0, now - _lastMs);
        _lastMs = now;
        _tokens = Math.Min(_bytesPerSecond * 2, _tokens + _bytesPerSecond * elapsed / 1000);
        _tokens -= bytes;

        if (_tokens >= 0) return;

        long waitMs = -_tokens * 1000 / _bytesPerSecond;
        if (waitMs > 0) await Task.Delay((int)Math.Min(waitMs, 1000), ct).ConfigureAwait(false);
        _tokens = 0;
    }

    public void Dispose() { }
}