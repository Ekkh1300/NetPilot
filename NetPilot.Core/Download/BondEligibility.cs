using System;
using System.Collections.Generic;
using System.Linq;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>
/// Decides which of a machine's adapters a bonded download may use, and why the others are out.
///
/// This lives in Core rather than in the Windows app because the question is not a Windows
/// question. The rules are the same wherever they run, and putting them here means the same
/// answer on every platform instead of one that was written by looking at a single machine's
/// adapter list - which is how an unrelated virtual adapter ends up offered to the user as a
/// second connection.
///
/// Three of the rules below are the difference between a feature that adds speed and one that
/// quietly makes things worse, so each is stated with its reason rather than left as a filter:
///
///   A tunnel is never bondable, even though it is up and has an address. Its traffic already
///   travels down a physical adapter, so counting it as a second path counts the same cable
///   twice. Worse, if the tunnel is bound to the adapter it is being bonded with, a block sent
///   down one and the other arrives down the same physical link twice - the download gets
///   slower, and nothing on screen explains why.
///
///   An adapter with an APIPA address is out. 169.254.0.0/16 is self-assigned when DHCP fails,
///   so it is the signature of an adapter that is present and has no network behind it. It
///   looks up, it has an IP, and every request through it simply times out - which is the worst
///   possible failure in a downloader, because the shared queue will hand it blocks that
///   nobody else has claimed, and the download stalls at whatever those blocks were worth.
///
///   An adapter whose gateway is shared with another selected adapter counts as the same
///   connection. This is the physics limit: Wi-Fi and Ethernet plugged into one router are one
///   uplink wearing two hats, and the file arrives no faster. Telling the user is the honest
///   thing to do; letting them discover it by watching the progress bar is not.
/// </summary>
public static class BondEligibility
{
    /// <summary>Whether an adapter may be offered as one of the networks a download goes over.</summary>
    public static bool IsBondable(AdapterInfo adapter)
    {
        if (adapter == null) return false;
        if (!adapter.IsUp) return false;

        // A tunnel or a virtual switch has no path of its own; it is a picture of the path
        // something else already owns.
        if (adapter.Kind is AdapterKind.Vpn or AdapterKind.Virtual or AdapterKind.Other) return false;

        return LinkClassifier.HasUsableIpv4(adapter.Ipv4);
    }

    /// <summary>
    /// The adapters that are up and usable but still not bondable, each with a reason a person
    /// can act on rather than a code.
    /// </summary>
    public static IReadOnlyList<(string Name, string Reason)> Skipped(IReadOnlyList<AdapterInfo> all)
    {
        var skipped = new List<(string, string)>();
        foreach (var a in all ?? Array.Empty<AdapterInfo>())
        {
            if (IsBondable(a)) continue;

            string reason = !a.IsUp ? "down"
                          : a.Kind is AdapterKind.Vpn ? "tunnel"
                          : a.Kind is AdapterKind.Virtual ? "virtual"
                          : a.Kind is AdapterKind.Other ? "not a network path"
                          : !LinkClassifier.HasUsableIpv4(a.Ipv4) ? "no address of its own"
                          : "unavailable";
            skipped.Add((a.Name, reason));
        }
        return skipped;
    }

    /// <summary>
    /// The names of selected adapters that are all behind one gateway, and so are one
    /// connection rather than several.
    ///
    /// Windows will happily have Wi-Fi and Ethernet both connected to the same router, and the
    /// machine sees two networks. It is one internet connection. The file will not arrive any
    /// sooner for using both, and the only honest thing is to say so before the download
    /// starts rather than after it finishes at the same speed.
    /// </summary>
    public static IReadOnlyList<string> SharedUplink(IEnumerable<AdapterInfo> selected)
    {
        return (selected ?? Enumerable.Empty<AdapterInfo>())
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Gateway))
            .GroupBy(a => a.Gateway.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(a => a.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when the machine has a Wi-Fi adapter that is down while a selected adapter is up -
    /// which on Windows means Windows turned Wi-Fi off, not that the user did.
    /// </summary>
    /// <remarks>
    /// This happens the moment Ethernet is plugged in. Windows is configured to minimise the
    /// number of simultaneous connections, and Wi-Fi is what it drops. The adapter still exists,
    /// still reports its saved network, and is simply disabled - so a user who set up a bonded
    /// download on Wi-Fi plus a USB tether finds one of the two gone the instant the cable goes
    /// in, with no message about it.
    ///
    /// That is the single most common reason a bonded download does not gain speed on this
    /// platform, and it looks exactly like the feature being broken.
    /// </remarks>
    public static bool NeedsWifiRestore(IReadOnlyList<AdapterInfo> all, IEnumerable<string> selected)
    {
        if (all == null || selected == null) return false;

        bool hasUpSelected = all.Any(a => a != null && a.IsUp && selected.Contains(a.Name));

        return hasUpSelected
            && all.Any(a => a != null && a.Kind == AdapterKind.Wifi && !a.IsUp);
    }
}
