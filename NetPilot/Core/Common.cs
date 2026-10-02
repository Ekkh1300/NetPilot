using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetPilot.Core;

/// <summary>Base class implementing INotifyPropertyChanged with helper setters.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Raises the "everything changed" reset. WPF treats an empty property name as
    /// "re-read every property on this object", which is how computed localized text
    /// (mode text, kind labels, status labels ...) switches language without each object
    /// having to enumerate its own property names. Verified not to push anything back
    /// into TwoWay-bound properties: a reset re-reads the source, it never writes it.
    /// </summary>
    public void RefreshAll() => Raise(string.Empty);

    protected bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }
}

/// <summary>Reusable ICommand implementation.</summary>
public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object> _execute;
    private readonly Func<object, bool> _canExecute;

    public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool> canExecute = null)
        : this(_ => execute(), canExecute == null ? null : new Func<object, bool>(_ => canExecute())) { }

    public event EventHandler CanExecuteChanged;
    public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Async command that disables itself while running (prevents double clicks).</summary>
public class AsyncRelayCommand : System.Windows.Input.ICommand
{
    private readonly Func<object, System.Threading.Tasks.Task> _execute;
    private readonly Func<object, bool> _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object, System.Threading.Tasks.Task> execute, Func<object, bool> canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public AsyncRelayCommand(Func<System.Threading.Tasks.Task> execute, Func<bool> canExecute = null)
        : this(_ => execute(), canExecute == null ? null : new Func<object, bool>(_ => canExecute())) { }

    public event EventHandler CanExecuteChanged;
    public bool CanExecute(object parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object parameter)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        RaiseCanExecuteChanged();
        try { await _execute(parameter); }
        catch (Exception ex) { App.LogCrash(ex); }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

#region Converters

/// <summary>Inverts a boolean. Only for bool-typed targets (IsEnabled, ...):
/// see <see cref="BoolToVisibilityConverter"/> for anything that takes a
/// <see cref="Visibility"/>, which this cannot be cast into.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is bool b && !b;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is bool b && !b;
}

/// <summary>Shows or hides an element from a bool; Invert flips it, which is what
/// empty-state hints need ("show until the data exists").</summary>
/// <remarks>
/// WPF rejects a plain bool coming back from a converter on a Visibility property
/// (invalid cast Boolean -> Visibility), drops the value and leaves the element at its
/// default Visible. Binding a Visibility to <see cref="InverseBoolConverter"/> therefore
/// silently never hides anything - the "No backup captured yet." hint stayed on screen
/// even after a backup existed.
/// </remarks>
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        bool b = v is bool x && x;
        bool show = Invert ? !b : b;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Converts bytes/sec (or bytes) into a friendly string such as "1.4 MB/s".</summary>
public class SpeedFormatConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        double bps = v is double d ? d : v is long l ? l : 0;
        return FormatSpeed(bps);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int i = 0;
        while (bytesPerSecond >= 1024 && i < units.Length - 1) { bytesPerSecond /= 1024; i++; }
        return $"{bytesPerSecond:0.#} {units[i]}";
    }

    public static string FormatBytes(double bytes)
    {
        if (bytes < 1) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.##} {units[i]}";
    }
}

/// <summary>Shows the element when the value is null (or hides when NotNull=true).</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        bool isNull = v == null || (v is string s && string.IsNullOrWhiteSpace(s));
        bool visible = Invert ? !isNull : isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Turns a ProgressBar's (Value, Maximum) pair into a 0..1 scale factor,
/// so a template can grow its indicator with a ScaleTransform instead of relying
/// on theme-specific template parts.</summary>
public class ProgressFractionConverter : IMultiValueConverter
{
    public object Convert(object[] v, Type t, object p, CultureInfo c)
    {
        double value = v != null && v.Length > 0 && v[0] is double d ? d : 0;
        double max = v != null && v.Length > 1 && v[1] is double m ? m : 0;
        if (max <= 0) return 0.0;
        return Math.Clamp(value / max, 0, 1);
    }

    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when a numeric value is zero (empty-state hints); Invert flips it.</summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        double n = v is int i ? i : v is long l ? l : v is double d ? d : 0;
        bool show = Invert ? n != 0 : n == 0;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

#endregion

/// <summary>A selectable option whose label is localized but whose key is stable.
/// Used for combo boxes whose items must follow the current language.</summary>
public class ChoiceVm : ObservableObject
{
    public string Key { get; }

    private string _label;
    public string Label { get => _label; set => Set(ref _label, value); }

    public ChoiceVm(string key, string label)
    {
        Key = key;
        _label = label;
    }
}
