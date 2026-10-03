using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetPilot.Daemon.Backends;

/// <summary>
/// Runs a system command and returns whether it worked, instead of throwing.
///
/// Every backend shells out: <c>nft</c>, <c>tc</c>, <c>ip</c>, <c>gsettings</c>,
/// <c>networksetup</c>. A missing binary, a non-zero exit or a refusal from the machine are
/// ordinary answers, not exceptions - the caller turns them into a reason the user can read.
/// </summary>
internal static class Shell
{
    /// <param name="stdin">When set, written to the process's stdin. This is how nft and tc
    /// take a whole script: passing a rule list as command-line arguments means re-quoting
    /// every rule, and one unbalanced quote becomes a command that silently does nothing.</param>
    public static (bool Ok, string Output) Run(string file, string arguments, int timeoutMs, string stdin = null)
        => RunAsync(file, arguments, timeoutMs, CancellationToken.None, stdin).GetAwaiter().GetResult();

    public static async Task<(bool Ok, string Output)> RunAsync(
        string file, string arguments, int timeoutMs, CancellationToken ct, string stdin = null)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };

        try { proc.Start(); }
        catch { return (false, $"{file} is not installed"); }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (stdin != null)
        {
            try { proc.StandardInput.Write(stdin); proc.StandardInput.Close(); } catch { }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A hung command must not take the daemon with it. Say so rather than reporting
            // an empty result, which would look like "the machine said nothing".
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (false, $"{file} timed out after {timeoutMs} ms");
        }

        return (proc.ExitCode == 0, sb.ToString().Trim());
    }
}