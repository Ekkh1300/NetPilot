using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace NetPilot.Services;

/// <summary>Helpers to run PowerShell commands asynchronously without blocking the UI.</summary>
public static class Sys
{
    [System.Runtime.InteropServices.DllImport("dnsapi.dll")]
    private static extern int DnsFlushResolverCache();

    /// <summary>Runs a PowerShell script and returns (success, combined output).</summary>
    public static Task<(bool ok, string output)> PsAsync(string script, int timeoutMs = 45000)
        => PsAsync(script, true, timeoutMs, null);

    /// <summary>
    /// Runs a PowerShell script, passing <paramref name="variables"/> as environment variables.
    ///
    /// This is how a value that came from the network reaches a script without being parsed as
    /// part of it. The script reads it back with <c>$env:NAME</c>. Interpolating a value into
    /// the script text instead is what allowed a paired phone to run commands as administrator
    /// through the peer-proxy address; PowerShell's quoting rules are subtle enough that getting
    /// that right by hand is not a standard worth holding a security boundary to.
    ///
    /// Environment variables set here are scoped to this process only - they are added to a
    /// fresh <see cref="ProcessStartInfo"/>, never to the current one, so nothing else in the app
    /// can read them back.
    /// </summary>
    public static Task<(bool ok, string output)> PsAsync(
        string script,
        int timeoutMs,
        IReadOnlyDictionary<string, string> variables)
        => PsAsync(script, true, timeoutMs, variables);

    /// <summary>Runs a PowerShell script and returns (success, <b>stdout only</b>).
    /// stderr is intentionally dropped: PowerShell serializes redirected error records as
    /// "#&lt; CLIXML&gt;…", which would corrupt JSON payloads.</summary>
    public static Task<(bool ok, string output)> PsStdoutAsync(string script, int timeoutMs = 45000)
        => PsAsync(script, false, timeoutMs, null);

    private static async Task<(bool ok, string output)> PsAsync(
        string script, bool includeStdErr, int timeoutMs,
        IReadOnlyDictionary<string, string>? variables)
    {
        try
        {
            // PowerShell 5.1 encodes redirected output with the OEM code page while the
            // reader below assumes UTF-8; forcing UTF-8 here keeps non-ASCII adapter names
            // intact. It must never be able to fail the script.
            const string prologue = "try{[Console]::OutputEncoding=[System.Text.Encoding]::UTF8}catch{};";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prologue + script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                // ArgumentList, not Arguments. A single Arguments string is re-parsed by the
                // target program's own command-line rules, so a value with a space or a quote
                // in it changes what runs. ArgumentList passes each argument as one unit and
                // cannot be re-split. The base64 blob is what made this survivable in practice -
                // it has no spaces - but that is an accident of the encoding, not a property of
                // the code, and the next caller may not be so lucky.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(encoded);

            if (variables != null)
            {
                foreach (var kv in variables)
                {
                    // An environment variable name is a key into a process block, so a value
                    // with an NUL in it would terminate the entry early. Rejected rather than
                    // sanitised: nothing legitimate contains one, and silently truncating a
                    // value the caller believes is intact would be worse.
                    if (kv.Key.Contains('\0') || kv.Value.Contains('\0')) continue;
                    psi.Environment[kv.Key] = kv.Value;
                }
            }
            using var p = Process.Start(psi);
            if (p == null) return (false, "failed to start powershell");

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => p.WaitForExit(timeoutMs));
            if (!exited)
            {
                try { p.Kill(); } catch { }
                return (false, "timeout");
            }
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            string combined = (includeStdErr ? stdout + stderr : stdout).Trim();
            return (p.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Pulls the first <i>parseable</i> balanced JSON object/array out of console
    /// output, so stray banners, warnings or CLIXML around it can never break parsing.
    /// Every bracket position is tried: a stray unmatched '[' in a warning line used to
    /// shadow the real payload that followed it.</summary>
    public static bool TryExtractJson(string raw, out string json)
    {
        json = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        const int maxCandidates = 32;
        int tried = 0;
        for (int start = 0; start < raw.Length && tried < maxCandidates; start++)
        {
            char open = raw[start];
            if (open != '{' && open != '[') continue;
            tried++;
            if (!TryBalanced(raw, start, out string candidate)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                {
                    json = candidate;
                    return true;
                }
            }
            catch { /* not the payload we are looking for; keep scanning */ }
        }
        return false;
    }

    private static bool TryBalanced(string raw, int start, out string payload)
    {
        payload = null;
        char open = raw[start];
        char close = open == '{' ? '}' : ']';

        // Track nesting so a stray close inside a string can't end the payload early.
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) { payload = raw.Substring(start, i - start + 1); return true; }
            }
        }
        return false;
    }

    /// <summary>Flushes the Windows DNS resolver cache in-process (fast, no shell).</summary>
    public static bool FlushDnsCache()
    {
        // Returns BOOL success. The old `== 0` test was inverted, so this always "failed"
        // and every flush paid for a PowerShell `ipconfig /flushdns` process spawn.
        try { return DnsFlushResolverCache() != 0; }
        catch { return false; }
    }

    public static async Task<bool> FlushDnsCacheAsync()
    {
        if (await Task.Run(FlushDnsCache)) return true;
        var (ok, _) = await PsAsync("ipconfig /flushdns | Out-Null");
        return ok;
    }
}
