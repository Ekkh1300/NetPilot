using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using NetPilot.Core;
using NetPilot.Core.Logging;
using NetPilot.Models;
using NetPilot.Services;
using L = NetPilot.Lang.Lang;

namespace NetPilot.ViewModels;

/// <summary>One network offered to a download, as the row the user sees.</summary>
public class BondNetworkRow : ObservableObject
{
    public string Name { get; init; } = "";
    public string Ipv4 { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public string Glyph { get; init; } = "\uE701";
    public IPAddress Address { get; init; }

    /// <summary>Set instead of <see cref="Address"/> when this path is the paired phone's HTTP
    /// proxy rather than a local adapter - the download dials its streams into that proxy and
    /// everything arrives through the phone's tunnel.</summary>
    public string ProxyHost { get; init; }
    public int ProxyPort { get; init; }
    public bool IsPhonePath => ProxyHost != null;

    private int _streams = BondNetwork.DefaultStreams;
    /// <summary>
    /// How many connections to run over this network.
    ///
    /// Not the number of blocks, and not a duplicate of them: one TCP connection frequently
    /// cannot fill a fast link on its own, so a real download runs several. The blocks are the
    /// work units; these are how many are in flight at once.
    /// </summary>
    public int Streams
    {
        get => _streams;
        set => Set(ref _streams, Math.Clamp(value, 1, 64));
    }

    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The BondNetwork this row describes - an adapter address, or the phone proxy.</summary>
    /// <exception cref="ArgumentException">A proxy endpoint that is not an address.</exception>
    public BondNetwork ToNetwork()
        => IsPhonePath
            ? BondNetwork.ViaProxy(Name, ProxyHost, ProxyPort, Streams)
            : new BondNetwork(Name, Address, Streams);
}

/// <summary>How much of one download a particular network actually carried.</summary>
public class NetworkShare : ObservableObject
{
    public string Name { get; init; } = "";

    private long _bytes;
    public long Bytes { get => _bytes; private set => Set(ref _bytes, value); }

    private double _fraction;
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    public string SizeText => DownloadRow.Bytes(_bytes);

    /// <summary>Raised here rather than by the caller because <c>Raise</c> is protected, and a
    /// caller outside this class cannot refresh a computed property it just changed.</summary>
    public void SetProgress(long bytes, double fraction)
    {
        Bytes = bytes;
        Fraction = fraction;
    }
}

/// <summary>One download in the list.</summary>
public class DownloadRow : ObservableObject
{
    public string Url { get; init; } = "";

    private string _fileName = "";
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    public CancellationTokenSource Cancel { get; } = new();
    public ObservableCollection<NetworkShare> Shares { get; } = new();

    private long _totalBytes;
    public long TotalBytes
    {
        get => _totalBytes;
        set { if (Set(ref _totalBytes, value)) { Raise(nameof(SizeText)); Raise(nameof(Fraction)); Raise(nameof(Percent)); } }
    }

    private long _bytesDone;
    public long BytesDone
    {
        get => _bytesDone;
        set { if (Set(ref _bytesDone, value)) { Raise(nameof(SizeText)); Raise(nameof(Fraction)); Raise(nameof(Percent)); } }
    }

    public double Fraction => TotalBytes <= 0 ? 0 : (double)BytesDone / TotalBytes;

    /// <summary>For the progress bar, which is scaled 0..100 rather than 0..1.</summary>
    public double Percent => Fraction * 100;

    public string SizeText => TotalBytes <= 0 ? "" : $"{Bytes(BytesDone)} / {Bytes(TotalBytes)}";

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }

    private string _stateText = "";
    public string StateText { get => _stateText; set => Set(ref _stateText, value); }

    private string _error = "";
    public string Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    private bool _isActive = true;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public RelayCommand CancelCommand { get; }

    public DownloadRow() => CancelCommand = new RelayCommand(() => Cancel.Cancel(), () => _isActive);

    /// <summary>Byte count to a size a person can read. Here rather than in the view so it stays
    /// identical wherever it is shown and can be tested.</summary>
    public static string Bytes(long b) => b switch
    {
        < 0 => "0 B",
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>
/// The multi-connection download page.
/// </summary>
/// <remarks>
/// This page downloads files. It does not change where other applications' traffic goes, does
/// not install anything, and needs no cooperation from the operating system: the speed comes
/// from opening the sockets itself and binding each one to a network it is allowed to use.
/// </remarks>
public class BondViewModel : PageVmBase
{
    public ObservableCollection<BondNetworkRow> Networks { get; } = new();
    public ObservableCollection<DownloadRow> Downloads { get; } = new();

    private string _url = "";
    public string Url { get => _url; set { if (Set(ref _url, value)) Raise(nameof(CanStart)); } }

    private string _folder = DefaultFolder();
    public string Folder { get => _folder; set { if (Set(ref _folder, value)) Raise(nameof(CanStart)); } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) { Raise(nameof(CanStart)); Raise(nameof(NotBusy)); } } }
    public bool NotBusy => !_isBusy;

    private string _notice = "";
    /// <summary>Something the user needs to know before starting, in one line.</summary>
    public string Notice { get => _notice; set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    private string _skipped = "";
    /// <summary>Adapters that are up but cannot be used, and why.</summary>
    public string Skipped { get => _skipped; set { if (Set(ref _skipped, value)) Raise(nameof(HasSkipped)); } }
    public bool HasSkipped => !string.IsNullOrEmpty(_skipped);

    private bool _needsWifiRestore;
    /// <summary>Windows turned Wi-Fi off when the cable went in. See <see cref="BondEligibility"/>.</summary>
    public bool NeedsWifiRestore { get => _needsWifiRestore; set => Set(ref _needsWifiRestore, value); }

    public bool CanStart => !string.IsNullOrWhiteSpace(_url) && !string.IsNullOrWhiteSpace(_folder) && !_isBusy;

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public AsyncRelayCommand RestoreWifiCommand { get; }
    public RelayCommand CancelAllCommand { get; }

    public BondViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        BrowseCommand = new RelayCommand(Browse);
        RestoreWifiCommand = new AsyncRelayCommand(RestoreWifiAsync);
        CancelAllCommand = new RelayCommand(CancelAll);
        _ = RefreshAsync();
    }

    public override void OnActivated() => _ = RefreshAsync();

    private static string DefaultFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ---------------- what is available ----------------

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var all = await AdapterService.GetAdaptersAsync();
            var chosen = new HashSet<string>(Networks.Where(n => n.IsSelected).Select(n => n.Name),
                                              StringComparer.OrdinalIgnoreCase);

            Networks.Clear();

            // The paired phone, when there is one, goes first: it is usually the path an
            // adapter cannot be - mobile data next to the home internet - and it needs no
            // eligibility test, because the pairing already proved the proxy answers.
            string phoneProxy = (MobileVpnService.Instance.PeerProxy ?? "").Trim();
            if (phoneProxy.Length > 0 && MobileVpnService.IsValidProxyAddress(phoneProxy))
            {
                var (phoneHost, phonePort) = MobileVpnService.SplitProxyAddress(phoneProxy);
                if (phoneHost.Length > 0 && int.TryParse(phonePort, out int phonePortNum) && phonePortNum > 0)
                {
                    Networks.Add(new BondNetworkRow
                    {
                        Name = L.Instance["bond_phone_name"],
                        Ipv4 = phoneHost + ":" + phonePortNum,
                        KindLabel = L.Instance["bond_phone_kind"],
                        Glyph = "\uE774",
                        ProxyHost = phoneHost,
                        ProxyPort = phonePortNum,
                        IsSelected = chosen.Count == 0 ||
                                     chosen.Contains(L.Instance["bond_phone_name"]),
                    });
                }
            }

            foreach (var a in all.Where(BondEligibility.IsBondable))
                Networks.Add(new BondNetworkRow
                {
                    Name = a.Name,
                    Ipv4 = a.Ipv4,
                    Glyph = a.KindGlyph,
                    KindLabel = $"{a.Kind} · {a.Ipv4}",
                    Address = IPAddress.TryParse(a.Ipv4, out var addr) ? addr : null,
                    // First refresh keeps everything on; later ones remember what was ticked,
                    // so re-scanning does not quietly turn a network off mid-session.
                    IsSelected = chosen.Count == 0 || chosen.Contains(a.Name),
                });

            Skipped = string.Join("\n", BondEligibility.Skipped(all).Select(s => $"{s.Name} — {s.Reason}"));

            var selected = all.Where(a => Networks.Any(n => n.Name == a.Name && n.IsSelected)).ToList();
            var shared = BondEligibility.SharedUplink(selected);
            NeedsWifiRestore = BondEligibility.NeedsWifiRestore(all, chosen);

            // The one thing worth saying out loud before a download starts: two adapters into
            // one router is one connection, and the file will not arrive sooner for using both.
            Notice = shared.Count > 0 ? $"{L.Instance["bond_shared_uplink"]}: {string.Join(", ", shared)}"
                   : NeedsWifiRestore ? L.Instance["bond_wifi_off"]
                   : "";
        }
        finally { IsBusy = false; }
    }

    private void Browse()
    {
        var dialog = new SaveFileDialog
        {
            FileName = GuessFileName(Url),
            Filter = L.Instance["bond_all_files"] + "|*.*",
            InitialDirectory = Directory.Exists(Folder) ? Folder : null,
            // Never overwrite what is already there: the engine picks a numbered name instead,
            // so browsing cannot destroy an earlier download of the same file.
            OverwritePrompt = false,
        };

        if (dialog.ShowDialog() != true) return;

        var dir = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrEmpty(dir)) Folder = dir;
        if (!string.IsNullOrWhiteSpace(dialog.FileName))
            Url = _url.Length > 0 ? _url : dialog.FileName;   // raise CanStart
    }

    /// <summary>The name a download will get when the server does not supply one.</summary>
    public static string GuessFileName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";

        // LocalPath rather than AbsolutePath, because AbsolutePath is the escaped form. A file
        // called "my file.zip" would otherwise be saved as "my%20file.zip", and one named in
        // Persian as a string of %D8%xx that nobody can read - the user picked the name and gets
        // something else back. GetFileName keeps only the last segment, so a name containing a
        // decoded separator cannot climb out of the folder either.
        return SafeFileName(Path.GetFileName(uri.LocalPath.TrimEnd('/')));
    }

    /// <summary>
    /// Makes a suggested name safe to actually write.
    ///
    /// An illegal character is not caught where it is produced; it surfaces much later as a
    /// download that failed with an unrelated-looking message, which is the worst place for it.
    /// </summary>
    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        // "." and ".." name directories, not files, and would land the download somewhere the
        // user did not choose. An empty result covers a name made only of illegal characters.
        if (cleaned.Length == 0 || cleaned is "." or "..") return "download";

        // Bounded because Windows refuses paths past 260 characters in total, and a very long
        // name is the part most likely to push one over that edge.
        return cleaned.Length > 180 ? cleaned[..180] : cleaned;
    }

    // ---------------- starting one ----------------

    private async Task StartAsync()
    {
        var selected = Networks.Where(n => n.IsSelected &&
                                          (n.Address != null || n.ProxyHost != null)).ToList();
        if (selected.Count == 0) { Notice = L.Instance["bond_pick_one"]; return; }

        var destination = Path.Combine(Folder, GuessFileName(Url));

        var row = new DownloadRow
        {
            Url = Url,
            FileName = GuessFileName(Url),
            StateText = L.Instance["bond_state_running"],
        };
        foreach (var n in selected)
            row.Shares.Add(new NetworkShare { Name = n.Name });
        Downloads.Insert(0, row);

        IsBusy = true;
        try
        {
            // Each row already knows its own shape: an adapter address to bind, or the phone
            // proxy to dial. Building them here, once, is the one place the check above and
            // the engine below have to agree.
            var networks = selected.Select(n => n.ToNetwork()).ToList();
            var download = new BondedDownload(Url, destination, networks);
            var speed = new SpeedMeter();

            download.Progress += p => OnProgress(row, speed, p);
            download.NetworkDropped += (name, why) => OnNetworkDropped(row, name, why);

            var remote = await Task.Run(() => download.RunAsync(row.Cancel.Token), row.Cancel.Token);

            // The file on disk is named from the URL, not from what the server suggested, so
            // that is what the row shows. Renaming the label to something the file is not
            // called would be a small lie that costs the user a minute of searching.
            row.BytesDone = row.TotalBytes;
            row.SpeedText = "";
            row.StateText = L.Instance["bond_state_done"];
            Log.Info("bond", $"finished {destination} over {remote.Size} bytes");
        }
        catch (OperationCanceledException)
        {
            row.StateText = L.Instance["bond_state_cancelled"];
            row.SpeedText = "";
        }
        catch (Exception ex)
        {
            // The message goes on the row, not into a dialog: a failed download is one item in a
            // list, and a modal box over the list would hide the others.
            row.Error = ex.Message;
            row.StateText = L.Instance["bond_state_failed"];
            Log.Error("bond", $"download failed: {Url}", ex);
        }
        finally
        {
            row.IsActive = false;
            row.CancelCommand.RaiseCanExecuteChanged();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Turns the engine's progress into something on screen.
    ///
    /// Raised on a thread-pool thread, so every property change is dispatched: a WPF binding
    /// touched from a background thread throws, and this would be the first place it happened.
    /// </summary>
    private void OnProgress(DownloadRow row, SpeedMeter speed, BondProgress p)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            row.TotalBytes = p.TotalBytes;
            row.BytesDone = p.BytesDone;
            row.SpeedText = speed.Sample(p.BytesDone);

            long totalPerNetwork = p.BytesPerNetwork.Values.Sum();
            foreach (var share in row.Shares)
            {
                p.BytesPerNetwork.TryGetValue(share.Name, out long bytes);
                share.SetProgress(bytes, totalPerNetwork > 0 ? (double)bytes / totalPerNetwork : 0);
            }
        });
    }

    private void OnNetworkDropped(DownloadRow row, string name, string why)
    {
        Application.Current?.Dispatcher.Invoke(() => Log.Warn("bond", $"{name} stopped: {why}"));
    }

    private void CancelAll()
    {
        foreach (var row in Downloads.Where(d => d.IsActive)) row.Cancel.Cancel();
    }

    private async Task RestoreWifiAsync()
    {
        try
        {
            var all = await AdapterService.GetAdaptersAsync();
            var wifi = all.FirstOrDefault(a => a.Kind == AdapterKind.Wifi && !a.IsUp);
            if (wifi == null) { NeedsWifiRestore = false; Notice = ""; return; }

            var (ok, msg) = await AdapterService.SetEnabledAsync(wifi.Name, true);
            if (!ok) { Notice = msg; return; }

            NeedsWifiRestore = false;
            await RefreshAsync();
        }
        catch (Exception ex) { Notice = ex.Message; Log.Error("bond", "could not restore Wi-Fi", ex); }
    }
}

/// <summary>A moving download speed, over the last couple of seconds.</summary>
internal sealed class SpeedMeter
{
    private readonly List<(long AtMs, long Bytes)> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public string Sample(long bytesDone)
    {
        lock (_samples)
        {
            _samples.Add((_clock.ElapsedMilliseconds, bytesDone));
            if (_samples.Count > 12) _samples.RemoveAt(0);

            // Two seconds is long enough to be steady and short enough to react. Anything
            // shorter reads the burst at the start of a block as if it were the real speed.
            var window = _samples.Where(s => _clock.ElapsedMilliseconds - s.AtMs <= 2000).ToList();
            if (window.Count < 2) return "";

            double seconds = (window[^1].AtMs - window[0].AtMs) / 1000.0;
            if (seconds <= 0) return "";

            double perSecond = (window[^1].Bytes - window[0].Bytes) / seconds;
            return perSecond <= 0 ? "" : $"{DownloadRow.Bytes((long)perSecond)}/s";
        }
    }
}
