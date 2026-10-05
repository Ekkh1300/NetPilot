using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetPilot;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContext = new NetPilot.ViewModels.MainViewModel();

        // A borderless window maximizes over the taskbar; clamp it to the work area
        // so the taskbar stays reachable (single-monitor friendly).
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;

        StartSelfTestIfNeeded();
    }

    // ---------------------------------------------------------------------
    // Visual smoke test: `NetPilot.exe --selftest` walks every nav item on a
    // timer and writes the current page key to a marker file, so an external
    // harness (which cannot inject input into an elevated window) knows when
    // to capture a screenshot.
    // ---------------------------------------------------------------------
    /// <summary>
    /// Where the page walk reports which page it is on, and which page to park on.
    ///
    /// Resolved from the data directory. These were absolute paths into the source tree, which
    /// means the self-test only worked when run from a specific folder on a specific machine, and
    /// wrote into the checkout rather than anywhere a build owns - which is also why a CI checkout
    /// could not use them.
    /// </summary>
    private static string SelfTestMarker => Path.Combine(App.DataDirectory(), "selftest.txt");
    private static string SelfTestHoldMarker => Path.Combine(App.DataDirectory(), "selftest-hold.txt");

    /// <summary>Page key the walk should keep re-capturing, or "" for a normal run.</summary>
    private string _holdKey = "";

    private void StartSelfTestIfNeeded()
    {
        if (!Array.Exists(Environment.GetCommandLineArgs(), a =>
                string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase))) return;

        if (DataContext is not NetPilot.ViewModels.MainViewModel vm || vm.Nav.Count == 0) return;

        // "echo mobile_vpn > selftest-hold.txt" parks the walk on that page and keeps
        // re-rendering it, so a harness that cannot click an elevated window can still
        // watch a page change underneath it (a phone pairing in, a state flipping).
        try
        {
            if (File.Exists(SelfTestHoldMarker))
                _holdKey = (File.ReadAllText(SelfTestHoldMarker) ?? "").Trim();
        }
        catch { }

        int i = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };

        timer.Tick += (_, _) =>
        {
            if (i >= vm.Nav.Count)
            {
                if (_holdKey.Length > 0)
                {
                    // Parked: keep the held page selected and re-render it so a harness can
                    // watch it change (a phone pairing in, a probe turning green).
                    var held = vm.Nav.FirstOrDefault(n => n.Key == _holdKey);
                    if (held != null)
                    {
                        held.IsSelected = true;
                        CaptureSelfTestFrame(_holdKey);
                        try { File.WriteAllText(SelfTestMarker, _holdKey); } catch { }
                        return;
                    }
                }
                timer.Stop();
                // Page walk done -> prove a *live* language switch repaints the window.
                _ = RunLanguagePhaseAsync(vm);
                return;
            }

            var key = vm.Nav[i].Key;
            vm.Nav[i].IsSelected = true;   // Switch() runs synchronously from here

            // Screenshot from *inside* the process: an external harness cannot do this
            // reliably. SetWindowPos toward an elevated window is blocked by UIPI, so a
            // non-admin capturer ends up copying a stale composed frame instead of the
            // page that is actually on screen. RenderTargetBitmap reads our own visual
            // tree, so it is always the real state.
            var shot = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(900)   // after the 0.35s page transition
            };
            shot.Tick += (_, _) =>
            {
                shot.Stop();
                CaptureSelfTestFrame(key);
                try { File.WriteAllText(SelfTestMarker, key); } catch { }
            };
            shot.Start();
            i++;
        };

        timer.Start();
    }

    /// <summary>Renders the window into a PNG next to the self-test marker.</summary>
    private void CaptureSelfTestFrame(string key)
    {
        try
        {
            UpdateLayout();

            int w = (int)Math.Ceiling(ActualWidth);
            int h = (int)Math.Ceiling(ActualHeight);
            if (w < 10 || h < 10) return;

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));

            var dir = Path.GetDirectoryName(SelfTestMarker) ?? ".";
            using (var fs = File.Create(Path.Combine(dir, "st-" + key + ".png")))
                enc.Save(fs);
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    private void WriteMarker(string text)
    {
        try { File.WriteAllText(SelfTestMarker, text); } catch { }
    }

    /// <summary>
    /// Language phase of the smoke test: flip the UI language while the window is up,
    /// capture the very same page before and after, and assert the on-screen text really
    /// changed. A page walk alone cannot catch a broken switch - a page that is rebuilt
    /// from scratch reads the new language anyway - so this checks a page that stays
    /// alive, which is exactly the case a wrong PropertyChanged name would break.
    /// Result goes to langcheck.txt for the external harness.
    /// </summary>
    private async System.Threading.Tasks.Task RunLanguagePhaseAsync(NetPilot.ViewModels.MainViewModel vm)
    {
        string checkFile = Path.Combine(Path.GetDirectoryName(SelfTestMarker) ?? ".", "langcheck.txt");
        string original = NetPilot.Lang.Lang.Instance.Language;
        string flipped = original == "fa" ? "en" : "fa";
        string oldWord = NetPilot.Lang.Lang.Instance["settings"];

        try
        {
            vm.Switch("settings");
            await System.Threading.Tasks.Task.Delay(1200);
            CaptureSelfTestFrame("lang_before_" + original);
            WriteMarker("lang_before_" + original);
            var before = CollectVisibleText();

            NetPilot.Lang.Lang.Instance.SetLanguage(flipped);
            await System.Threading.Tasks.Task.Delay(1200);
            var after = CollectVisibleText();
            string newWord = NetPilot.Lang.Lang.Instance["settings"];
            CaptureSelfTestFrame("lang_after_" + flipped);
            WriteMarker("lang_after_" + flipped);

            bool beforeHasOld = before.Contains(oldWord);
            bool staleOld = after.Contains(oldWord);   // old-language header still visible
            bool hasNew = after.Contains(newWord);     // new-language header took over

            File.WriteAllText(checkFile,
                $"original={original}\nflipped={flipped}\n" +
                $"oldWord={oldWord}\nnewWord={newWord}\n" +
                $"beforeHasOld={(beforeHasOld ? 1 : 0)}\n" +
                $"afterHasOld={(staleOld ? 1 : 0)}\n" +
                $"afterHasNew={(hasNew ? 1 : 0)}\n" +
                $"ok={(beforeHasOld && hasNew && !staleOld ? 1 : 0)}\n");

            // Leave the app in the language it was launched with.
            NetPilot.Lang.Lang.Instance.SetLanguage(original);
            await System.Threading.Tasks.Task.Delay(700);
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            try { File.WriteAllText(checkFile, "exception=" + ex.Message); } catch { }
        }
        finally
        {
            try { File.Delete(SelfTestMarker); } catch { }
        }
    }

    /// <summary>Every non-empty string currently rendered (text blocks and string contents).</summary>
    private System.Collections.Generic.HashSet<string> CollectVisibleText()
    {
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        void Walk(DependencyObject d)
        {
            if (d == null) return;
            if (d is System.Windows.Controls.TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                set.Add(tb.Text.Trim());
            if (d is System.Windows.Controls.ContentControl cc && cc.Content is string cs && !string.IsNullOrWhiteSpace(cs))
                set.Add(cs.Trim());
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }

        Walk(this);
        return set;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { /* drag interrupted (mouse released / alt-tab) */ }
        }
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (MaxButton == null) return;
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    /// <summary>Closing the window hides it to the tray (when enabled) instead of
    /// quitting; a real exit goes through <see cref="App.ExitApp"/>.</summary>
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;

        e.Cancel = true;

        if (NetPilot.Services.SettingsService.Current.MinimizeToTray)
            Hide();
        else
            Dispatcher.BeginInvoke(new Action(App.ExitApp));
    }
}
