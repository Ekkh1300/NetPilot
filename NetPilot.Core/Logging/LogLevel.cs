using System;
using System.Globalization;

namespace NetPilot.Core.Logging;

/// <summary>How much a log entry matters, and therefore whether it is written at all.</summary>
public enum LogLevel
{
    /// <summary>Everything, including per-packet detail. Off by default: it is far too
    /// chatty to ship enabled, and a file nobody can read is not a diagnostic tool.</summary>
    Trace = 0,

    /// <summary>Detail that helps understand a decision - which link was chosen, which rule
    /// was skipped. On by default in the desktop app, off for the daemon.</summary>
    Debug = 1,

    /// <summary>Normal operation: started, stopped, rule applied, probe completed.</summary>
    Info = 2,

    /// <summary>Something unexpected that the app recovered from. A refused firewall rule,
    /// a device that vanished mid-enumeration, a proxy that stopped answering.</summary>
    Warn = 3,

    /// <summary>A failure the user will notice.</summary>
    Error = 4,

    /// <summary>The app cannot continue.</summary>
    Fatal = 5,
}

public static class LogLevelExtensions
{
    /// <summary>Short fixed-width tag, so a log file stays greppable by eye.</summary>
    public static string Tag(this LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Fatal => "FTL",
        _ => "???",
    };

    public static bool Includes(this LogLevel threshold, LogLevel level) => level >= threshold;

    /// <summary>
    /// Parses a level name, case-insensitively, and refuses anything unrecognised rather than
    /// defaulting. A typo in a config file that silently became "log everything" is worse than
    /// a typo that is reported.
    /// </summary>
    public static bool TryParse(string text, out LogLevel level)
    {
        level = LogLevel.Info;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToLowerInvariant())
        {
            case "trace" or "trc" or "all" or "verbose": level = LogLevel.Trace; return true;
            case "debug" or "dbg": level = LogLevel.Debug; return true;
            case "info" or "inf": level = LogLevel.Info; return true;
            case "warn" or "warning" or "wrn": level = LogLevel.Warn; return true;
            case "error" or "err": level = LogLevel.Error; return true;
            case "fatal" or "ftl" or "none" or "off": level = LogLevel.Fatal; return true;
            default: return false;
        }
    }

    /// <summary>Round-trippable timestamp: ISO 8601 to the second, UTC.
    ///
    /// UTC because a log is evidence. A support conversation spans time zones and daylight
    /// saving, and "01:15" twice a year is not a timestamp.</summary>
    public static string Stamp(DateTime t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string StampMs(DateTime t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}