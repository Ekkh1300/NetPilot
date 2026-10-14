using System;
using System.Collections.Generic;

namespace NetPilot.Services;

/// <summary>
/// The shared queue of blocks, and the only place the download's bookkeeping lives.
///
/// Every stream pulls from this one queue, so no block is ever handed to two streams at the
/// same time and no block is skipped - the property that matters most in a downloader that
/// writes a file at absolute offsets from several places at once. Getting it wrong does not
/// produce a slow download; it produces a corrupt file that looks fine.
///
/// A block carries the <see cref="Block.Index"/> it had in the <see cref="BlockPlan"/> that cut
/// the file, which is NOT its position here: a resumed download only holds the blocks the
/// previous run missed, so those blocks arrive numbered 2, 3, 4... and sit at 0, 1, 2. The
/// first version of this class looked blocks up by their Index, which meant <see cref="Complete"/>
/// found nothing, paid out nothing, and a resumed download never finished - and never failed
/// either, because every call reported success. The offset is the identity that survives a
/// resume, so that is what this maps on.
/// </summary>
public sealed class BlockQueue
{
    private readonly Block[] _blocks;

    /// <summary>Identity map. Offsets are unique within one plan and survive a resume.</summary>
    private readonly Dictionary<long, int> _byOffset;

    /// <summary>Indices a stream may be able to claim. Stale entries are skipped on dequeue.</summary>
    private readonly Queue<int> _ready = new();

    private readonly HashSet<int> _inFlight = new();
    private readonly HashSet<int> _finished = new();

    private long _bytesDone;

    /// <summary>Bytes written, paid out exactly once - including a raced block, whose loser is
    /// discarded rather than counted a second time.</summary>
    public long BytesDone { get { lock (_ready) return _bytesDone; } }

    /// <summary>Blocks a stream could take right now.</summary>
    public int WaitingCount { get { lock (_ready) return _blocks.Length - _inFlight.Count - _finished.Count; } }
    public int InFlightCount { get { lock (_ready) return _inFlight.Count; } }
    public bool IsComplete { get { lock (_ready) return _finished.Count == _blocks.Length; } }

    /// <summary>How many blocks this queue is responsible for - the whole file when it is a
    /// fresh download, only the missing ones when it is a resume.</summary>
    public int BlockCount => _blocks.Length;

    /// <summary>Blocks still to fetch, for an error message that has to be truthful about how
    /// far the download actually got.</summary>
    public int RemainingCount { get { lock (_ready) return _blocks.Length - _finished.Count; } }

    /// <summary>Fraction of blocks written, 0..1. For a progress bar and nothing else.</summary>
    public double Progress { get { lock (_ready) return _blocks.Length == 0 ? 0 : (double)_finished.Count / _blocks.Length; } }

    public BlockQueue(IReadOnlyList<Block> blocks)
    {
        _blocks = new Block[blocks.Count];
        _byOffset = new Dictionary<long, int>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            _blocks[i] = blocks[i];
            _byOffset[blocks[i].Offset] = i;
            _ready.Enqueue(i);
        }
    }

    /// <summary>Take the next block, or fail to. Does not block; a false means "nothing to do".</summary>
    public bool TryClaim(out Block block)
    {
        lock (_ready)
        {
            // A released block is re-enqueued, and a raced one stays in flight, so the queue can
            // hold indices that are no longer claimable. Skipping them keeps the counts right
            // without a second pass to clean the queue.
            while (_ready.Count > 0)
            {
                int i = _ready.Dequeue();
                if (_finished.Contains(i) || !_inFlight.Add(i)) continue;
                block = _blocks[i];
                return true;
            }
        }
        block = default;
        return false;
    }

    /// <summary>
    /// Hand a block back after a stream failed partway through it.
    ///
    /// Without this a single dropped connection would strand its block forever and the download
    /// would finish at 99% with no way to get the rest.
    /// </summary>
    public void Release(Block block)
    {
        lock (_ready)
        {
            if (!_byOffset.TryGetValue(block.Offset, out int i)) return;
            if (!_inFlight.Remove(i)) return;      // not ours, or already finished
            _ready.Enqueue(i);
        }
    }

    /// <summary>Record a block as written, and pay out its bytes exactly once.</summary>
    /// <returns>true when this call is the one that completed it - false when a racer beat it.</returns>
    public bool Complete(Block block)
    {
        lock (_ready)
        {
            if (!_byOffset.TryGetValue(block.Offset, out int i)) return false;
            if (!_finished.Add(i)) return false;    // someone else already got here

            _inFlight.Remove(i);
            _bytesDone += block.Length;
            return true;
        }
    }

    /// <summary>
    /// A block that is already in flight elsewhere, so a stream with nothing to do can race it.
    ///
    /// Near the end of a download the queue drains while one slow network is still pulling a
    /// block. Letting an idle stream start the same block from the beginning turns that last
    /// stretch from "however long the slow network takes" into "however long it takes on
    /// whichever of the two is faster". <see cref="Complete"/> is what keeps the bookkeeping
    /// honest when both streams eventually finish: the loser's result is discarded and the
    /// bytes are paid out once.
    /// </summary>
    public bool TryClaimSlowestInFlight(out Block block)
    {
        lock (_ready)
        {
            int best = -1;
            foreach (int i in _inFlight)
                // "Slowest" is not measurable from here - the queue knows order, not time - so it
                // takes the largest remaining block instead, which is the one most likely to be
                // the one holding the download up.
                if (best < 0 || _blocks[i].Length > _blocks[best].Length) best = i;

            if (best < 0) { block = default; return false; }
            block = _blocks[best];
            return true;
        }
    }

    /// <summary>All blocks with their current state, for a progress grid.</summary>
    public IReadOnlyList<(Block Block, BlockState State)> Snapshot()
    {
        lock (_ready)
        {
            var result = new (Block, BlockState)[_blocks.Length];
            for (int i = 0; i < _blocks.Length; i++)
                result[i] = (_blocks[i], _finished.Contains(i) ? BlockState.Done
                                                   : _inFlight.Contains(i) ? BlockState.InFlight
                                                                           : BlockState.Waiting);
            return result;
        }
    }
}

public enum BlockState
{
    Waiting = 0,
    InFlight = 1,
    Done = 2,
}