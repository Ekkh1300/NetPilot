using System;
using System.Collections.Generic;
using System.Linq;

namespace NetPilot.Services;

// The phone-tunnel contract. Lives in Core because the Android phone, the Windows UI and the
// macOS/Linux daemon all speak it, and a change to the wire format has to land in all three at
// once. The namespace is deliberately the historical NetPilot.Services so the Windows app keeps
// compiling without touching a single using statement.

// ReSharper disable once InconsistentNaming
/// <summary>How the phone link is carried.</summary>
public enum MvMethod
{
    Auto,
    UsbTethering,
    WifiHotspot,
    DirectLan,
    Proxy,
}

/// <summary>A network interface that could carry the phone's connection.</summary>
/// <remarks>
/// Declared with properties rather than the public fields it started as. Two reasons, both
/// found the hard way: XAML data binding cannot see a field, so the Avalonia build rendered
/// every adapter as a blank tile; and <c>System.Text.Json</c> ignores fields unless
/// <c>IncludeFields</c> is set, so <c>/status</c> once returned an array of empty objects.
/// Properties bind and serialise without either option, and the Windows build's object
/// initialisers keep working unchanged.
/// </remarks>
public sealed class MvLink
{
    public int IfIndex { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>usb | hotspot | lan | vpn | other</summary>
    public string Kind { get; set; } = "other";

    public bool IsUp { get; set; }

    public string Ipv4 { get; set; } = "";

    public string Gateway { get; set; } = "";

    public long Rx { get; set; }

    public long Tx { get; set; }

    public int Metric { get; set; }

    /// <summary>Adapter class according to the shared classifier (Vpn / Wifi / Ethernet / …).</summary>
    public bool IsVpn { get; set; }

    /// <summary>Localized kind label; kept out of the service so it stays UI-free.</summary>
    public string KindKey => Kind is "usb" or "hotspot" or "lan" ? $"mv_link_kind_{Kind}" : "mv_link_kind_other";
}

/// <summary>Everything the Mobile VPN page shows, produced by a single background probe.</summary>
public sealed class MvState
{
    public List<MvLink> Links { get; set; } = new();
    public bool MobileConnected { get; set; }
    /// <summary>true = the phone reports an active VPN, false = reported inactive, null = unknown.</summary>
    public bool? MobileVpnActive { get; set; }

    public bool PcVpnActive { get; set; }

    /// <summary>A VPN-looking adapter is up but carries no usable address: a VPN client is
    /// installed and not tunnelling. Not "active" (that blocks sharing for no reason) and not
    /// confidently "inactive" either, so the page says unknown instead of guessing.</summary>
    public bool PcVpnUnknown { get; set; }

    public bool PcConnected { get; set; }

    public int LocalPeers { get; set; }

    public long RateDown { get; set; }

    public long RateUp { get; set; }

    /// <summary>True when no rate could be derived from this probe (first sample, the
    /// link changed identity, or no link is up) - the caller should take a second
    /// sample rather than believe the zero it just read.</summary>
    public bool WasBaseline { get; set; }

    public bool Sharing { get; set; }

    public DateTime TakenAt { get; set; }

    public MvLink UsbLink => Links.FirstOrDefault(l => l.Kind == "usb" && l.IsUp);
    public MvLink HotspotLink => Links.FirstOrDefault(l => l.Kind == "hotspot" && l.IsUp);
    public MvLink AnyUpLink =>
        UsbLink ?? HotspotLink ?? Links.FirstOrDefault(l => l.IsUp && l.Kind == "lan");
}

/// <summary>Properties, not fields: a XAML binding cannot see a field, and the UI binds this
/// directly to show why an operation was refused.</summary>
public sealed class MvResult
{
    public bool Ok { get; set; }

    /// <summary>Localization key, so the caller decides the language.</summary>
    public string Key { get; set; } = "";

    public string Detail { get; set; } = "";

    public static MvResult Success(string key = "mv_done") => new() { Ok = true, Key = key };

    public static MvResult Fail(string key, string detail = "") => new() { Ok = false, Key = key, Detail = detail };
}