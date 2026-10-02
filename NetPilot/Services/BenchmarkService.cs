using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>Real, concurrent DNS benchmark: UDP DNS query RTT + ICMP ping, averaged over rounds.</summary>
public static class BenchmarkService
{
    public const string TestDomain = "www.gstatic.com";

    public static event Action<DnsBenchmarkResult> ResultUpdated;
    public static event Action Started;
    public static event Action Finished;

    public static async Task<List<DnsBenchmarkResult>> RunAsync(
        IReadOnlyList<DnsEntry> entries,
        int rounds,
        IProgress<double> progress = null,
        CancellationToken ct = default)
    {
        Started?.Invoke();
        // Six of these run at once and each used to results.Add(...) on this one List: the
        // additions raced (_size++ then _items[_size]), so entries were lost or the task threw
        // an ArgumentOutOfRangeException that only OperationCanceledException was catching.
        // A ConcurrentBag keeps every result; the caller gets it in score order anyway.
        var results = new System.Collections.Concurrent.ConcurrentBag<DnsBenchmarkResult>();
        if (entries == null || entries.Count == 0) { Finished?.Invoke(); return results.OrderByDescending(r => r.Score).ToList(); }

        using var throttle = new SemaphoreSlim(6);
        int completed = 0;
        var tasks = entries.Select(async entry =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var res = await Task.Run(() => BenchmarkOne(entry, rounds, ct), ct);
                results.Add(res);
                Interlocked.Increment(ref completed);
                progress?.Report((double)completed / entries.Count);
                ResultUpdated?.Invoke(res);
            }
            catch (OperationCanceledException) { }
            finally { throttle.Release(); }
        }).ToList();

        await Task.WhenAll(tasks);
        Finished?.Invoke();

        int rank = 1;
        foreach (var r in results.Where(r => !r.Failed).OrderByDescending(r => r.Score))
            r.Rank = rank++;
        return results.OrderByDescending(r => r.Score).ToList();
    }

    private static DnsBenchmarkResult BenchmarkOne(DnsEntry entry, int rounds, CancellationToken ct)
    {
        var res = new DnsBenchmarkResult { Entry = entry, Rounds = rounds };
        string primary = entry.ServersV4.FirstOrDefault();
        if (primary == null) { res.ComputeStats(); return res; }

        // 1) Real DNS response time over UDP, several rounds.
        for (int i = 0; i < rounds; i++)
        {
            ct.ThrowIfCancellationRequested();
            string server = entry.ServersV4[i % entry.ServersV4.Count];
            var answer = DnsQuery.QueryAsync(server, TestDomain, DnsQuery.TypeA, 2500, ct).GetAwaiter().GetResult();
            if (answer.Ok && answer.Records.Count > 0)
            {
                res.SuccessCount++;
                res.SamplesMs.Add(answer.Ms);
            }
            Thread.Sleep(60); // be polite between rounds
        }

        // 2) ICMP ping to the resolver (informational, shown separately).
        try
        {
            using var ping = new Ping();
            int okRounds = Math.Min(rounds, 3);
            var times = new List<long>();
            for (int i = 0; i < okRounds; i++)
            {
                ct.ThrowIfCancellationRequested();
                var reply = ping.Send(primary, 1500);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
                Thread.Sleep(50);
            }
            if (times.Count > 0)
            {
                res.PingOk = true;
                res.PingAvgMs = times.Average();
            }
        }
        catch { /* ICMP may be blocked; DNS RTT is the primary metric */ }

        res.ComputeStats();
        return res;
    }
}
