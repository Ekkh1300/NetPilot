using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>Adapter discovery (Ethernet / Wi-Fi / Virtual / VPN) + connection counting.</summary>
public static class AdapterService
{
    public static async Task<List<AdapterInfo>> GetAdaptersAsync()
    {
        const string script = @"
Get-NetAdapter | Sort-Object -Property Status, Name | ForEach-Object {
  $i = $_.ifIndex
  $ip = Get-NetIPConfiguration -InterfaceIndex $i -ErrorAction SilentlyContinue
  $v4 = @(); $gw = ''
  if ($ip) {
    $v4 = @($ip.IPv4Address.IPAddress)
    $gw = (@($ip.IPv4DefaultGateway.NextHop) -join ',')
  }
  $dns = @((Get-DnsClientServerAddress -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
  $dhcp = (Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue).Dhcp
  # LinkSpeed arrives as '1 Gbps' / '100 Mbps' / '10 Gbps'. Stripping the unit left the page
  # showing a 10 GbE NIC as '10 Mbps' - a factor of 1000, silently. Normalise to Mbps here.
  $raw = [string]$_.LinkSpeed
  $num = 0.0
  if ($raw -match '([0-9]+(?:\.[0-9]+)?)\s*([KMG]?)bps') {
    $num = [double]$Matches[1]
    $num *= switch ($Matches[2].ToUpperInvariant()) { 'G' { 1000 } 'K' { 0.001 } default { 1 } }
  }
  $speed = [long][Math]::Round($num)
  [pscustomobject]@{
    IfIndex=[int]$i; Name=$_.Name; Description=$_.InterfaceDescription; Mac=$_.MacAddress
    Status=[string]$_.Status; Ipv4=($v4 -join ','); Gateway=$gw; Dns=($dns -join ',')
    LinkSpeed=[long]$speed; IsDhcp=($dhcp -eq 'Enabled')
  }
} | ConvertTo-Json -Compress";
        var (ok, output) = await Sys.PsStdoutAsync(script, 25000);
        var list = new List<AdapterInfo>();
        if (!ok || string.IsNullOrWhiteSpace(output)) return list;
        if (!Sys.TryExtractJson(output, out var json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : new[] { root }.AsEnumerable();
            foreach (var it in items)
            {
                string desc = it.TryGetProperty("Description", out var d) ? d.GetString() : "";
                string status = it.TryGetProperty("Status", out var s) ? s.GetString() : "";
                var info = new AdapterInfo
                {
                    IfIndex = it.GetProperty("IfIndex").GetInt32(),
                    Name = it.TryGetProperty("Name", out var n) ? n.GetString() : "",
                    Description = desc,
                    Mac = it.TryGetProperty("Mac", out var m) ? m.GetString() : "",
                    Status = status,
                    IsUp = string.Equals(status, "Up", StringComparison.OrdinalIgnoreCase),
                    Ipv4 = SplitStr(it, "Ipv4"),
                    Gateway = it.TryGetProperty("Gateway", out var g) ? g.GetString() : "",
                    DnsServers = SplitStr(it, "Dns").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    LinkSpeedMbps = it.TryGetProperty("LinkSpeed", out var ls) ? ls.GetInt64() : 0,
                    IsDhcp = it.TryGetProperty("IsDhcp", out var dh) && dh.ValueKind == JsonValueKind.True,
                    Kind = Classify(desc + " " + (it.TryGetProperty("Name", out var nn) ? nn.GetString() : "")),
                };
                list.Add(info);
            }
        }
        catch (Exception ex) { App.LogCrash(ex); }
        return list;
    }

    private static string SplitStr(JsonElement it, string prop) =>
        it.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static AdapterKind Classify(string text)
    {
        string t = (text ?? "").ToLowerInvariant();
        if (t.Contains("vpn") || t.Contains("tap-windows") || t.Contains("wireguard") || t.Contains("wintun")) return AdapterKind.Vpn;
        if (t.Contains("virtual") || t.Contains("hyper-v") || t.Contains("vmware") || t.Contains("vbox") ||
            t.Contains("loopback") || t.Contains("bluetooth") || t.Contains("kernel") || t.Contains("npcap")) return AdapterKind.Virtual;
        if (t.Contains("wi-fi") || t.Contains("wifi") || t.Contains("wireless") || t.Contains("802.11")) return AdapterKind.Wifi;
        if (t.Contains("ethernet") || t.Contains("gigabit") || t.Contains("realtek") || t.Contains("intel") || t.Contains("killer")) return AdapterKind.Ethernet;
        return AdapterKind.Other;
    }

    public static async Task<(bool ok, string msg)> SetEnabledAsync(string name, bool enable)
    {
        string cmd = enable
            ? $"Enable-NetAdapter -Name '{name.Replace("'", "''")}' -Confirm:$false"
            : $"Disable-NetAdapter -Name '{name.Replace("'", "''")}' -Confirm:$false";
        var (ok, output) = await Sys.PsAsync(cmd, 20000);
        return (ok, output);
    }

    // ------------- native per-PID connection counts (no PowerShell overhead) -------------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const int UDP_TABLE_OWNER_PID = 1;

    /// <summary>PID -> number of open TCP/UDP endpoints. Cheap enough to run every 2 seconds.</summary>
    public static Dictionary<int, int> GetConnectionPidCounts()
    {
        var counts = new Dictionary<int, int>();
        try
        {
            CountTcp(counts);
            CountUdp(counts);
        }
        catch { }
        return counts;
    }

    private static void CountTcp(Dictionary<int, int> counts)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0) return;
            int rows = Marshal.ReadInt32(buf);
            IntPtr rowPtr = IntPtr.Add(buf, 4);
            int rowSize = 24; // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
            for (int i = 0; i < rows; i++)
            {
                int pid = Marshal.ReadInt32(rowPtr, 20);
                if (pid > 0) counts[pid] = counts.TryGetValue(pid, out var c) ? c + 1 : 1;
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void CountUdp(Dictionary<int, int> counts)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0) != 0) return;
            int rows = Marshal.ReadInt32(buf);
            IntPtr rowPtr = IntPtr.Add(buf, 4);
            int rowSize = 12; // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid
            for (int i = 0; i < rows; i++)
            {
                int pid = Marshal.ReadInt32(rowPtr, 8);
                if (pid > 0) counts[pid] = counts.TryGetValue(pid, out var c) ? c + 1 : 1;
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
