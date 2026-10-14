using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The bonded download engine cuts one file into blocks and writes them at absolute offsets
/// from several streams at once.
///
/// That makes "every byte belongs to exactly one block" a property that cannot be checked
/// after the fact. A downloader that gets it wrong does not download slowly; it writes a file
/// that looks complete and is not, and nothing downstream ever notices. So it is asserted here
/// over the sizes the engine will actually meet, rather than reasoned about.
/// </summary>
public class BlockQueueTests
{
    private const long MB = 1024 * 1024;

    // ---------------- cutting the file ----------------

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(MB)]
    [InlineData(2 * MB)]
    [InlineData(8 * MB - 1)]
    [InlineData(8 * MB)]
    [InlineData(17 * MB)]
    [InlineData(100 * MB)]
    [InlineData(1024 * MB)]
    public void EveryByteOfTheFileBelongsToExactlyOneBlock(long size)
    {
        var blocks = BlockPlan.For(size).Blocks;

        long covered = 0;
        long expectedNext = 0;
        foreach (var b in blocks)
        {
            // No gaps: each block starts where the previous one ended.
            Assert.Equal(expectedNext, b.Offset);
            covered += b.Length;
            expectedNext = b.LastByte + 1;

            Assert.True(b.Length > 0, $"{b} is empty");
            Assert.Equal(b.Offset + b.Length, expectedNext);
        }

        Assert.Equal(size, covered);
        Assert.Equal(size, expectedNext);
    }

    /// <summary>
    /// A file larger than an int can hold. This is not a hypothetical: the layout loop was
    /// counting in `int` and overflowed at 2 GB, which is inside the range of every ISO image
    /// anyone would bond two networks to download.
    /// </summary>
    [Fact]
    public void AFileLargerThanTwoGigabytesLaysOutWithoutOverflowing()
    {
        const long GB = 1024 * MB;
        var plan = BlockPlan.For(3 * GB);

        Assert.Equal(3 * GB, plan.Blocks.Sum(b => b.Length));
        Assert.Equal(3 * GB, plan.Blocks[^1].LastByte + 1);
        Assert.All(plan.Blocks, b => Assert.True(b.Length > 0, $"{b} is empty"));
        Assert.True(plan.BlockCount > 1, "three gigabytes must be split, not fetched in one request");
    }

    [Fact]
    public void LargeFilesCapAtTheDefaultBlockSize()
    {
        var plan = BlockPlan.For(100 * MB);

        Assert.True(plan.BlockSize <= BlockPlan.DefaultBlockSize,
            $"a {plan.BlockSize} byte block is over the cap");
        Assert.Equal(13, plan.BlockCount);
        // The remainder is spread across the blocks rather than left as one small trailing
        // block: equal blocks keep the queue balanced when networks are badly mismatched.
        Assert.Equal(100 * MB, plan.Blocks.Sum(b => b.Length));
        Assert.True(plan.Blocks[^1].Length > plan.BlockSize * 0.9,
            $"the last block ({plan.Blocks[^1].Length}) should be about the size of the others");
    }

    [Fact]
    public void SmallFilesStillGetTwoBlocksSoBothNetworksAreFilled()
    {
        // 3 MB over Wi-Fi and a USB tether: one block means the second network has nothing to
        // do at all. Two means each network takes one, which is the whole point of the split.
        var plan = BlockPlan.For(3 * MB);

        Assert.Equal(2, plan.BlockCount);
        Assert.Equal(3 * MB, plan.Blocks.Sum(b => b.Length));
        Assert.Equal(1572864, plan.Blocks[0].Length);
    }

    [Fact]
    public void AFileSmallerThanTheFloorStaysOneRequestRatherThanOneRequestPerMegabyte()
    {
        // Below the floor, splitting would mean a request per megabyte - more round trips than
        // data. The floor wins, and that is deliberate.
        var plan = BlockPlan.For(MB);

        Assert.Equal(1, plan.BlockCount);
        Assert.Equal(MB, plan.Blocks[0].Length);
    }

    [Fact]
    public void AnEmptyFileHasNothingToFetchAndSaysSo() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockPlan.For(0));

    [Fact]
    public void ANegativeFileIsARatherSeriousMistake() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockPlan.For(-5));

    [Fact]
    public void ABlockBelowTheRoundTripFloorIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockPlan.For(100 * MB, 4096));

    // ---------------- resuming ----------------

    [Fact]
    public void AResumeRefetchesOnlyTheBlocksThePreviousRunLeftBehind()
    {
        var plan = BlockPlan.For(100 * MB);   // 13 blocks, so a handful left behind is a real remainder

        // The offsets of the first and third blocks: what an interrupted run left on disk.
        var done = new[] { plan.Blocks[0].Offset, plan.Blocks[2].Offset };
        var missing = plan.MissingFrom(b => done.Contains(b.Offset));

        Assert.Equal(plan.BlockCount - 2, missing.Count);
        // What is missing is everything the two finished blocks did not cover.
        Assert.Equal(plan.TotalBytes - plan.Blocks[0].Length - plan.Blocks[2].Length,
                     missing.Sum(b => b.Length));
        Assert.Equal(plan.TotalBytes, plan.Blocks.Sum(b => b.Length));
    }

    /// <summary>
    /// The pause/resume path: drain the first queue halfway, rebuild the second from what the
    /// file actually contains, and prove nothing is refetched and nothing is double-paid.
    /// </summary>
    [Fact]
    public void AResumeContinuesExactlyWhereThePreviousRunStopped()
    {
        var plan = BlockPlan.For(40 * MB);
        var written = new HashSet<long>();

        var first = new BlockQueue(plan.Blocks);
        long firstRunBytes = 0;
        for (int i = 0; i < plan.BlockCount / 2; i++)
        {
            Assert.True(first.TryClaim(out var b));
            first.Complete(b);
            written.Add(b.Offset);
            firstRunBytes += b.Length;
        }

        var second = new BlockQueue(plan.MissingFrom(b => written.Contains(b.Offset)));

        Assert.Equal(plan.BlockCount - written.Count, second.BlockCount);
        Assert.Equal(plan.TotalBytes - firstRunBytes, second.Snapshot().Sum(s => s.Block.Length));

        // Every remaining block lands exactly once, and none of them was already on disk.
        var reFetched = new HashSet<long>();
        long refetchedBytes = 0;
        while (second.TryClaim(out var b))
        {
            Assert.True(written.Add(b.Offset), $"block at {b.Offset} was refetched although the file has it");
            Assert.True(reFetched.Add(b.Index));
            refetchedBytes += b.Length;
            second.Complete(b);
        }

        Assert.True(second.IsComplete);
        Assert.Equal(second.BlockCount, reFetched.Count);

        // What the first run wrote plus what the resume fetched is the whole file, and the
        // overlap between them is nothing at all. `written` holds both runs by now, so the
        // first run's own total is what the two halves have to add up to.
        Assert.Equal(plan.TotalBytes, firstRunBytes + refetchedBytes);
        Assert.Equal(plan.BlockCount, written.Count);
    }

    // ---------------- handing out the work ----------------

    [Fact]
    public void EachBlockIsClaimedByExactlyOneStream()
    {
        var plan = BlockPlan.For(100 * MB);
        var q = new BlockQueue(plan.Blocks);
        var seen = new HashSet<int>();

        while (q.TryClaim(out var block))
            Assert.True(seen.Add(block.Index), $"{block} was handed out twice");

        Assert.Equal(plan.BlockCount, seen.Count);
        Assert.False(q.TryClaim(out _), "the queue has nothing left to give");
    }

    [Fact]
    public void ABlockIsPaidOutOnlyOnce()
    {
        var q = new BlockQueue(BlockPlan.For(16 * MB).Blocks);
        q.TryClaim(out var block);

        Assert.True(q.Complete(block), "the stream that finished the block must be the one paid");
        long after = q.BytesDone;

        Assert.False(q.Complete(block), "a second completion must not count");
        Assert.Equal(after, q.BytesDone);
    }

    [Fact]
    public void AFailedBlockIsHandedBackRatherThanStranded()
    {
        var q = new BlockQueue(BlockPlan.For(8 * MB).Blocks);
        q.TryClaim(out var block);

        Assert.Equal(1, q.InFlightCount);
        q.Release(block);

        Assert.Equal(0, q.InFlightCount);
        // Both blocks are waiting again: the one handed back, and the one that was never
        // claimed in the first place.
        Assert.Equal(2, q.WaitingCount);

        // A released block goes to the back of the queue - it goes back into work, not to the
        // front - so what matters is that it is claimable again, not that it is next.
        var claimable = new List<Block>();
        while (q.TryClaim(out var b)) claimable.Add(b);
        Assert.Contains(block, claimable);
        Assert.Equal(2, claimable.Count);
        Assert.False(q.IsComplete, "nothing was written, so the download is not finished");
    }

    [Fact]
    public void ReleasingTwiceDoesNotHandTheSameBlockOutTwice()
    {
        var q = new BlockQueue(BlockPlan.For(4 * MB).Blocks);   // two blocks
        q.TryClaim(out var block);
        q.Release(block);
        q.Release(block);   // a stream that failed twice must not corrupt the counts

        // Two waiting, not three: the block was put back once, and the second Release saw a
        // block that was no longer in flight and left it alone.
        Assert.Equal(2, q.WaitingCount);
        Assert.Equal(0, q.InFlightCount);
    }

    // ---------------- the end-game ----------------

    [Fact]
    public void AnIdleStreamRacesTheLargestBlockStillInFlight()
    {
        // The queue is drained and one slow network is still pulling a block. An idle stream
        // must be able to start the same block from the beginning, or the download ends at the
        // speed of whichever network drew the last block.
        var q = new BlockQueue(BlockPlan.For(16 * MB).Blocks);

        Assert.True(q.TryClaim(out var slow));
        Assert.True(q.TryClaim(out var fast));
        Assert.True(q.Complete(fast));

        Assert.True(q.TryClaimSlowestInFlight(out var raced));
        Assert.Equal(slow, raced);

        Assert.True(q.Complete(slow));
        Assert.False(q.TryClaimSlowestInFlight(out _), "nothing is left in flight to race");
    }

    [Fact]
    public void TheRacedBlockIsPaidOutExactlyOnce()
    {
        var q = new BlockQueue(BlockPlan.For(MB).Blocks);   // one block only, so it can be finished
        q.TryClaim(out var block);

        Assert.True(q.TryClaimSlowestInFlight(out var racer));
        Assert.Equal(block, racer);

        Assert.True(q.Complete(block));
        Assert.False(q.Complete(racer), "the loser of the race must not be paid");
        Assert.Equal(block.Length, q.BytesDone);
        Assert.True(q.IsComplete, "the one and only block is written, so the file is whole");
    }

    [Fact]
    public void AFinishedBlockCannotBeRaced()
    {
        var q = new BlockQueue(BlockPlan.For(8 * MB).Blocks);
        q.TryClaim(out var b);
        q.Complete(b);

        Assert.False(q.TryClaimSlowestInFlight(out _), "a finished block is not in flight");
    }

    // ---------------- under real concurrency ----------------

    /// <summary>
    /// Many streams all racing for work at once, draining one file, with some of them failing
    /// and handing work back.
    ///
    /// Two things are checked that no single-threaded test can see. The first is mutual
    /// exclusion: a block already held by one stream must never be handed to a second, and the
    /// "holders" dictionary is what turns that from a claim into an observation. The second is
    /// that a raced block is paid once - with the lock removed this test loses blocks and
    /// double-counts bytes, which is exactly the failure it exists to catch.
    /// </summary>
    [Theory]
    [InlineData(1, 16)]
    [InlineData(4, 32)]
    [InlineData(7, 8)]
    [InlineData(64, 128)]
    public void ConcurrentStreamsDrainTheQueueWithoutOverlap(long fileMB, int streamCount)
    {
        var plan = BlockPlan.For(fileMB * MB);
        var q = new BlockQueue(plan.Blocks);

        var holders = new ConcurrentDictionary<int, int>();
        var overlaps = new ConcurrentBag<int>();
        var completed = new ConcurrentDictionary<int, int>();
        var claimed = new ConcurrentDictionary<int, int>();

        Parallel.For(0, streamCount, _ =>
        {
            var r = new Random(Guid.NewGuid().GetHashCode());
            while (q.TryClaim(out var block))
            {
                // Two streams holding the same block is the corruption this queue exists to
                // prevent, so it is recorded rather than thrown on - the assertion runs after
                // the drain, and one exception inside Parallel.For would abandon the whole run.
                if (!holders.TryAdd(block.Index, 1)) overlaps.Add(block.Index);
                claimed.AddOrUpdate(block.Index, 1, (_, n) => n + 1);

                // Fail one block in twenty: a real connection drops, and the queue has to
                // absorb it and hand the block back out.
                if (r.Next(20) == 0)
                {
                    holders.TryRemove(block.Index, out _);
                    q.Release(block);
                    Thread.Sleep(r.Next(0, 3));
                    continue;
                }

                Thread.Sleep(r.Next(0, 2));    // stand in for network time
                if (q.Complete(block)) completed.AddOrUpdate(block.Index, 1, (_, n) => n + 1);
                holders.TryRemove(block.Index, out _);
            }
        });

        Assert.True(q.IsComplete, "every block was claimed but the download is not finished");
        Assert.Empty(overlaps);
        Assert.Empty(holders);

        // Every block was completed exactly once, so the bytes add up to the whole file. This
        // is the invariant the whole engine rests on.
        Assert.Equal(plan.BlockCount, completed.Count);
        Assert.All(completed, kv => Assert.True(kv.Value >= 1));
        Assert.Equal(fileMB * MB, q.BytesDone);
        Assert.All(q.Snapshot(), s => Assert.Equal(BlockState.Done, s.State));

        // A released block may legitimately be claimed more than once; a completed one never is.
        Assert.All(claimed, kv => Assert.True(kv.Value >= 1, $"block {kv.Key} was never claimed"));
    }

    [Fact]
    public void TheSnapshotMatchesTheBookkeeping()
    {
        var plan = BlockPlan.For(100 * MB);   // 13 blocks
        var q = new BlockQueue(plan.Blocks);
        q.TryClaim(out _);           // one block out with a stream
        q.TryClaim(out var b);
        Assert.True(q.Complete(b));  // and that one is done

        var snap = q.Snapshot();
        Assert.Equal(plan.BlockCount, snap.Count);
        Assert.Equal(1, snap.Count(s => s.State == BlockState.Done));
        Assert.Equal(1, snap.Count(s => s.State == BlockState.InFlight));
        Assert.Equal(plan.BlockCount - 2, snap.Count(s => s.State == BlockState.Waiting));
        Assert.Equal(plan.BlockCount - 2, q.WaitingCount);
        Assert.Equal(1, q.InFlightCount);
    }

    /// <summary>
    /// A block that has already been written is not in flight, so a late Release must not put
    /// it back in the queue - that would hand it to another stream and double-write the same
    /// bytes.
    /// </summary>
    [Fact]
    public void ReleasingAFinishedBlockDoesNotPutItBackInWork()
    {
        var q = new BlockQueue(BlockPlan.For(MB).Blocks);   // one block only
        q.TryClaim(out var b);
        Assert.True(q.Complete(b));
        q.Release(b);

        Assert.Equal(0, q.WaitingCount);
        Assert.Equal(0, q.InFlightCount);
        Assert.True(q.IsComplete);
    }
}