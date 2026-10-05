using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using NetPilot.Core;
using NetPilot.Core.Logging;

namespace NetPilot.ViewModels;

/// <summary>One line in the diagnostics list, flattened for the view.</summary>
public sealed class LogLineRow
{
    public LogLineRow(LogEntry e)
    {
        Stamp = LogLevelExtensions.Stamp(e.Timestamp).Replace("Z", "");
        Tag = e.Level.Tag();
        Text = e.Message + (string.IsNullOrEmpty(e.Detail) ? "" : "\n" + e.Detail);
        BrushKey = e.Level switch
        {
            LogLevel.Error or LogLevel.Fatal => "B.Danger",
            LogLevel.Warn => "B.Warning",
            LogLevel.Debug or LogLevel.Trace => "B.Text2",
            _ => "B.Text",
        };
    }

    public string Stamp { get; }
    public string Tag { get; }
    public string Text { get; }

    /// <summary>A resource key, not a brush: the Windows app's brushes come from the theme
    /// dictionary, and a hard-coded colour here would not follow a theme change.</summary>
    public string BrushKey { get; }
}

/// <summary>
/// The diagnostics page: the log, the counters that say whether to trust it, and the one
/// button that turns all of it into something a person can paste into a support thread.
///
/// Derives from <see cref="PageVmBase"/> like every other page, which is what gives it the
/// shared language binding and the automatic re-read on a language switch.
///
/// The counters sit above the log on purpose. A log with a hole in it is worse than useless -
/// it looks complete - so "dropped: 447" belongs next to the entries, not in a file the user
/// has to go and find.
/// </summary>
public sealed class DiagnosticsViewModel : PageVmBase
{
    public DiagnosticsViewModel()
    {
        RefreshCommand = new RelayCommand(() => Refresh());
        Refresh();
    }

    public ObservableCollection<LogLineRow> Lines { get; } = new();

    public IReadOnlyList<LogLevel> Levels { get; } =
        new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error };

    private LogLevel _level = LogLevel.Trace;
    public LogLevel Level
    {
        get => _level;
        set { if (Set(ref _level, value)) Refresh(); }
    }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value ?? "")) { Raise(nameof(FilterIsEmpty)); Refresh(); } }
    }

    /// <summary>
    /// Drives the search field's hint. Separate from testing <see cref="Filter"/> in the view,
    /// because an empty string as a bound value is not distinguishable from "not set" - so a
    /// hint written straight into the field would look like a search the user had typed.
    /// </summary>
    public bool FilterIsEmpty => _filter.Length == 0;

    public RelayCommand RefreshCommand { get; }

    public string LogPath => Log.FilePath ?? Lang["dash_logger_memory_only"];

    public string WrittenText => string.Format(Lang["dash_logger_written"], Log.WrittenCount);

    public string DroppedText => string.Format(Lang["dash_logger_dropped"], Log.DroppedCount);

    public string QueuedText => string.Format(Lang["dash_logger_queued"], Log.QueuedCount);

    public string SummaryText => string.Format(
        Lang["dash_logger_runtime"],
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        App.IsElevated() ? Lang["dash_logger_admin"] : Lang["dash_logger_user"]);

    /// <summary>True when entries were lost or the file could not be written. The page says so
    /// rather than presenting a short log as a complete one.</summary>
    public bool HasProblems => Log.DroppedCount > 0 || Log.WriteFailureCount > 0;

    public string ProblemText => string.Format(Lang["dash_logger_problem"],
                                               Log.DroppedCount, Log.WriteFailureCount);

    public string EntryCountText => string.Format(Lang["dash_logger_entries"], Lines.Count);

    /// <summary>Re-reads the in-memory buffer. Cheap: the buffer is bounded.</summary>
    public void Refresh()
    {
        var entries = Log.Recent(400, _level, null, string.IsNullOrEmpty(_filter) ? null : _filter);
        Lines.Clear();
        foreach (var e in entries) Lines.Add(new LogLineRow(e));
        Raise(nameof(LogPath));
        Raise(nameof(WrittenText));
        Raise(nameof(DroppedText));
        Raise(nameof(QueuedText));
        Raise(nameof(SummaryText));
        Raise(nameof(HasProblems));
        Raise(nameof(ProblemText));
        Raise(nameof(EntryCountText));
    }
}