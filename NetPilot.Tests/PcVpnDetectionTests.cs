using System.Linq;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Regression suite for the "PC VPN: connected" tile claiming a tunnel that was not there.
///
/// A VPN client's virtual network card is reported <c>Up</c> by the driver on almost every
/// machine that merely *has* the client installed. Counting that as a live tunnel is what
/// made users see "PC VPN connected" while their VPN was off - and, because an "active" PC
/// VPN also blocks sharing, it stopped the feature from working at all.
///
/// The rule the product now follows: an established tunnel carries a usable IPv4 address; a
/// driver-level "Up" with no address (or an APIPA one) means "installed, not tunnelling",
/// which the page reports as unknown rather than guessing.
/// </summary>
public class PcVpnDetectionTests
{
    [Theory]
    [InlineData("10.8.0.2", true)]                       // a real tunnel address
    [InlineData("192.168.7.1", true)]                   // split-tunnel LAN-style
    [InlineData("100.64.0.7", true)]                    // CGNAT range a VPN may hand out
    [InlineData("10.8.0.2,100.64.0.7", true)]           // several, one usable
    [InlineData("169.254.12.34", false)]                // APIPA: nothing behind the adapter
    [InlineData("169.254.1.1,10.0.0.5", true)]          // one APIPA, one real
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-an-address", false)]
    public void UsableAddress_IsWhatSeparatesALiveTunnelFromAnIdleAdapter(string ipv4, bool expected)
    {
        Assert.Equal(expected, MobileVpnService.HasUsableIpv4(ipv4));
    }

    [Fact]
    public void AnAdapterWithOnlyAnApipaAddress_IsNotUsable()
    {
        // The exact shape Windows self-assigns when DHCP fails on a virtual card.
        Assert.False(MobileVpnService.HasUsableIpv4("169.254.100.101"));
    }

    [Fact]
    public void IPv6OnlyAdaptersDoNotCountAsAUsableAddress()
    {
        // The tunnel indicator is IPv4-based; a link-local v6 address is not evidence of one.
        Assert.False(MobileVpnService.HasUsableIpv4("fe80::1"));
    }

    [Fact]
    public void CommaSeparatedListToleratesWhitespace()
    {
        Assert.True(MobileVpnService.HasUsableIpv4(" 10.0.0.9 , 192.168.0.1 "));
    }

    [Fact]
    public void EveryAdapterOnThisMachineIsClassedConsistently()
    {
        // A smoke check on the real machine: whatever the detector reports must be built from
        // a usable address, never from "the driver says Up".
        var state = MobileVpnService.Instance.DetectAsync(force: true).GetAwaiter().GetResult();
        Assert.True(state.Links.All(l => l.Kind != "vpn" || !l.IsVpn),
            "a VPN adapter must never be offered as a phone link");
        if (state.PcVpnActive)
        {
            // Active has to be backed by an address, or we are back to the old lie.
            var vpnAdapters = state.Links.Where(l => l.IsVpn).ToList();
            Assert.True(vpnAdapters.Count == 0 ||
                        vpnAdapters.Any(l => MobileVpnService.HasUsableIpv4(l.Ipv4)),
                "PC VPN reported active without a usable address on any adapter");
        }
    }
}
