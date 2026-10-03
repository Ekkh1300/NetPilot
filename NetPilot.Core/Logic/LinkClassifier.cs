using System;
using System.Net;
using System.Net.Sockets;

namespace NetPilot.Services;

/// <summary>
/// Decides what an interface *is*, from nothing but its name and description.
///
/// This used to live inside MobileVpnService as a pile of <c>if (t.Contains("..."))</c> lines
/// written by looking at one machine's adapter list. It is now shared, because the same
/// question has to be answered identically on Windows, Linux and macOS - and because the
/// alternative was three copies that quietly disagree, which is how a HotspotShield adapter
/// ends up advertised to the user as "the phone".
///
/// The rules below are matched against the interface name and description, so they have to
/// work for all three naming conventions:
///   Windows  "Ethernet 2", "Local Area Connection* 12", "Android USB Device"
///   Linux    enp0s3, wlan0, usb0, enx001122334455, tun0, wg0, tailscale0, br-abc123
///   macOS    en0, en5, utun3, awdl0, bridge100, lo0
/// </summary>
public static class LinkClassifier
{
    /// <summary>
    /// True when an interface holds at least one real IPv4 address.
    ///
    /// This is what separates a live tunnel from a virtual network card that the driver merely
    /// reports as connected. APIPA (169.254.0.0/16) is self-assigned when DHCP fails, so it is
    /// the signature of an adapter that is present but has no network behind it - on Windows
    /// *and* on Linux, which assigns link-local addresses the same way.
    /// </summary>
    public static bool HasUsableIpv4(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) return false;
        foreach (var part in list.Split(','))
        {
            string ip = part.Trim();
            if (ip.Length == 0) continue;
            if (ip == "0.0.0.0" || ip.StartsWith("127.")) continue;
            if (ip.StartsWith("169.254.")) continue;
            if (IPAddress.TryParse(ip, out var parsed) &&
                parsed.AddressFamily == AddressFamily.InterNetwork)
                return true;
        }
        return false;
    }

    /// <summary>Classifies an interface as one of the supported transfer methods.</summary>
    public static string ClassifyLink(string description, string name, bool isVpn)
    {
        if (isVpn) return "vpn";
        // Trimmed: when a platform has no description for an interface (Linux and macOS do
        // not) the combined string would begin with a space and every StartsWith rule below
        // would silently fail - which is how utun0/tun0/wg0 stopped being recognised as
        // tunnels the moment this was shared.
        string t = $"{description} {name}".Trim().ToLowerInvariant();

        // A phone on USB. Windows names come from the RNDIS driver; Linux names the
        // interface usb0/enx<mac>; macOS does not expose USB tethering as an interface at
        // all (the phone gets an IP on en0 and the Mac treats it as plain Ethernet), which is
        // why "lan" is a correct answer on macOS rather than a fallback.
        if (t.Contains("rndis") || t.Contains("remote ndis") || t.Contains("usb ncm") ||
            t.Contains("tether") || t.Contains("android") || t.Contains("honor") ||
            t.Contains("huawei") || t.Contains("xiaomi") || t.Contains("oppo") ||
            t.Contains("vivo") || t.Contains("oneplus") || t.Contains("realme") ||
            t.Contains("google nexus") || t.Contains("pixel") || t.Contains("mtp") ||
            t.Contains("functionfs") || t.Contains("cdc ether") || t.Contains("pdanet") ||
            t.Contains("broadband") || t.Contains("cdc ecm") || t.Contains("quectel") ||
            t.Contains("novatel") || t.Contains("sierra wireless") || t.Contains("zte") ||
            StartsWith(t, "usb") || Contains(t, "enx") || Contains(t, "cdc_") ||
            Contains(t, "iphone") || Contains(t, "ipad"))
            return "usb";

        // A phone acting as a Wi-Fi access point. Windows exposes it as "Local Area
        // Connection* 12"; Linux/macOS leave it looking like an ordinary Wi-Fi interface,
        // so this can only be confirmed by asking the system - which is what the per-platform
        // backends do before calling in here with isVpn=false.
        if (t.Contains("wi-fi direct") || t.Contains("hosted network") ||
            t.Contains("microsoft wi-fi direct") || t.Contains("mobile hotspot") ||
            t.Contains("wireless display") || StartsWith(t, "ap"))
            return "hotspot";

        if (t.Contains("loopback") || t.Contains("virtual") || t.Contains("hyper-v") ||
            t.Contains("vmware") || t.Contains("vbox") || t.Contains("kernel") ||
            t.Contains("npcap") || t.Contains("bluetooth") || t.Contains("bridge") ||
            t.Contains("loop") || StartsWith(t, "lo") || StartsWith(t, "docker") ||
            StartsWith(t, "veth") || StartsWith(t, "virbr") || Contains(t, "awdl") ||
            Contains(t, "llw"))
            return "other";

        return "lan";
    }

    /// <summary>Adapters that are tunnels rather than a path to the phone (WAN Miniport,
    /// OpenVPN, WireGuard, Tailscale, <c>utun</c>, <c>wg0</c>, …). The shared classifier
    /// already covers most of them; this catches the names it does not know about.</summary>
    public static bool IsVpnLike(string description, string name)
    {
        // Trimmed for the same reason as in ClassifyLink: an empty description would otherwise
        // leave a leading space and defeat every prefix rule.
        string t = $"{description} {name}".Trim().ToLowerInvariant();
        if (t.Contains("wan miniport") || t.Contains("openvpn") || t.Contains("wireguard") ||
            t.Contains("wintun") || t.Contains("tailscale") || t.Contains("tap-windows") ||
            t.Contains("tun/") || t.Contains("ikev2") || t.Contains("pptp") ||
            t.Contains("l2tp") || t.Contains("sstp") ||
            // Linux and macOS tunnel interfaces.
            StartsWith(t, "utun") || StartsWith(t, "wg") || StartsWith(t, "tun") ||
            StartsWith(t, "ppp") || StartsWith(t, "zt") || StartsWith(t, "nordlynx") ||
            Contains(t, "ipsec") || Contains(t, "gvisor") || Contains(t, "sing-tun"))
            return true;

        // Commercial VPN adapters. These must be caught before ClassifyLink, which
        // would otherwise see "hotspot" inside "HotspotShield" and advertise the
        // adapter as a phone link.
        if (t.Contains("hotspotshield") || t.Contains("hotspot shield") ||
            t.Contains("nordvpn") || t.Contains("protonvpn") || t.Contains("expressvpn") ||
            t.Contains("windscribe") || t.Contains("mullvad") || t.Contains("surfshark") ||
            t.Contains("anyconnect") || t.Contains("forticlient") || t.Contains("globalprotect") ||
            t.Contains("zscaler") || t.Contains("checkpoint") || t.Contains("softether"))
            return true;

        // TUN-style proxy / client tunnels (Happ Tunnel, Clash, Xray, sing-box, ...).
        // They describe themselves as plain adapters, so nothing else catches them:
        // without this they would be offered as "the phone" in the link list and the
        // traffic tile would keep sampling an almost idle tunnel - reading 0 B/s.
        return t.Contains("happ") || t.Contains("tunnel") || t.Contains("xray") ||
               t.Contains("v2ray") || t.Contains("sing-box") ||
               t.Contains("singbox") || t.Contains("clash") || t.Contains("hysteria") ||
               t.Contains("shadowsocks") || t.Contains("trojan") || t.Contains("outline") ||
               t.Contains("zerotier") || t.Contains("hamachi") || t.Contains("gost ");
    }

    /// <summary>
    /// Prefix test that tolerates the trailing index every Unix interface name carries
    /// (<c>utun3</c>, <c>wg0</c>, <c>ap0</c>) but stops before a longer word, so "loopback"
    /// is not the loopback rule matching and "adapter0" is not an access point.
    /// </summary>
    private static bool StartsWith(string haystack, string prefix)
    {
        if (!haystack.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return haystack.Length == prefix.Length || !char.IsLetter(haystack[prefix.Length]);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);
}