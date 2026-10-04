using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NetPilot.Core.Logging;

namespace NetPilot.Desktop;

/// <summary>One line in the diagnostics list, flattened for the view.</summary>
public sealed class LogLine
{
    public LogLine(LogEntry e)
    {
        Stamp = LogLevelExtensions.Stamp(e.Timestamp).Replace("Z", "");
        Tag = e.Level.Tag();
        Text = e.Message + (e.Detail != null ? "\n" + e.Detail : string.Empty);
        LevelBrush = e.Level switch
        {
            LogLevel.Error or LogLevel.Fatal => "#F87171",
            LogLevel.Warn => "#FBBF24",
            LogLevel.Debug or LogLevel.Trace => "#93A1BC",
            _ => "#E8EDF7",
        };
    }

    public string Stamp { get; }
    public string Tag { get; }
    public string Text { get; }
    public string LevelBrush { get; }
}

/// <summary>
/// The diagnostics page: the log, the counters that say whether to trust it, and the one
/// button that turns all of it into something a person can paste into a support thread.
///
/// The counters sit above the log on purpose. A log with a hole in it is worse than useless -
/// it looks complete - so "dropped: 447" belongs next to the entries, not in a file the user
/// has to go and find.
/// </summary>
public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private readonly AppServices _services;
    private LogLevel _level = LogLevel.Trace;
    private string _filter = "";

    public DiagnosticsViewModel(AppServices services) => _services = services;

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<LogLine> LogLines { get; } = new();

    public IReadOnlyList<LogLevel> LogLevels { get; } =
        new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error };

    public LogLevel LogLevel
    {
        get => _level;
        set { _level = value; Refresh(); Raise(); }
    }

    public string LogFilter
    {
        get => _filter;
        set { _filter = value ?? ""; Refresh(); Raise(); }
    }

    public string LogPath => Log.FilePath ?? "(memory only - no writable directory)";

    public string LogWritten => "written: " + Log.WrittenCount;

    public string LogDropped => "dropped: " + Log.DroppedCount;

    public string LogQueued => "queued: " + Log.QueuedCount;

    public string LogEntryCount => LogLines.Count + " entries";

    public bool LogHasProblems => Log.DroppedCount > 0 || Log.WriteFailureCount > 0;

    public string LogSummary =>
        "Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription +
        " · " + System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    /// <summary>Re-reads the in-memory buffer. Called on a timer and whenever a filter
    /// changes; the buffer is bounded so this is cheap.</summary>
    public void Refresh()
    {
        var entries = Log.Recent(400, _level,
                                categoryFilter: null,
                                textFilter: string.IsNullOrEmpty(_filter) ? null : _filter);
        LogLines.Clear();
        foreach (var e in entries) LogLines.Add(new LogLine(e));

        // The counters are raised with the entries. They used to be read only when the page
        // opened, so a log that had filled up and started dropping showed "dropped: 0"
        // directly above the entries that were missing because of it.
        Raise(nameof(LogEntryCount));
        Raise(nameof(LogHasProblems));
        Raise(nameof(LogWritten));
        Raise(nameof(LogDropped));
        Raise(nameof(LogQueued));
    }

    /// <summary>Everything, as one block of text, already redacted. This is the whole point of
    /// the page: the user should not have to assemble a support report by hand.</summary>
    public string BuildDiagnosticsReport() => Log.DiagnosticsReport();

    /// <summary>One click to the clipboard. Avalonia's clipboard wants a data object, and there
    /// is no implicit conversion from string - which is why this is not just
    /// <c>Clipboard.SetTextAsync(s)</c>.</summary>
    public async System.Threading.Tasks.Task CopyDiagnosticsAsync()
    {
        string report = BuildDiagnosticsReport();

        // The window is reached through the lifetime rather than through a control reference:
        // a view model that has to be handed its own view in order to use the clipboard is a
        // view model that cannot be created before the view exists.
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var clip = desktop?.MainWindow?.Clipboard;
        if (clip is null)
        {
            Log.Warn("diagnostics", "no clipboard is available; the report was not copied");
            return;
        }

        try
        {
            await clip.SetTextAsync(report);
            Log.Info("diagnostics", $"diagnostics report copied, {report.Length} characters");
        }
        catch (Exception ex)
        {
            // A clipboard that refuses is not worth a crash, and the reason belongs in the
            // very log the user just asked to copy.
            Log.Warn("diagnostics", "could not copy the report to the clipboard", ex);
        }
    }

    /// <summary>Opens the log directory in the platform's file manager, or says why not.
    /// Revealing a file the user has never heard of is the difference between "restart the
    /// app" and "here, look at this file".</summary>
    public void OpenLogFolder()
    {
        string dir = Log.FilePath is string p ? Path.GetDirectoryName(p) : null;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Log.Warn("diagnostics", "there is no log directory to open");
            return;
        }
        try
        {
            var os = Environment.OSVersion.Platform;
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", dir);
            else
                System.Diagnostics.Process.Start("xdg-open", dir);
            Log.Info("diagnostics", "opened the log directory");
        }
        catch (Exception ex)
        {
            Log.Warn("diagnostics", "could not open the log directory", ex);
        }
    }

    /// <summary>Avalonia bindings need a command object; the page declares two.</summary>
    public System.Windows.Input.ICommand CopyDiagnostics => new AsyncCommand(CopyDiagnosticsAsync);

    public System.Windows.Input.ICommand OpenLogFolderCommand => new SyncCommand(OpenLogFolder);

    private sealed class AsyncCommand : System.Windows.Input.ICommand
    {
        private readonly Func<System.Threading.Tasks.Task> _run;
        public AsyncCommand(Func<System.Threading.Tasks.Task> run) => _run = run;
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public async void Execute(object parameter)
        {
            try { await _run(); } catch { /* the action logs its own failure */ }
        }
    }

    private sealed class SyncCommand : System.Windows.Input.ICommand
    {
        private readonly Action _run;
        public SyncCommand(Action run) => _run = run;
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter)
        {
            try { _run(); } catch { }
        }
    }

    private void Raise([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}