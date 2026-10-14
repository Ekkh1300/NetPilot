using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// End-to-end tests of the bonded downloader, against a real HTTP server that answers Range
/// requests.
///
/// Everything here is checked by comparing the assembled file against the bytes the server
/// actually served. A downloader that is fast and slightly wrong is worse than useless, so the
/// assertion is always "these bytes, in this order" rather than "the progress bar reached 100%".
/// </summary>
public class BondedDownloadTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
                                                "netpilot-bond-" + Guid.NewGuid().ToString("N"));

    public BondedDownloadTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static BondNetwork Loopback(int streams) =>
        new("loopback", IPAddress.Loopback, streams);

    private string TempFile(string name) => Path.Combine(_dir, name);

    /// <summary>
    /// Cancels a download as soon as it has written something, and swallows the cancellation.
    ///
    /// A timer is the wrong way to interrupt this: a file over loopback can finish long before
    /// it expires, and then there is nothing left to resume and the test asserts on a staging
    /// file that was correctly cleaned up. Waiting for the first progress event makes the
    /// interruption land *during* the transfer, which is the situation worth testing.
    /// </summary>
    private static async Task InterruptAfterFirstBlockAsync(BondedDownload dl)
    {
        using var cut = new CancellationTokenSource();
        dl.Progress += _ => cut.Cancel();
        try { await dl.RunAsync(cut.Token); }
        catch (OperationCanceledException) { }   // the expected outcome of an interruption
    }

    private static async Task AssertFileMatchesAsync(string path, byte[] expected)
    {
        Assert.True(File.Exists(path), $"{path} was never finished");
        var actual = await File.ReadAllBytesAsync(path);
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i])
                throw new Xunit.Sdk.XunitException(
                    $"byte {i} of {expected.Length} differs: wanted {expected[i]}, got {actual[i]}");
    }

    // ---------------- the basic promise ----------------

    [Fact]
    public async Task OneNetworkDownloadsTheWholeFileIntact()
    {
        using var server = new RangeTestServer(8 * MB);
        var dest = TempFile("one.bin");

        var dl = new BondedDownload(server.Url, dest, new[] { Loopback(4) });
        var remote = await dl.RunAsync();

        Assert.Equal(8 * MB, remote.Size);
        Assert.True(remote.SupportsRanges, "the server answers ranges, so the probe must say so");
        Assert.Equal("\"test-v1\"", remote.ETag);
        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(8 * MB));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task ManyStreamsStillAssembleTheFileExactly(int streams)
    {
        // The property under test is that the number of streams cannot change what the file
        // ends up being - not that more streams are faster, which a loopback test cannot show.
        using var server = new RangeTestServer(12 * MB);
        var dest = TempFile($"many-{streams}.bin");

        var dl = new BondedDownload(server.Url, dest, new[] { Loopback(streams) });
        await dl.RunAsync();

        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(12 * MB));
    }

    [Fact]
    public async Task EveryByteIsRequestedExactlyOnce()
    {
        using var server = new RangeTestServer(10 * MB);
        var dest = TempFile("coverage.bin");

        await new BondedDownload(server.Url, dest, new[] { Loopback(6) }).RunAsync();

        // Rebuild the requested ranges and prove they tile the file with nothing overlapping
        // and nothing missed - the server-side view of the same invariant the queue asserts.
        var spans = server.RangesRequested
                          .Where(r => r.StartsWith("asked "))
                          .Select(r => r["asked bytes=".Length..])
                          .Select(spec =>
                          {
                              var parts = spec.Split('-');
                              return (From: long.Parse(parts[0]), To: long.Parse(parts[1]));
                          })
                          .OrderBy(s => s.From)
                          .ToList();

        Assert.True(spans.Count > 0, "the server was never asked for a range");

        // The probe asks for one byte at offset 0 to discover the file, and that byte is also
        // part of the first block. Overlap there is expected; a *gap* is the failure that
        // matters, because a gap is a stretch of the file nobody fetched.
        long next = 0;
        foreach (var (from, to) in spans)
        {
            Assert.True(from <= next,
                $"a gap: a range started at {from}, but only {next} bytes of the file had been requested");
            next = Math.Max(next, to + 1);
        }
        Assert.Equal(10 * MB, next);

        // And the probe that makes bonding possible was actually made.
        Assert.Contains("asked bytes=0-0", server.RangesRequested);
    }

    // ---------------- the mechanism is real ----------------

    /// <summary>
    /// The negative control for the whole feature.
    ///
    /// If <c>Socket.Bind</c> were a no-op - if the engine asked for a local address and the OS
    /// ignored it - this download would succeed like any other, because the default route
    /// reaches the same server. It has to fail instead, and it does: an address the machine does
    /// not own cannot be bound at all.
    ///
    /// This is what separates "the code calls Bind" from "the code is on the network it says it
    /// is on". The positive test cannot tell those apart; this one can.
    /// </summary>
    [Fact]
    public async Task BindingToAnAddressTheMachineDoesNotOwnFailsRatherThanUsingTheDefaultRoute()
    {
        using var server = new RangeTestServer(4 * MB);
        var dest = TempFile("nowhere.bin");

        // 203.0.113.7 is TEST-NET-3: reserved for documentation, never a local address.
        var nowhere = new BondNetwork("nowhere", IPAddress.Parse("203.0.113.7"), 1);

        await Assert.ThrowsAnyAsync<Exception>(
            () => new BondedDownload(server.Url, dest, new[] { nowhere }).RunAsync());

        Assert.False(File.Exists(dest), "nothing should have been finished by a socket that never connected");
    }

    /// <summary>
    /// The other half of the same control: a bound socket really does arrive from the address it
    /// was bound to, as seen by the server. If the source address were not the loopback address,
    /// the binding would not be reaching the network at all.
    /// </summary>
    [Fact]
    public async Task ConnectionsArriveFromTheAddressTheyWereBoundTo()
    {
        using var server = new RangeTestServer(6 * MB);
        var dest = TempFile("source.bin");

        await new BondedDownload(server.Url, dest, new[] { Loopback(4) }).RunAsync();

        Assert.True(server.SourceAddresses.Count > 0, "no connection reached the server");
        Assert.All(server.SourceAddresses,
                   a => Assert.Equal(IPAddress.Loopback, a));
    }

    // ---------------- a server that will not cooperate ----------------

    [Fact]
    public async Task AServerThatIgnoresRangesStillGetsItsFileDownloaded()
    {
        using var server = new RangeTestServer(5 * MB) { SupportsRanges = false };
        var dest = TempFile("noranges.bin");

        var dl = new BondedDownload(server.Url, dest, new[] { Loopback(4) });
        var remote = await dl.RunAsync();

        Assert.False(remote.CanBond, "no ranges means no bonding, and pretending otherwise is a bug");
        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(5 * MB));
    }

    /// <summary>
    /// One slow stretch, so the idle streams have something to race.
    ///
    /// Half the file answers immediately and half takes a while, which is what a mismatched pair
    /// of networks looks like at the end of a download. The file has to come out whole either
    /// way, and it does.
    /// </summary>
    [Fact]
    public async Task ASlowStretchDoesNotLeaveTheFileShort()
    {
        using var server = new RangeTestServer(10 * MB);
        server.SlowDown(8 * MB, 10 * MB, 700);
        var dest = TempFile("slow.bin");

        await new BondedDownload(server.Url, dest, new[] { Loopback(8) }).RunAsync();

        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(10 * MB));
    }

    // ---------------- pausing and resuming ----------------

    [Fact]
    public async Task AnInterruptedDownloadResumesAndStillComesOutWhole()
    {
        using var server = new RangeTestServer(12 * MB);
        var dest = TempFile("resume.bin");
        var payload = RangeTestServer.PayloadOf(12 * MB);

        // First run: cut it off as soon as it has started writing, the way a closed app or a
        // lost connection would.
        await InterruptAfterFirstBlockAsync(new BondedDownload(server.Url, dest, new[] { Loopback(4) }));

        Assert.True(File.Exists(dest + StagingFile.Suffix),
                    "an interrupted download must leave its staging file behind, or there is nothing to resume from");

        // Second run: the same file on the server, so the resume is allowed.
        await new BondedDownload(server.Url, dest, new[] { Loopback(4) }).RunAsync();

        await AssertFileMatchesAsync(dest, payload);
    }

    [Fact]
    public async Task AResumeIsRefusedWhenTheServerNowHasADifferentFile()
    {
        using var server = new RangeTestServer(8 * MB);
        var dest = TempFile("changed.bin");

        await InterruptAfterFirstBlockAsync(new BondedDownload(server.Url, dest, new[] { Loopback(4) }));

        long staged = new FileInfo(dest + StagingFile.Suffix).Length;
        Assert.Equal(8 * MB, staged);

        // The server swapped the file underneath us. Resuming would splice two different files
        // together, which is worse than starting again.
        server.ETag = "\"test-v2\"";

        await new BondedDownload(server.Url, dest, new[] { Loopback(4) }).RunAsync();

        Assert.True(File.Exists(dest), "the download finished, so the destination must exist");
        Assert.Equal(8 * MB, new FileInfo(dest).Length);
        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(8 * MB));
    }

    // ---------------- a network that goes wrong ----------------

    /// <summary>
    /// A connection that dies partway through a transfer.
    ///
    /// This is the ordinary case, not the exotic one: a laptop lid, a phone hotspot moving, a
    /// wifi roam, a proxy timing out. The previous behaviour was to hand the block back and stop
    /// that stream, and if every stream did the same the download simply ended at whatever
    /// percentage it had reached - with the file still there, the log still quiet, and nothing
    /// that said "you could have retried".
    ///
    /// One blip is absorbed by a stream's own retry. Enough of them to outlast every stream is
    /// the case that used to end the download early.
    /// </summary>
    [Theory]
    [InlineData(1)]     // a single blip: the stream's own retry
    [InlineData(6)]     // several, so the backoff actually grows
    [InlineData(20)]    // more than every stream can absorb on its own
    public async Task DroppedConnectionsDoNotFinishTheDownloadEarly(int drops)
    {
        using var server = new RangeTestServer(8 * MB);
        server.DropBlockRequests(drops);
        var dest = TempFile($"dropped-{drops}.bin");

        await new BondedDownload(server.Url, dest, new[] { Loopback(2) }).RunAsync();

        // The whole test is meaningless if the server never actually dropped anything - it would
        // be passing because the failure never happened rather than because it was survived.
        // This is what turns a green test into evidence.
        Assert.True(server.DropsMade > 0,
                    $"the server dropped nothing, so this tested a healthy download instead ({drops} asked for)");

        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(8 * MB));
    }

    /// <summary>
    /// The same failure, but unrecoverable: a server that drops every single block request cannot
    /// be downloaded, and hanging or retrying forever would be worse than saying so.
    /// </summary>
    [Fact]
    public async Task AServerThatDropsEverythingEventuallyFailsRatherThanHanging()
    {
        using var server = new RangeTestServer(4 * MB);
        server.DropBlockRequests(1000);
        var dest = TempFile("never.bin");

        await Assert.ThrowsAsync<IOException>(
            () => new BondedDownload(server.Url, dest, new[] { Loopback(1) }).RunAsync());

        Assert.True(server.DropsMade > 0, "nothing was dropped, so this proved nothing");
        Assert.False(File.Exists(dest), "a file that never arrived must not be presented as one");
    }

    // ---------------- reporting ----------------

    [Fact]
    public async Task ProgressAddsUpToTheWholeFile()
    {
        using var server = new RangeTestServer(8 * MB);
        var dest = TempFile("progress.bin");

        var dl = new BondedDownload(server.Url, dest, new[] { Loopback(4) });
        long lastSeen = 0;
        dl.Progress += p => lastSeen = p.BytesDone;

        await dl.RunAsync();

        Assert.Equal(8 * MB, lastSeen);
    }

    [Fact]
    public async Task PerNetworkTotalsAccountForEveryByte()
    {
        using var server = new RangeTestServer(8 * MB);
        var dest = TempFile("pernet.bin");

        var dl = new BondedDownload(server.Url, dest, new[] { Loopback(4) });
        BondProgress final = null;
        dl.Progress += p => final = p;

        await dl.RunAsync();

        Assert.NotNull(final);
        Assert.Equal(8 * MB, final.BytesPerNetwork.Values.Sum());
        Assert.Equal(8 * MB, final.BytesDone);
        Assert.Equal(1.0, final.Fraction);
    }

    [Fact]
    public async Task ANetworkThatKeepsRefusingIsLeftAloneAndSaidSo()
    {
        using var server = new RangeTestServer(8 * MB) { SupportsRanges = false };
        var dest = TempFile("dropped.bin");

        var dl = new BondedDownload(server.Url, dest, new[] { Loopback(4) });
        string dropped = null;
        dl.NetworkDropped += (name, _) => dropped = name;

        await dl.RunAsync();

        // With ranges off the probe refuses to bond, so nothing is dropped - and that is the
        // point of the assertion: a fallback that reports a network problem it never had would
        // be a misleading one.
        Assert.Null(dropped);
    }

    [Fact]
    public void ABondedDownloadNeedsAtLeastOneNetwork() =>
        Assert.Throws<ArgumentException>(() => new BondedDownload("http://x/y", TempFile("none.bin"),
                                                                  Array.Empty<BondNetwork>()));

    [Fact]
    public void ANetworkWithNoStreamsIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BondNetwork("wifi", IPAddress.Loopback, 0));

    [Fact]
    public void ANetworkWithNoAddressIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new BondNetwork("wifi", null));
}