using System.Collections.Generic;
using System.Linq;
using NetPilot.Models;
using NetPilot.Services;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Which adapters a bonded download is allowed to use.
///
/// The rules here are unusual in that getting one wrong does not cause an error anywhere. It
/// causes a download that is slower than a single connection, with a progress bar that looks
/// healthy, a log full of ordinary entries, and no message explaining it - which is a far worse
/// outcome than a crash and the reason each rule is tested rather than trusted.
/// </summary>
public class BondEligibilityTests
{
    private static AdapterInfo Adapter(
        string name, string ipv4 = "192.168.1.20", bool up = true,
        AdapterKind kind = AdapterKind.Ethernet, string gateway = "192.168.1.1")
        => new() { Name = name, Ipv4 = ipv4, IsUp = up, Kind = kind, Gateway = gateway };

    // ---------------- what may be bonded ----------------

    [Fact]
    public void AnAdapterThatIsUpWithAnAddressOfItsOwnIsBondable() =>
        Assert.True(BondEligibility.IsBondable(Adapter("Ethernet")));

    [Theory]
    [InlineData(AdapterKind.Wifi)]
    [InlineData(AdapterKind.Ethernet)]
    public void EitherKindOfRealConnectionIsBondable(AdapterKind kind) =>
        Assert.True(BondEligibility.IsBondable(Adapter("conn", kind: kind)));

    /// <summary>
    /// The rule that matters most, and the one a filter written by hand would most likely miss.
    ///
    /// A VPN adapter is up, has an address, and looks exactly like a second network from
    /// every angle except the physical one: its traffic already travels down a physical adapter.
    /// Bonding it counts the same cable twice, so the download gets slower and no part of the
    /// system reports a fault.
    /// </summary>
    [Theory]
    [InlineData(AdapterKind.Vpn)]
    [InlineData(AdapterKind.Virtual)]
    [InlineData(AdapterKind.Other)]
    public void SomethingThatIsNotItsOwnPathIsNeverBondable(AdapterKind kind) =>
        Assert.False(BondEligibility.IsBondable(Adapter("tunnel", kind: kind)),
                     $"{kind} has no path of its own, so it must not be counted as one");

    [Fact]
    public void AnAdapterThatIsDownIsNotBondable() =>
        Assert.False(BondEligibility.IsBondable(Adapter("Ethernet", up: false)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("")]
    [InlineData("169.254.10.20")]     // APIPA: present, and behind no network at all
    public void AnAdapterWithNoUsableAddressIsNotBondable(string ipv4) =>
        Assert.False(BondEligibility.IsBondable(Adapter("Ethernet", ipv4: ipv4)),
                     $"'{ipv4}' looks like a network but is not one");

    [Fact]
    public void NothingIsBondableWhenThereIsNothing() =>
        Assert.False(BondEligibility.IsBondable(null));

    // ---------------- why the others were left out ----------------

    [Fact]
    public void EveryAdapterThatWasNotOfferedIsListedWithAReason()
    {
        var all = new List<AdapterInfo>
        {
            Adapter("Ethernet"),
            Adapter("Wi-Fi", kind: AdapterKind.Wifi),
            Adapter("WireGuard", kind: AdapterKind.Vpn),
            Adapter("vEthernet", kind: AdapterKind.Virtual),
            Adapter("Old NIC", up: false),
            Adapter("No DHCP", ipv4: "169.254.3.4"),
        };

        var skipped = BondEligibility.Skipped(all);

        Assert.Equal(4, skipped.Count);
        Assert.DoesNotContain(skipped, s => s.Name == "Ethernet");
        Assert.DoesNotContain(skipped, s => s.Name == "Wi-Fi");
        Assert.Equal("tunnel", skipped.Single(s => s.Name == "WireGuard").Reason);
        Assert.Equal("virtual", skipped.Single(s => s.Name == "vEthernet").Reason);
        Assert.Equal("down", skipped.Single(s => s.Name == "Old NIC").Reason);
        Assert.Equal("no address of its own", skipped.Single(s => s.Name == "No DHCP").Reason);
    }

    [Fact]
    public void TheSkippedListIsSafeWhenThereIsNothingToSkip() =>
        Assert.Empty(BondEligibility.Skipped(null));

    // ---------------- the physics ----------------

    [Fact]
    public void TwoAdaptersBehindOneRouterAreOneConnection()
    {
        // The whole physics limit, in one list: Wi-Fi and Ethernet into the same router are one
        // uplink, so asking for both gains nothing and saying nothing about it would be a lie
        // told by omission.
        var shared = BondEligibility.SharedUplink(new[]
        {
            Adapter("Wi-Fi",      kind: AdapterKind.Wifi,   gateway: "192.168.1.1"),
            Adapter("Ethernet",                              gateway: "192.168.1.1"),
        });

        Assert.Equal(2, shared.Count);
        Assert.Contains("Wi-Fi", shared);
        Assert.Contains("Ethernet", shared);
    }

    [Fact]
    public void AdaptersOnDifferentGatewaysAreGenuinelyDifferentConnections()
    {
        var shared = BondEligibility.SharedUplink(new[]
        {
            Adapter("Wi-Fi",     kind: AdapterKind.Wifi, gateway: "192.168.1.1"),
            Adapter("USB modem",                         gateway: "10.0.0.1"),
        });

        Assert.Empty(shared);
    }

    [Fact]
    public void AnAdapterWithNoGatewayIsNotAssumedToShareAnything() =>
        Assert.Empty(BondEligibility.SharedUplink(new[]
        {
            Adapter("Ethernet", gateway: ""),
            Adapter("Wi-Fi", kind: AdapterKind.Wifi, gateway: null),
        }));

    [Fact]
    public void TheGatewayComparisonIsNotCaseOrSpaceSensitive()
    {
        var shared = BondEligibility.SharedUplink(new[]
        {
            Adapter("Ethernet", gateway: "192.168.1.1"),
            Adapter("Ethernet 2", gateway: " 192.168.1.1 "),
        });

        Assert.Equal(2, shared.Count);
    }

    [Fact]
    public void NothingIsAssumedWhenNothingIsSelected() =>
        Assert.Empty(BondEligibility.SharedUplink(null));

    // ---------------- Windows turning Wi-Fi off ----------------

    /// <summary>
    /// The most common reason a bonded download gains nothing on Windows, and the one that
    /// looks exactly like the feature being broken: plugging in Ethernet makes Windows disable
    /// the Wi-Fi adapter, so one of the two paths silently disappears.
    /// </summary>
    [Fact]
    public void WifiIsReportedAsNeedingRestoreWhenEthernetIsUpAndWifiWentDown()
    {
        var all = new[]
        {
            Adapter("Ethernet", gateway: "10.0.0.1"),
            Adapter("Wi-Fi", kind: AdapterKind.Wifi, up: false, gateway: "192.168.1.1"),
        };

        Assert.True(BondEligibility.NeedsWifiRestore(all, new[] { "Ethernet" }));
    }

    [Fact]
    public void WifiThatIsStillUpDoesNotNeedRestoring()
    {
        var all = new[]
        {
            Adapter("Ethernet", gateway: "10.0.0.1"),
            Adapter("Wi-Fi", kind: AdapterKind.Wifi, gateway: "192.168.1.1"),
        };

        Assert.False(BondEligibility.NeedsWifiRestore(all, new[] { "Ethernet" }));
    }

    [Fact]
    public void NothingIsReportedWhenNoAdapterIsSelected()
    {
        var all = new[] { Adapter("Wi-Fi", kind: AdapterKind.Wifi, up: false) };

        Assert.False(BondEligibility.NeedsWifiRestore(all, new string[0]));
    }

    [Fact]
    public void NothingIsReportedWhenNothingIsUp()
    {
        var all = new[]
        {
            Adapter("Ethernet", up: false),
            Adapter("Wi-Fi", kind: AdapterKind.Wifi, up: false),
        };

        Assert.False(BondEligibility.NeedsWifiRestore(all, new[] { "Ethernet" }),
                     "a machine with nothing connected has not had its Wi-Fi turned off");
    }

    [Fact]
    public void NothingIsReportedWhenThereIsNoMachineAtAll() =>
        Assert.False(BondEligibility.NeedsWifiRestore(null, new[] { "Ethernet" }));
}
