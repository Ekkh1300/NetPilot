using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>System-tray icon with a quick DNS switcher menu (works without opening the window).</summary>
public sealed class TrayService
{
    public static readonly TrayService Instance = new();
    private TrayService() { }

    private NotifyIcon _icon;
    private ToolStripMenuItem _statusItem;
    private ToolStripMenuItem _dnsRoot;
    private ToolStripMenuItem _restoreItem;
    private ToolStripMenuItem _openItem;
    private ToolStripMenuItem _exitItem;

    public event Action<DnsEntry> QuickApplyRequested;
    public event Action RestoreRequested;
    public event Action OpenRequested;
    public event Action ExitRequested;

    public void Init()
    {
        try
        {
            var menu = new ContextMenuStrip();
            menu.BackColor = Color.FromArgb(24, 28, 40);
            menu.ForeColor = Color.White;
            menu.ShowImageMargin = false;
            menu.Font = new Font("Segoe UI", 9.5f);

            _statusItem = new ToolStripMenuItem("DNS: …") { Enabled = false, ForeColor = Color.FromArgb(150, 160, 180) };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());

            _dnsRoot = new ToolStripMenuItem(Lang.Lang.Instance["tray_quick_dns"]);
            menu.Items.Add(_dnsRoot);

            _restoreItem = new ToolStripMenuItem(Lang.Lang.Instance["tray_restore"]);
            _restoreItem.Click += (_, _) => RestoreRequested?.Invoke();
            menu.Items.Add(_restoreItem);

            menu.Items.Add(new ToolStripSeparator());
            _openItem = new ToolStripMenuItem(Lang.Lang.Instance["tray_open"]);
            _openItem.Click += (_, _) => OpenRequested?.Invoke();
            menu.Items.Add(_openItem);

            _exitItem = new ToolStripMenuItem(Lang.Lang.Instance["tray_exit"]);
            _exitItem.Click += (_, _) => ExitRequested?.Invoke();
            menu.Items.Add(_exitItem);

            // The tray menu sits outside the WPF tree, so no binding reaches it: it
            // re-reads the localized strings itself whenever the language switches.
            Lang.Lang.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is null or "" or "Item[]") ApplyLanguage();
            };
            ApplyLanguage();

            menu.Opening += (_, _) => _ = RefreshMenuAsync(menu);

            _icon = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "NetPilot",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        }
        catch (Exception ex) { App.LogCrash(ex); }
    }

    public void Dispose()
    {
        if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; }
    }

    /// <summary>Re-reads the localized captions after a UI language switch. The tray menu
    /// is WinForms, so no WPF binding ever reaches these items.</summary>
    private void ApplyLanguage()
    {
        try
        {
            if (_dnsRoot != null) _dnsRoot.Text = Lang.Lang.Instance["tray_quick_dns"];
            if (_restoreItem != null) _restoreItem.Text = Lang.Lang.Instance["tray_restore"];
            if (_openItem != null) _openItem.Text = Lang.Lang.Instance["tray_open"];
            if (_exitItem != null) _exitItem.Text = Lang.Lang.Instance["tray_exit"];
            _ = RefreshStatusAsync();
        }
        catch { }
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            string desc = await DnsService.DescribeCurrentAsync();
            if (_statusItem != null)
                _statusItem.Text = Lang.Lang.Instance["tray_current_dns"] + ": " + desc;
            if (_icon != null)
                _icon.Text = Trunc($"NetPilot — {desc}", 63);
        }
        catch { }
    }

    private async Task RefreshMenuAsync(ToolStrip menu)
    {
        try
        {
            await RefreshStatusAsync();
            _dnsRoot.DropDownItems.Clear();
            foreach (var entry in DnsService.GetAll())
            {
                var item = new ToolStripMenuItem($"{entry.Name}   ({entry.ServersSummary})");
                var e = entry;
                item.Click += (_, _) => QuickApplyRequested?.Invoke(e);
                _dnsRoot.DropDownItems.Add(item);
            }
        }
        catch { }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Tray icon. Prefers the icon the exe itself carries (Assets\NetPilot.ico, set
    /// through ApplicationIcon) so the tray, the taskbar and the shortcut are the same mark;
    /// falls back to drawing it when the exe icon cannot be read.</summary>
    private static Icon MakeIcon()
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var fromExe = Icon.ExtractAssociatedIcon(exe);
                if (fromExe != null) return fromExe;
            }
        }
        catch { /* fall through to the drawn icon */ }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
                Color.FromArgb(56, 189, 248), Color.FromArgb(129, 140, 248), 45f);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var pen = new Pen(Color.FromArgb(255, 255, 255, 255), 2f);
            g.DrawEllipse(pen, 3, 3, 26, 26);
            using var font = new Font("Segoe UI Semibold", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("N", font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
        }
        IntPtr hIcon = bmp.GetHicon();
        Icon icon;
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            icon = (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);   // GetHicon owns this handle; we cloned it.
        }
        return icon;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
