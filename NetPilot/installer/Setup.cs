// NetPilot setup - single-file installer, no third-party tooling.
//
// Built with the C# 5 compiler that ships inside Windows
// (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe), so it runs on any
// Windows 10/11 box without .NET or any runtime installed. The application payload
// is a zip embedded as a manifest resource (see build.ps1).
//
// Switches:
//   /SILENT          install with defaults, no UI (exit code 0 = success)
//   /DIR=<path>      install destination (with /SILENT)
//   /UNINSTALL       remove the installation registered in the registry
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NetPilotInstaller
{
    internal static class Program
    {
        public const string AppName = "NetPilot";
        public const string Version = "1.2.0";
        public const string Publisher = "esi";
        public const string PayloadResource = "netpilot.payload.zip";

        [STAThread]
        private static void Main(string[] args)
        {
            bool uninstall = false;
            bool silent = false;
            string dir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "/UNINSTALL", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                else if (string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (a.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(5).Trim('"');
                else if (string.Equals(a, "/DIR", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) dir = args[++i].Trim('"');
            }

            L.Init();

            if (uninstall)
            {
                Environment.Exit(Uninstall.Run(silent));
                return;
            }

            if (silent)
            {
                Environment.Exit(Install.Run(dir ?? DefaultDir(), true, true, null));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(dir));
        }

        public static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        }
    }

    // ------------------------------------------------------------------ strings
    internal static class L
    {
        public static bool IsFa;
        private static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]>();

        static L()
        {
            Add("title", "نصب NetPilot", "NetPilot Setup");
            Add("sub", "مدیریت و پایش شبکه", "Network monitoring and control");
            Add("creator", "سازنده", "Creator");
            Add("dest", "پوشهٔ نصب", "Install folder");
            Add("browse", "انتخاب…", "Browse…");
            Add("desktop", "میان‌بر روی دسکتاپ", "Desktop shortcut");
            Add("startmenu", "میان‌بر در منوی Start", "Start menu shortcut");
            Add("install", "نصب", "Install");
            Add("cancel", "انصراف", "Cancel");
            Add("finish", "پایان", "Finish");
            Add("ready", "برای شروع نصب، روی «نصب» بزنید.", "Click Install to begin.");
            Add("installing", "در حال نصب…", "Installing…");
            Add("extracting", "در حال استخراج فایل‌ها", "Extracting files");
            Add("done", "نصب با موفقیت انجام شد.", "Installation completed successfully.");
            Add("launch", "اجرای NetPilot", "Launch NetPilot");
            Add("close_app", "NetPilot در حال اجراست و بسته می‌شود. ادامه می‌دهید؟",
                "NetPilot is running and will be closed. Continue?");
            Add("app_desc", "مدیریت و پایش شبکه", "Network monitor and tools");
            Add("uninstall_done", "NetPilot از سیستم حذف شد.", "NetPilot has been removed from this computer.");
            Add("error", "خطا در نصب", "Setup error");
            Add("bad_dir", "محل نصب انتخاب نشده است.", "Please choose an install folder.");
            Add("admin_hint", "برای نصب در پوشهٔ سیستمی به دسترسی ادمین نیاز است.",
                "Installing to a system folder requires administrator rights.");
        }

        private static void Add(string key, string fa, string en) { T[key] = new string[] { fa, en }; }

        /// <summary>Selects the label language from the Windows UI culture.</summary>
        public static void Init()
        {
            G("title");   // forces the static table to load
            IsFa = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "fa";
        }

        public static string G(string key)
        {
            string[] v;
            if (T.TryGetValue(key, out v)) return IsFa ? v[0] : v[1];
            return key;
        }
    }

    // ------------------------------------------------------------------ shortcuts
    internal static class Shortcut
    {
        /// <summary>Creates a .lnk through the in-box WScript.Shell COM object.</summary>
        public static void Create(string lnkPath, string target, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new InvalidOperationException("WScript.Shell unavailable");
            object sh = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                    new object[] { Path.GetDirectoryName(target) });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally
            {
                Marshal.FinalReleaseComObject(sh);
            }
        }

        public static void Delete(string lnkPath)
        {
            try { if (File.Exists(lnkPath)) File.Delete(lnkPath); }
            catch { /* already gone */ }
        }
    }

    // ------------------------------------------------------------------ install
    internal static class Install
    {
        /// <summary>Writes a line into the uninstall manifest (files + shortcuts we created).</summary>
        private class Manifest
        {
            public readonly List<string> Lines = new List<string>();
            public void AddFile(string relative) { Lines.Add("file:" + relative); }
            public void AddLink(string full) { Lines.Add("shortcut:" + full); }
            public void Save(string dir) { System.IO.File.WriteAllText(Path.Combine(dir, "uninstall.dat"), string.Join(Environment.NewLine, Lines.ToArray())); }
        }

        public static int Run(string dest, bool desktop, bool menu, SetupForm ui)
        {
            try
            {
                if (string.IsNullOrEmpty(dest)) return 2;
                dest = dest.TrimEnd('\\');

                if (!CloseRunningApp(ui)) return 3;   // user declined to close the app
                Directory.CreateDirectory(dest);

                Manifest manifest = new Manifest();
                ExtractPayload(dest, ui, manifest);

                string exe = Path.Combine(dest, Program.AppName + ".exe");
                if (!File.Exists(exe)) throw new FileNotFoundException("payload did not contain " + Program.AppName + ".exe");

                // Shortcuts for every user (the installer is elevated).
                string commonDesktop = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Program.AppName + ".lnk");
                string commonMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs");
                string menuLink = Path.Combine(commonMenu, Program.AppName + ".lnk");

                if (desktop)
                {
                    Shortcut.Create(commonDesktop, exe, L.G("app_desc"));
                    manifest.AddLink(commonDesktop);
                }
                if (menu)
                {
                    Shortcut.Create(menuLink, exe, L.G("app_desc"));
                    manifest.AddLink(menuLink);
                }

                manifest.Save(dest);
                RegisterUninstall(dest, exe);
                OpenBridgePortInFirewall();
                return 0;
            }
            catch (Exception ex)
            {
                if (ui != null) ui.ShowError(ex.Message);
                else Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        /// <summary>
        /// Allows the paired phone to reach the bridge.
        ///
        /// The app listens on 8787 for the REST API and the phone's proxy listens on 8788.
        /// Windows blocks inbound connections to a new listener, so without these rules the
        /// phone reports "PC unreachable" against a bridge that is running perfectly - the
        /// single most confusing failure of this feature. The installer is elevated, so it can
        /// simply create them; the app re-creates them on start if a user removed them in
        /// between. A host with a locked-down policy keeps its own rules - this is best
        /// effort, never fatal.
        /// </summary>
        private static void OpenBridgePortInFirewall()
        {
            foreach (int port in new[] { 8787, 8788 })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "netsh.exe",
                        Arguments = "advfirewall firewall add rule name=\"" + Program.AppName +
                            " mobile bridge\" dir=in action=allow protocol=TCP localport=" + port +
                            " profile=any",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null) { p.WaitForExit(8000); p.Dispose(); }
                    }
                }
                catch { /* best effort */ }
            }
        }

        private static bool CloseRunningApp(SetupForm ui)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(Program.AppName); }
            catch { return true; }
            if (procs.Length == 0) return true;

            if (ui != null && !ui.Confirm(L.G("close_app")))
            {
                foreach (Process p in procs) p.Dispose();
                return false;
            }
            foreach (Process p in procs)
            {
                try { p.Kill(); }
                catch { /* already exiting */ }
                finally { p.Dispose(); }
            }
            Thread.Sleep(800);
            return true;
        }

        private static void ExtractPayload(string dest, SetupForm ui, Manifest manifest)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream raw = asm.GetManifestResourceStream(Program.PayloadResource);
            if (raw == null) throw new InvalidOperationException("embedded payload is missing");

            using (raw)
            using (ZipArchive zip = new ZipArchive(raw, ZipArchiveMode.Read))
            {
                List<ZipArchiveEntry> files = new List<ZipArchiveEntry>();
                foreach (ZipArchiveEntry e in zip.Entries)
                    if (e.FullName.Length > 0 && e.FullName[e.FullName.Length - 1] != '/')
                        files.Add(e);

                if (ui != null) ui.BeginProgress(files.Count);

                for (int i = 0; i < files.Count; i++)
                {
                    ZipArchiveEntry e = files[i];
                    string relative = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(dest, relative);
                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    // Overwrite: this is also how an upgrade replaces a previous install.
                    e.ExtractToFile(target, true);
                    manifest.AddFile(relative);

                    if (ui != null) ui.ReportProgress(i + 1, files.Count, Path.GetFileName(relative));
                }
            }
        }

        private static void RegisterUninstall(string dest, string exe)
        {
            try
            {
                using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                {
                    using (var sub = key.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.AppName))
                    {
                        if (sub == null) return;
                        sub.SetValue("DisplayName", Program.AppName);
                        sub.SetValue("DisplayVersion", Program.Version);
                        sub.SetValue("Publisher", Program.Publisher);
                        sub.SetValue("InstallLocation", dest);
                        sub.SetValue("DisplayIcon", exe);
                        sub.SetValue("UninstallString", "\"" + Assembly.GetExecutingAssembly().Location + "\" /UNINSTALL");
                        sub.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        sub.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        try
                        {
                            long kb = 0;
                            foreach (string f in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
                                kb += new FileInfo(f).Length;
                            sub.SetValue("EstimatedSize", (int)Math.Min(kb / 1024, int.MaxValue), Microsoft.Win32.RegistryValueKind.DWord);
                        }
                        catch { /* size is informational only */ }
                    }
                }
            }
            catch { /* programs-and-features entry is a convenience, not a requirement */ }
        }
    }

    // ------------------------------------------------------------------ uninstall
    internal static class Uninstall
    {
        public static int Run(bool silent)
        {
            try
            {
                string dest = ResolveInstallDir();
                if (string.IsNullOrEmpty(dest))
                {
                    if (!silent) MessageBox.Show(L.G("uninstall_done"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Process[] procs = Process.GetProcessesByName(Program.AppName);
                foreach (Process p in procs)
                {
                    try { p.Kill(); } catch { }
                    finally { p.Dispose(); }
                }
                if (procs.Length > 0) Thread.Sleep(800);

                RemoveShortcutsAndFiles(dest);

                // Registry entry (ours only).
                try
                {
                    using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                        key.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.AppName, false);
                }
                catch { }

                if (!silent)
                    MessageBox.Show(L.G("uninstall_done"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(ex.Message, L.G("error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string ResolveInstallDir()
        {
            try
            {
                using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var sub = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.AppName))
                {
                    if (sub != null)
                    {
                        object v = sub.GetValue("InstallLocation");
                        if (v != null) return v.ToString().TrimEnd('\\');
                    }
                }
            }
            catch { }

            // Fallback: the payload directory the setup itself was unpacked from is not
            // the install dir, so without registry data there is nothing safe to remove.
            return null;
        }

        private static void RemoveShortcutsAndFiles(string dest)
        {
            string manifestPath = Path.Combine(dest, "uninstall.dat");
            List<string> files = new List<string>();
            List<string> links = new List<string>();

            if (File.Exists(manifestPath))
            {
                foreach (string line in File.ReadAllLines(manifestPath))
                {
                    if (line.StartsWith("file:")) files.Add(line.Substring(5));
                    else if (line.StartsWith("shortcut:")) links.Add(line.Substring(9));
                }
            }

            foreach (string l in links) Shortcut.Delete(l);

            // Remove only what we installed, deepest paths first, then prune empty folders.
            files.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string rel in files)
            {
                try
                {
                    string full = Path.Combine(dest, rel);
                    if (File.Exists(full)) File.Delete(full);
                }
                catch { /* locked or already gone */ }
            }

            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); }
            catch { }

            // The inbound rules the installer added are ours to take away again.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "advfirewall firewall delete rule name=\"" + Program.AppName + " mobile bridge\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null) { p.WaitForExit(8000); p.Dispose(); }
                }
            }
            catch { /* best effort */ }

            PruneEmptyDirs(dest);
        }

        /// <summary>
        /// Removes the folders this install created. Deepest first, and only when a folder
        /// is completely empty, so nothing the user put in there is ever touched.
        /// </summary>
        private static void PruneEmptyDirs(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;

                // Nested folders need more than one pass: a child can only disappear
                // after its own children did.
                for (int pass = 0; pass < 6; pass++)
                {
                    string[] dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
                    Array.Sort(dirs, delegate (string a, string b) { return b.Length.CompareTo(a.Length); });

                    bool removed = false;
                    foreach (string d in dirs)
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(d).Length == 0)
                            {
                                Directory.Delete(d);
                                removed = true;
                            }
                        }
                        catch { /* locked or protected: keep it */ }
                    }
                    if (!removed) break;
                }

                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
                    Directory.Delete(root);
            }
            catch { /* leave what we cannot safely remove */ }
        }
    }

    // ------------------------------------------------------------------ UI
    internal sealed class SetupForm : Form
    {
        private readonly TextBox _dest;
        private readonly CheckBox _desktop;
        private readonly CheckBox _menu;
        private readonly CheckBox _launch;
        private readonly Button _primary;
        private readonly Button _cancel;
        private readonly Label _hint;
        private readonly Label _task;
        private readonly ProgressBar _bar;
        private bool _installed;

        public SetupForm(string initialDir)
        {
            Text = L.G("title");
            ClientSize = new Size(474, 336);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 0);
            BackColor = Color.White;
            if (L.IsFa) RightToLeft = RightToLeft.Yes;

            // Accent header
            var header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 86;
            header.BackColor = Color.FromArgb(24, 28, 40);
            Controls.Add(header);

            var glyph = new Label();
            glyph.Text = "N";
            glyph.Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold);
            glyph.ForeColor = Color.White;
            glyph.BackColor = Color.FromArgb(56, 189, 248);
            glyph.TextAlign = ContentAlignment.MiddleCenter;
            glyph.Size = new Size(46, 46);
            glyph.Location = new Point(24, 20);
            header.Controls.Add(glyph);

            var title = new Label();
            title.Text = L.G("title");
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.BackColor = Color.Transparent;
            title.AutoSize = true;
            title.Location = new Point(86, 22);
            header.Controls.Add(title);

            var sub = new Label();
            sub.Text = L.G("sub");
            sub.ForeColor = Color.FromArgb(150, 160, 180);
            sub.BackColor = Color.Transparent;
            sub.AutoSize = true;
            sub.Location = new Point(88, 54);
            header.Controls.Add(sub);

            var creator = new Label();
            creator.Text = L.G("creator") + ":  esi";
            creator.ForeColor = Color.FromArgb(56, 189, 248);
            creator.BackColor = Color.Transparent;
            creator.AutoSize = true;
            creator.Location = new Point(88, 71);
            header.Controls.Add(creator);

            var destLabel = new Label();
            destLabel.Text = L.G("dest");
            destLabel.AutoSize = true;
            destLabel.Location = new Point(24, 106);
            Controls.Add(destLabel);

            _dest = new TextBox();
            _dest.Location = new Point(24, 126);
            _dest.Size = new Size(344, 23);
            _dest.Text = initialDir ?? Program.DefaultDir();
            Controls.Add(_dest);

            var browse = new Button();
            browse.Text = L.G("browse");
            browse.Location = new Point(376, 125);
            browse.Size = new Size(74, 25);
            browse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = _dest.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) _dest.Text = dlg.SelectedPath;
                }
            };
            Controls.Add(browse);

            _desktop = new CheckBox();
            _desktop.Text = L.G("desktop");
            _desktop.Checked = true;
            _desktop.AutoSize = true;
            _desktop.Location = new Point(24, 162);
            Controls.Add(_desktop);

            _menu = new CheckBox();
            _menu.Text = L.G("startmenu");
            _menu.Checked = true;
            _menu.AutoSize = true;
            _menu.Location = new Point(24, 186);
            Controls.Add(_menu);

            _task = new Label();
            _task.Text = L.G("ready");
            _task.AutoSize = false;
            _task.Location = new Point(24, 222);
            _task.Size = new Size(426, 20);
            _task.ForeColor = Color.FromArgb(100, 116, 139);
            Controls.Add(_task);

            _bar = new ProgressBar();
            _bar.Location = new Point(24, 248);
            _bar.Size = new Size(426, 8);
            _bar.Visible = false;
            Controls.Add(_bar);

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.Location = new Point(24, 262);
            _hint.Size = new Size(426, 18);
            _hint.ForeColor = Color.FromArgb(148, 163, 184);
            _hint.Font = new Font("Segoe UI", 8F);
            Controls.Add(_hint);

            _launch = new CheckBox();
            _launch.Text = L.G("launch");
            _launch.Checked = true;
            _launch.AutoSize = true;
            _launch.Location = new Point(24, 286);
            _launch.Visible = false;
            Controls.Add(_launch);

            _cancel = new Button();
            _cancel.Text = L.G("cancel");
            _cancel.Size = new Size(92, 30);
            _cancel.Location = new Point(358, 290);
            _cancel.Click += delegate { Close(); };
            Controls.Add(_cancel);

            _primary = new Button();
            _primary.Text = L.G("install");
            _primary.Size = new Size(92, 30);
            _primary.Location = new Point(258, 290);
            _primary.BackColor = Color.FromArgb(56, 189, 248);
            _primary.ForeColor = Color.White;
            _primary.FlatStyle = FlatStyle.Flat;
            _primary.FlatAppearance.BorderSize = 0;
            _primary.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _primary.Click += delegate { OnPrimary(); };
            Controls.Add(_primary);

            AcceptButton = _primary;
            CancelButton = _cancel;
        }

        private void OnPrimary()
        {
            if (_installed)
            {
                if (_launch.Checked)
                {
                    try
                    {
                        string exe = Path.Combine(_dest.Text.TrimEnd('\\'), Program.AppName + ".exe");
                        if (File.Exists(exe))
                        {
                            var psi = new ProcessStartInfo(exe);
                            Process.Start(psi);
                        }
                    }
                    catch { /* launching is optional */ }
                }
                Close();
                return;
            }

            string dest = _dest.Text.Trim();
            if (dest.Length == 0) { MessageBox.Show(this, L.G("bad_dir"), L.G("error"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            _dest.Enabled = false;
            _desktop.Enabled = false;
            _menu.Enabled = false;
            _cancel.Enabled = false;
            _primary.Enabled = false;
            _task.Text = L.G("installing");

            string d = dest;
            bool desk = _desktop.Checked;
            bool sm = _menu.Checked;

            var th = new Thread(delegate()
            {
                int rc = Install.Run(d, desk, sm, this);
                try
                {
                    BeginInvoke(new Action<int>(Finish), rc);
                }
                catch { /* form already closed */ }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void Finish(int rc)
        {
            if (rc != 3)
            {
                // 3 = the user declined to close the running app; nothing failed, so the
                // form simply goes back to its ready state without an error message.
                if (rc != 0)
                {
                    _dest.Enabled = true;
                    _desktop.Enabled = true;
                    _menu.Enabled = true;
                    _cancel.Enabled = true;
                    _primary.Enabled = true;
                    _bar.Visible = false;
                    _task.Text = L.G("error");
                    return;
                }
            }
            else
            {
                _dest.Enabled = true;
                _desktop.Enabled = true;
                _menu.Enabled = true;
                _cancel.Enabled = true;
                _primary.Enabled = true;
                _bar.Visible = false;
                _task.Text = L.G("ready");
                return;
            }

            _installed = true;
            _bar.Visible = false;
            _task.Text = L.G("done");
            _hint.Text = "";
            _launch.Visible = true;
            _primary.Text = L.G("finish");
            _primary.Enabled = true;
            _cancel.Enabled = false;
        }

        // Called from the worker thread.
        public void BeginProgress(int total)
        {
            RunOnUi(delegate
            {
                _bar.Visible = true;
                _bar.Minimum = 0;
                _bar.Maximum = Math.Max(total, 1);
                _bar.Value = 0;
            });
        }

        public void ReportProgress(int done, int total, string name)
        {
            RunOnUi(delegate
            {
                if (done <= _bar.Maximum) _bar.Value = done;
                _task.Text = L.G("extracting") + "  (" + done + " / " + total + ")";
                _hint.Text = name;
            });
        }

        public bool Confirm(string message)
        {
            if (InvokeRequired)
            {
                bool ok = false;
                Invoke((Action)delegate
                {
                    ok = MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                });
                return ok;
            }
            return MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public void ShowError(string message)
        {
            RunOnUi(delegate
            {
                MessageBox.Show(this, message, L.G("error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
        }

        private void RunOnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); }
                catch { /* form already closed */ }
                return;
            }
            action();
        }
    }
}
