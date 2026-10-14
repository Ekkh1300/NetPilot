using System;
using System.Collections.Generic;

namespace NetPilot.Services;

/// <summary>One contiguous stretch of the file, with the absolute offset it belongs at.</summary>
public readonly struct Block : IEquatable<Block>
{
    public int Index { get; }
    public long Offset { get; }
    public long Length { get; }

    public Block(int index, long offset, long length)
    {
        Index = index; Offset = offset; Length = length;
    }

    /// <summary>The last byte this block covers, inclusive - the `end` half of a Range header.</summary>
    public long LastByte => Offset + Length - 1;

    public bool Equals(Block other) => Index == other.Index && Offset == other.Offset && Length == other.Length;
    public override bool Equals(object obj) => obj is Block b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Index, Offset, Length);
    public override string ToString() => $"[{Index}] {Offset}..{LastByte}";
}

/// <summary>
/// How a file is cut into blocks before a single byte is requested.
///
/// Splitting a file into equal halves - one per network - makes a bonded download exactly as
/// fast as its slowest network, because the fast one sits idle waiting for the last chunk. So
/// the file is cut into many blocks and the blocks go into one shared queue instead; every
/// stream takes whichever block comes next, which is what makes a fast network do more of the
/// work automatically, without anyone estimating throughputs in advance.
///
/// The size of a block is the one tuning knob that matters:
///
///   Too large, and the queue is short, so there are not enough work units to spread across
///   networks, and a stream that dies throws away a whole lot of the file.
///   Too small, and per-request overhead starts to matter - every block is an HTTP request, so
///   a million 8 KB blocks means a million round trips.
///
/// 8 MB is the default and, from the same reasoning, the same choice the tool this was modelled
/// on makes. Small files get smaller blocks so that a two-network machine still has two streams
/// to fill, with a floor of 1 MB below which one request cannot be worth its own round trip.
///
/// Note on block count: a 16 MB file is two blocks, not sixteen. That is deliberate and it is
/// also why the number of streams is a separate setting - a bonded download runs many streams
/// per network, because a single TCP connection often cannot fill a fast link on its own. The
/// blocks are the work units; the streams are how many are in flight at once.
/// </summary>
public sealed class BlockPlan
{
    /// <summary>Default block size. Matches the upstream tool this was modelled on.</summary>
    public const long DefaultBlockSize = 8L * 1024 * 1024;

    /// <summary>A block smaller than this is not worth the HTTP round trip it costs.</summary>
    public const long MinBlockSize = 1024 * 1024;

    public long TotalBytes { get; }
    public long BlockSize { get; }
    public IReadOnlyList<Block> Blocks { get; }

    public int BlockCount => Blocks.Count;

    private BlockPlan(long totalBytes, long blockSize, IReadOnlyList<Block> blocks)
    {
        TotalBytes = totalBytes; BlockSize = blockSize; Blocks = blocks;
    }

    /// <exception cref="ArgumentOutOfRangeException">A file of nothing has nothing to fetch.</exception>
    public static BlockPlan For(long totalBytes, long preferredBlockSize = DefaultBlockSize)
    {
        if (totalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes),
                $"a file of {totalBytes} bytes has no blocks to fetch");
        if (preferredBlockSize < MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(preferredBlockSize),
                $"{preferredBlockSize} is below the {MinBlockSize} floor, so requests would cost more than they carry");

        // Two blocks per stream on a small file - that is the point of the size - but never
        // smaller than the round-trip floor, so a file under 2 MB stays one block rather than
        // becoming a request per megabyte.
        long sized = Math.Max(MinBlockSize,
                              Math.Min(preferredBlockSize, (totalBytes + 1) / 2));

        int count = (int)((totalBytes + sized - 1) / sized);
        long size = (totalBytes + count - 1) / count;

        // `long`, not int: a 4 GB ISO overflows an int counter at 2 GB, and it would do so
        // inside the loop that lays the file out - a silent, arithmetic failure on exactly the
        // files this feature exists for.
        var blocks = new List<Block>(count);
        for (long offset = 0; offset < totalBytes; offset += size)
            blocks.Add(new Block(blocks.Count, offset, Math.Min(size, totalBytes - offset)));

        return new BlockPlan(totalBytes, size, blocks);
    }

    /// <summary>
    /// The blocks this plan still has outstanding, given the ones already written to disk.
    ///
    /// This is what makes a restart cheap: rather than replay a journal, the finished state is
    /// the file itself, and any byte range that is already there does not need fetching again.
    ///
    /// Takes the whole block rather than just its offset, because what records progress is the
    /// block index - the staging file's record is a list of those - and mapping an offset back
    /// to an index to ask the question is a step that would eventually be done wrong.
    /// </summary>
    public IReadOnlyList<Block> MissingFrom(Predicate<Block> isWritten)
    {
        var missing = new List<Block>(Blocks.Count);
        foreach (var b in Blocks)
            if (!isWritten(b)) missing.Add(b);
        return missing;
    }
}