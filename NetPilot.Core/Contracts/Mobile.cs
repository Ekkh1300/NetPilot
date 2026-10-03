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
public sealed class MvLink
{
    public int IfIndex;
    public string Name = "";
    public string Description = "";
    /// <summary>usb | hotspot | lan | vpn | other</summary>
    public string Kind = "other";
    public bool IsUp;
    public string Ipv4 = "";
    public string Gateway = "";
    public long Rx;
    public long Tx;
    public int Metric;
    /// <summary>Adapter class according to the shared classifier (Vpn / Wifi / Ethernet / …).</summary>
    public bool IsVpn;

    /// <summary>Localized kind label; kept out of the service so it stays UI-free.</summary>
    public string KindKey => Kind is "usb" or "hotspot" or "lan" ? $"mv_link_kind_{Kind}" : "mv_link_kind_other";
}

/// <summary>Everything the Mobile VPN page shows, produced by a single background probe.</summary>
public sealed class MvState
{
    public List<MvLink> Links = new();
    public bool MobileConnected;
    /// <summary>true = the phone reports an active VPN, false = reported inactive, null = unknown.</summary>
    public bool? MobileVpnActive;
    public bool PcVpnActive;
    /// <summary>A VPN-looking adapter is up but carries no usable address: a VPN client is
    /// installed and not tunnelling. Not "active" (that blocks sharing for no reason) and not
    /// confidently "inactive" either, so the page says unknown instead of guessing.</summary>
    public bool PcVpnUnknown;
    public bool PcConnected;
    public int LocalPeers;
    public long RateDown;
    public long RateUp;
    /// <summary>True when no rate could be derived from this probe (first sample, the
    /// link changed identity, or no link is up) - the caller should take a second
    /// sample rather than believe the zero it just read.</summary>
    public bool WasBaseline;
    public bool Sharing;
    public DateTime TakenAt;

    public MvLink UsbLink => Links.FirstOrDefault(l => l.Kind == "usb" && l.IsUp);
    public MvLink HotspotLink => Links.FirstOrDefault(l => l.Kind == "hotspot" && l.IsUp);
    public MvLink AnyUpLink =>
        UsbLink ?? HotspotLink ?? Links.FirstOrDefault(l => l.IsUp && l.Kind == "lan");
}

public sealed class MvResult
{
    public bool Ok;
    /// <summary>Localization key, so the caller decides the language.</summary>
    public string Key = "";
    public string Detail = "";

    public static MvResult Success(string key = "mv_done") => new() { Ok = true, Key = key };
    public static MvResult Fail(string key, string detail = "") => new() { Ok = false, Key = key, Detail = detail };
}