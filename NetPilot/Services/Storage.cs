using System;
using System.IO;
using System.Text.Json;

namespace NetPilot.Services;

/// <summary>Thread-safe JSON persistence under %LOCALAPPDATA%\NetPilot.</summary>
public static class Storage
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPilot");

    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        IncludeFields = false,
    };

    static Storage() { Directory.CreateDirectory(Dir); }

    public static void LoadFailure(string file, Exception ex)
        => App.LogCrash(new IOException($"storage: could not read {file} - it will be re-created on the next save", ex));

    public static T Load<T>(string file, T fallback = default)
    {
        try
        {
            string path = Path.Combine(Dir, file);
            lock (_lock)
            {
                if (!File.Exists(path)) return fallback;
                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return fallback;
                return JsonSerializer.Deserialize<T>(text, _json) ?? fallback;
            }
        }
        catch (Exception ex)
        {
            // Keep the unreadable file: the caller falls back to defaults and the next
            // Save would overwrite it, which silently destroys the user's history.
            try
            {
                string path = Path.Combine(Dir, file);
                if (File.Exists(path)) File.Copy(path, path + ".bad", true);
            }
            catch { /* preservation is best effort */ }
            LoadFailure(file, ex);
            return fallback;
        }
    }

    public static void Save<T>(string file, T value)
    {
        try
        {
            string path = Path.Combine(Dir, file);
            string tmp = path + ".tmp";
            string text = JsonSerializer.Serialize(value, _json);
            lock (_lock)
            {
                File.WriteAllText(tmp, text);
                // Atomic replace: File.Copy over a live file could be interrupted and leave
                // a truncated schedules/limits/usage file behind.
                File.Move(tmp, path, true);
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }
}

public class AppSettings
{
    public string Language { get; set; } = "fa";
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public List<string> FavoriteDnsIds { get; set; } = new();
    public string ActiveProfileId { get; set; } = "";
    public string MonitorTarget { get; set; } = "snapp.ir";

    /** One-shot flag: the monitor target has been moved off the old default. */
    public bool MonitorTargetMigrated { get; set; }
    public int BenchmarkRounds { get; set; } = 4;
}

public static class SettingsService
{
    private static AppSettings _current;
    private static readonly object _lock = new();

    public static AppSettings Current
    {
        get
        {
            if (_current == null)
            {
                lock (_lock)
                {
                    _current ??= Storage.Load("settings.json", new AppSettings());
                    if (!_current.MonitorTargetMigrated)
                    {
                        // Every build until now shipped "1.1.1.1", which answers in ~105 ms
                        // from here while snapp.ir answers in ~24 ms. Only a value that is
                        // still the old default (or empty) is moved; the flag is persisted so
                        // a target the user picks later is never taken away again.
                        if (string.IsNullOrWhiteSpace(_current.MonitorTarget)
                            || _current.MonitorTarget.Trim() == "1.1.1.1")
                            _current.MonitorTarget = "snapp.ir";
                        _current.MonitorTargetMigrated = true;
                        Storage.Save("settings.json", _current);
                    }
                }
            }
            return _current;
        }
    }

    public static void Save()
    {
        lock (_lock) Storage.Save("settings.json", Current);
    }

    public static void SaveLanguage(string lang)
    {
        Current.Language = lang;
        Save();
    }
}
