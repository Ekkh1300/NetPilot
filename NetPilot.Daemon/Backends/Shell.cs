using System;
using System.Collections.Generic;
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
///
/// The list-argument overloads are the ones callers should use. They existed because the string
/// form was a command-injection path: it handed the target program one string, which that
/// program split by its own rules, so an interface name, resolver address or nft rule arriving
/// from an HTTP request could become extra arguments - and anything carrying a shell
/// metacharacter could become a different command.
/// </summary>
internal static class Shell
{
    /// <param name="stdin">When set, written to the process's stdin. This is how nft and tc
    /// take a whole script: passing a rule list as command-line arguments means re-quoting
    /// every rule, and one unbalanced quote becomes a command that silently does nothing.</param>
    public static (bool Ok, string Output) Run(string file, string arguments, int timeoutMs, string stdin = null)
        => RunAsync(file, arguments, timeoutMs, CancellationToken.None, stdin).GetAwaiter().GetResult();

    /// <summary>
    /// Runs a command with each argument passed as one unit. This is the form that is safe for
    /// any value that came from outside the process.
    /// </summary>
    public static (bool Ok, string Output) Run(
        string file, IReadOnlyList<string> args, int timeoutMs, string stdin = null)
        => RunAsync(file, args, timeoutMs, CancellationToken.None, stdin).GetAwaiter().GetResult();

    public static Task<(bool Ok, string Output)> RunAsync(
        string file, IReadOnlyList<string> args, int timeoutMs, CancellationToken ct, string stdin = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return StartAsync(psi, file, timeoutMs, ct, stdin);
    }

    /// <summary>
    /// The escaping overload, kept for the few callers whose arguments are literals with nothing
    /// external in them.
    ///
    /// Not a security boundary and not offered as one: the split is decided by the callee's own
    /// parser, so quoting here cannot be complete. Anything that came from outside should use the
    /// list overload, where each value is one argument by construction.
    /// </summary>
    public static async Task<(bool Ok, string Output)> RunAsync(
        string file, string arguments, int timeoutMs, CancellationToken ct, string stdin = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in SplitArguments(arguments)) psi.ArgumentList.Add(a);
        return await StartAsync(psi, file, timeoutMs, ct, stdin).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits an argument string on unquoted whitespace, honouring double quotes the way a
    /// Windows command line would. Exposed for the tests, since this is the exact behaviour that
    /// made an injected value able to become extra arguments.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments)) return result;

        var current = new StringBuilder();
        bool inQuotes = false;
        bool has = false;

        foreach (char c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                has = true;      // "" is an explicit empty argument
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (has) { result.Add(current.ToString()); current.Clear(); has = false; }
                continue;
            }
            current.Append(c);
            has = true;
        }
        if (has) result.Add(current.ToString());
        return result;
    }

    private static async Task<(bool Ok, string Output)> StartAsync(
        ProcessStartInfo psi, string file, int timeoutMs, CancellationToken ct, string stdin)
    {
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