using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetPilot.Core.Logging;

/// <summary>One line in the log. Immutable, so it can be handed to a sink without a copy.</summary>
public sealed class LogEntry
{
    public LogEntry(DateTime ts, LogLevel level, string category, string message, string detail = null)
    {
        Timestamp = ts;
        Level = level;
        Category = category ?? "app";
        Message = message ?? "";
        Detail = detail;
    }

    public DateTime Timestamp { get; }

    public LogLevel Level { get; }

    /// <summary>Which subsystem wrote it: "bridge", "limiter", "tunnel", "ui". Levels are
    /// filtered per category, so the tunnel can be traced while the UI stays quiet.</summary>
    public string Category { get; }

    public string Message { get; }

    /// <summary>Stack trace and exception chain, already redacted. Null for ordinary entries.</summary>
    public string Detail { get; }

    /// <summary>One line, fixed layout, so the file stays readable with grep and tail.</summary>
    public string ToLine()
    {
        var sb = new StringBuilder(96);
        sb.Append(LogLevelExtensions.StampMs(Timestamp));
        sb.Append(' ').Append(Level.Tag());
        sb.Append(" [").Append(Category).Append(']');
        sb.Append(' ').Append(Message.Replace('\n', '\u2028').Replace('\r', ' '));
        return sb.ToString();
    }

    public override string ToString() => ToLine();
}