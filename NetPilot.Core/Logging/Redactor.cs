using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NetPilot.Core.Logging;

/// <summary>
/// Removes secrets before anything reaches a file.
///
/// This exists because people post logs in support threads. The product has a pairing token
/// and a six-digit pairing code, both of which grant a phone control of the machine's network;
/// a log that contains one is a log that hands over the machine to whoever can read it.
/// Redaction happens once, in one place, before any sink sees the text - so a sink cannot
/// forget it.
///
/// It is deliberately narrow. Redacting too much destroys the diagnostic value: interface
/// names, addresses and byte counters are the whole point of the file, so none of those are
/// touched. What goes are credentials, and MAC addresses, which identify a person's hardware
/// and serve no diagnostic purpose.
/// </summary>
public static class Redactor
{
    // Bearer tokens: the pairing token travels as "Authorization: Bearer <token>".
    private static readonly Regex Bearer = new(
        @"\b(Bearer\s+)[A-Za-z0-9._\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A JSON field named token / code / secret / password, with or without quotes around the key.
    private static readonly Regex JsonSecret = new(
        @"""(?<k>token|pairingCode|pairing_code|secret|password|passwd|apiKey|api_key)""\s*:\s*""(?<v>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A six-digit pairing code standing on its own. Six digits is specific enough on its own:
    // no log message in this product legitimately contains six bare digits.
    private static readonly Regex SixDigitCode = new(
        @"(?<![\d.])\d{6}(?![\d.])", RegexOptions.Compiled);

    // MAC addresses. Hardware identity with no diagnostic value.
    private static readonly Regex Mac = new(
        @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);

    /// <summary>The text substituted for anything removed. Kept stable so tests can assert on
    /// it and so a reader can see that something was removed rather than guess.</summary>
    public const string Mask = "[redacted]";

    /// <summary>Applies every rule. Cheap enough to run on every entry: the patterns are
    /// compiled and the input is a single line.</summary>
    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // Rules run longest-first in effect, not in code order: the JSON rule has to consume
        // "token":"..." before the bare six-digit rule sees a code inside it.
        text = Bearer.Replace(text, "$1" + Mask);
        text = JsonSecret.Replace(text, m => "\"" + m.Groups["k"].Value + "\":\"" + Mask + "\"");
        text = Mac.Replace(text, Mask);
        text = SixDigitCode.Replace(text, Mask);
        return text;
    }

    /// <summary>Applies every rule to an exception's full text, including the stack trace and
    /// any inner exceptions - a secret in a nested exception is still a secret.</summary>
    public static string Apply(Exception ex)
    {
        if (ex is null) return "";

        var sb = new System.Text.StringBuilder();
        int depth = 0;
        for (var e = ex; e != null && depth < 8; e = e.InnerException, depth++)
        {
            if (depth > 0) sb.Append(" ---> ");
            sb.Append(e.GetType().FullName).Append(": ").Append(e.Message).Append('\n');
            if (!string.IsNullOrEmpty(e.StackTrace)) sb.Append(e.StackTrace).Append('\n');
            if (e is AggregateException agg && agg.InnerExceptions.Count > 1)
            {
                // AggregateException.ToString() lists them all, but the loop above only walks
                // InnerException. Include the rest so nothing is lost.
                for (int i = 1; i < agg.InnerExceptions.Count; i++)
                {
                    sb.Append(" ---> ").Append(agg.InnerExceptions[i].GetType().FullName)
                      .Append(": ").Append(agg.InnerExceptions[i].Message).Append('\n');
                }
            }
        }
        return Apply(sb.ToString());
    }

    /// <summary>Counts how many redactions a piece of text would trigger. Used by the tests
    /// and by the diagnostics page, so the user can see that something was masked rather than
    /// wondering why a number looks odd.</summary>
    public static int CountRedactions(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        n += Bearer.Matches(text).Count;
        n += JsonSecret.Matches(text).Count;
        n += Mac.Matches(text).Count;
        n += SixDigitCode.Matches(text).Count;
        return n;
    }
}