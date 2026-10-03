using System;
using System.Collections.Generic;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The classifier moved out of MobileVpnService into NetPilot.Core so Windows, Linux and macOS
/// all answer "what is this interface?" the same way. These tests exist because the rules were
/// originally written by staring at one machine's adapter list, and the Linux/macOS names were
/// added afterwards - which is exactly the situation where a regression goes unnoticed.
/// </summary>
public class CrossPlatformLinkClassifierTests
{
    // ---------------- VPN must never be offered as the phone ----------------

    [Theory]
    [InlineData("WireGuard Tunnel", "WireGuard", true)]
    [InlineData("TAP-Windows Adapter V9", "Ethernet 3", true)]
    [InlineData("HotspotShield", "Ethernet", true)]
    [InlineData("Hotspot Shield", "Ethernet", true)]
    [InlineData("NordVPN", "Ethernet", true)]
    [InlineData("AnyConnect VPN", "Ethernet", true)]
    public void WindowsTunnels_AreRecognised(string desc, string name, bool expected) =>
        Assert.Equal(expected, LinkClassifier.IsVpnLike(desc, name));

    [Theory]
    [InlineData("utun3", true)]
    [InlineData("utun0", true)]
    [InlineData("wg0", true)]
    [InlineData("tun0", true)]
    [InlineData("tailscale0", true)]
    [InlineData("ppp0", true)]
    [InlineData("nordlynx", true)]
    public void LinuxAndMacTunnelInterfaces_AreRecognised(string name, bool expected) =>
        Assert.Equal(expected, LinkClassifier.IsVpnLike("", name));

    [Theory]
    [InlineData("en0")]
    [InlineData("wlan0")]
    [InlineData("eth0")]
    [InlineData("lo")]
    [InlineData("lo0")]
    [InlineData("docker0")]
    [InlineData("awdl0")]
    public void OrdinaryInterfaces_AreNotTunnels(string name) =>
        Assert.False(LinkClassifier.IsVpnLike("", name));

    /// <summary>The bug this whole function exists for: "HotspotShield" contains "hotspot",
    /// so a naive check offered the user's commercial VPN client as "the phone".</summary>
    [Fact]
    public void AVpnClientIsNeverClassifiedAsThePhone()
    {
        bool isVpn = LinkClassifier.IsVpnLike("HotspotShield", "Ethernet");
        Assert.True(isVpn);
        Assert.Equal("vpn", LinkClassifier.ClassifyLink("HotspotShield", "Ethernet", isVpn));
    }

    // ---------------- phone link kinds ----------------

    [Theory]
    [InlineData("Android USB Device", "Ethernet", "usb")]
    [InlineData("Remote NDIS Based Internet Share", "Local Area Connection* 12", "usb")]
    [InlineData("usb0", "usb0", "usb")]
    [InlineData("enx001122334455", "enx001122334455", "usb")]
    public void UsbPhoneLinks_AreRecognised(string desc, string name, string expected) =>
        Assert.Equal(expected, LinkClassifier.ClassifyLink(desc, name, false));

    [Theory]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", "Local Area Connection* 12", "hotspot")]
    [InlineData("ap0", "ap0", "hotspot")]
    public void HotspotLinks_AreRecognised(string desc, string name, string expected) =>
        Assert.Equal(expected, LinkClassifier.ClassifyLink(desc, name, false));

    [Theory]
    [InlineData("lo", "other")]
    [InlineData("lo0", "other")]
    [InlineData("veth1234", "other")]
    [InlineData("docker0", "other")]
    [InlineData("bridge100", "other")]
    public void LoopbackAndVirtualLinks_AreNotOfferedAsThePhone(string name, string expected) =>
        Assert.Equal(expected, LinkClassifier.ClassifyLink(name, name, false));

    [Theory]
    [InlineData("Ethernet", "Ethernet", "lan")]
    [InlineData("Wi-Fi", "wlan0", "lan")]
    [InlineData("en0", "en0", "lan")]
    public void PlainInterfaces_AreLan(string desc, string name, string expected) =>
        Assert.Equal(expected, LinkClassifier.ClassifyLink(desc, name, false));

    /// <summary>The word-boundary guard, stated precisely.
    ///
    /// The first case is a correction worth keeping: my first version of this test asserted
    /// that "Local Area Connection" classifies as "other". That expectation was wrong - it is
    /// a LAN connection. What matters is only that it is not caught by the "lo" loopback rule
    /// (which would hide a real LAN from the phone link list). The second case is the real
    /// Windows hotspot: same name, but the *description* identifies it, which is exactly why
    /// both fields are passed in.</summary>
    [Theory]
    [InlineData("Local Area Connection", "Local Area Connection", "lan")]     // not caught by the "lo" rule
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", "Local Area Connection* 12", "hotspot")]
    [InlineData("Ethernet", "adapter0", "lan")]                               // "ap" must not match "adapter"
    public void PrefixRulesRespectWordBoundaries(string desc, string name, string expected) =>
        Assert.Equal(expected, LinkClassifier.ClassifyLink(desc, name, false));

    // ---------------- usable address ----------------

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.8.0.2,100.64.0.7", true)]
    [InlineData("10.8.0.2,169.254.12.34", true)]   // one real address is enough
    [InlineData("169.254.12.34", false)]           // APIPA: no network behind it
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void UsableAddress_SeparatesALiveTunnelFromAnIdleAdapter(string ipv4, bool expected) =>
        Assert.Equal(expected, LinkClassifier.HasUsableIpv4(ipv4));

    [Fact]
    public void AnIPv6OnlyInterfaceIsNotUsable()
    {
        // The Android tunnel is IPv4-only, so an adapter holding just a v6 address is not
        // something we can route the phone's traffic over.
        Assert.False(LinkClassifier.HasUsableIpv4("fe80::1%en0"));
    }
}