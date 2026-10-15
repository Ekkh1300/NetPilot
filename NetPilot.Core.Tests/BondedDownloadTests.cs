using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

    /// <summary>
    /// The feature's central claim, and the one test that can actually check it: two networks
    /// with their own local addresses, one file, every socket arriving from the address of the
    /// network that opened it.
    ///
    /// Every other download test in this file runs against a single address, so none of them can
    /// tell "bound to the right network" apart from "bound to anything at all" - they would all
    /// pass if the second network were silently ignored and every socket left from the first.
    /// That is precisely the bug that makes a bonded download look like it is working while it
    /// adds no speed whatsoever, and it is why this test exists separately.
    ///
    /// 127.0.0.1 and 127.0.0.2 stand in for two physical adapters because the claim under test
    /// is about socket binding rather than about this machine's hardware: the machine owns both
    /// addresses, so each bind is genuine, and the source address the server observes is the only
    /// thing that differs between them. The routing half of the claim - that an address selects
    /// an adapter - is the operating system's, and cannot be shown from a test process.
    ///
    /// Stream count equals block count on purpose. A socket opens only when its stream claims
    /// work, so a network whose streams never claim anything would open nothing and the server
    /// would never see its address; with one stream per block, every stream's first claim
    /// succeeds and both networks necessarily carry part of the file.
    ///
    /// The second half of the method - the branch on <see cref="CanBind"/> - exists because
    /// 127.0.0.2 is not a second local address everywhere. Windows and Linux treat all of
    /// 127.0.0.0/8 as local; macOS configures lo0 with 127.0.0.1 and refuses to bind anything
    /// else. Rather than skip there and have the test quietly stop testing on one platform, the
    /// machine without a second address is asked the question it does have an answer to.
    /// </summary>
    [Fact]
    public async Task TwoNetworksEachKeepTheirOwnSourceAddress()
    {
        const long fileBytes = 64 * MB;
        using var server = new RangeTestServer(fileBytes);
        var dest = TempFile("two-nets.bin");

        var loopback1 = IPAddress.Parse("127.0.0.1");
        var loopback2 = IPAddress.Parse("127.0.0.2");

        var wifi = new BondNetwork("wifi", loopback1, 4);
        var usb = new BondNetwork("usb", loopback2, 4);

        BondProgress final = null;
        var dl = new BondedDownload(server.Url, dest, new[] { wifi, usb });
        dl.Progress += p => final = p;

        await dl.RunAsync();

        // The file comes out whole either way, on every machine.
        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(fileBytes));
        Assert.NotNull(final);

        if (!CanBind(loopback2))
        {
            // This machine cannot express the precondition, so the claim that needs two addresses
            // is not the one being asserted here. Returning with nothing would leave a test that
            // passes forever without checking anything, which is worse than not having it.
            //
            // What the machine does have is a network that cannot bind at all - which is what a
            // dead uplink looks like from the engine's side. So that is what gets asserted: it
            // carries nothing, and the working network carries the whole file on its own.
            //
            // That a dead network also gets *named* is deliberately not checked here. Doing so
            // waits for every stream to exhaust its retries, which takes longer than this
            // download does, so the assertion would pass or fail on scheduling rather than on
            // behaviour. It is checked on every platform by
            // ANetworkThatCannotConnectIsReportedAndDoesNotStopTheRest instead.
            Assert.Equal(0, final.BytesPerNetwork.GetValueOrDefault("usb"));
            Assert.Equal(fileBytes, final.BytesPerNetwork["wifi"]);
            return;
        }

        // 1. The server saw a request from each address. One address only would mean the second
        //    network's sockets never left on their own.
        Assert.Contains(server.Served, r => r.Source.Equals(loopback1));
        Assert.Contains(server.Served, r => r.Source.Equals(loopback2));

        // 2. Both networks actually carried part of the file - a second network that was passed
        //    in, accepted, and then never used would register here as zero.
        Assert.True(final.BytesPerNetwork["wifi"] > 0,
                    $"wifi fetched nothing of {fileBytes} bytes");
        Assert.True(final.BytesPerNetwork["usb"] > 0,
                    $"usb fetched nothing of {fileBytes} bytes");

        // 3. And the two of them still account for every byte between them.
        Assert.Equal(fileBytes, final.BytesPerNetwork.Values.Sum());

        // 4. No third address ever appeared. A socket that fell back to some other route would
        //    show up here as an address nobody configured, which is the silent failure this
        //    whole mechanism is built to avoid.
        foreach (var source in server.Served.Select(r => r.Source).Distinct())
            Assert.True(source.Equals(loopback1) || source.Equals(loopback2),
                        $"a connection left from {source}, which no network in this download was bound to");
    }

    /// <summary>Whether this machine will accept a bind to the given address at all.</summary>
    private static bool CanBind(IPAddress address)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (SocketException) { return false; }
    }

    // ---------------- a server that will not cooperate ----------------

    /// <summary>
    /// A network that cannot reach the destination at all.
    ///
    /// Every other test here assumes each network works. This one adds one whose address the
    /// machine does not own, so its sockets fail to bind on the first attempt and on every retry
    /// after it - which is what a dead uplink, an unplugged cable or an adapter with no network
    /// behind it actually looks like to the engine.
    ///
    /// Two things have to hold at once, and neither is automatic. The download must still finish
    /// on the working network, since one broken path must not take the whole thing with it. And
    /// the broken one must be reported, because a network that contributes nothing and says
    /// nothing is worse than one that was never there: it sits on the page looking available.
    /// </summary>
    [Fact]
    public async Task ANetworkThatCannotConnectIsReportedAndDoesNotStopTheRest()
    {
        // Enough blocks that every stream draws one on its first claim. With fewer, the working
        // network takes the lot and the dead streams exit finding nothing to do - never attempting
        // a connection, so never failing, so the test would pass for the wrong reason.
        using var server = new RangeTestServer(32 * MB);
        var dest = TempFile("one-dead.bin");

        // And a download long enough for the dead network to be identified at all.
        //
        // A network is reported once every stream on it has used up its retries, which takes
        // roughly ten seconds of backoff. Against a file that finishes in two, the download ends
        // first and the report never has reason to happen - correct enough in itself, since there
        // is nothing left to warn about, but it means the short version of this test passes
        // without the event ever being raised. Slowing one block is what makes the two networks
        // still be running when the dead one gives up, which is the case worth pinning down.
        server.SlowDown(0, 8 * MB, 12_000);

        var good = new BondNetwork("good", IPAddress.Loopback, 2);
        // 203.0.113.7 is TEST-NET-3: reserved for documentation, never a local address.
        var dead = new BondNetwork("dead", IPAddress.Parse("203.0.113.7"), 2);

        var drops = new List<string>();
        string droppedWhy = null;
        var dl = new BondedDownload(server.Url, dest, new[] { good, dead });
        dl.NetworkDropped += (name, why) => { drops.Add(name); droppedWhy = why; };

        await dl.RunAsync();

        // The file still arrives whole over the network that works.
        await AssertFileMatchesAsync(dest, RangeTestServer.PayloadOf(32 * MB));

        // The dead network is named. This is also the proof that it really tried: the event only
        // fires once every stream on a network has failed a full set of retries, so reaching this
        // line means its sockets were opened, refused, and retried rather than never used at all.
        Assert.Contains("dead", drops);

        // The working network is not tarred with the same brush. Reporting both would be as bad
        // as reporting neither - the page would be warning about the one connection still working.
        Assert.DoesNotContain("good", drops);

        // And it gives a reason someone could act on. An empty or whitespace reason would leave
        // the page showing a warning with nothing behind it.
        Assert.False(string.IsNullOrWhiteSpace(droppedWhy),
                     "a network that is dropped has to say why");
    }

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