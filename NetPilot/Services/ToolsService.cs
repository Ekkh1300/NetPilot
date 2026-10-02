using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using NetPilot.Models;

namespace NetPilot.Services;

/// <summary>Network tools: Flush DNS, Renew IP, Reset Network, Ping, Traceroute, NSLookup.</summary>
public static class ToolsService
{
    public static async Task<ToolResult> FlushDnsAsync()
    {
        bool ok = await Sys.FlushDnsCacheAsync();
        HistoryService.Add("flush_dns", "DNS Flushed", "");
        return new ToolResult { Title = "Flush DNS", Success = ok, Output = ok ? "DNS resolver cache flushed successfully." : "Flush completed with warnings." };
    }

    public static async Task<ToolResult> RenewIpAsync()
    {
        var sb = new StringBuilder();
        var (ok1, o1) = await Sys.PsAsync("ipconfig /release | Out-String", 60000);
        sb.AppendLine(o1);
        var (ok2, o2) = await Sys.PsAsync("ipconfig /renew | Out-String", 60000);
        sb.AppendLine(o2);
        bool ok = ok1 && ok2;
        HistoryService.Add("ip_renewed", "IP Renewed", ok ? "success" : "finished with errors");
        return new ToolResult { Title = "Renew IP", Success = ok, Output = sb.ToString().Trim() };
    }

    public static async Task<ToolResult> ResetNetworkAsync()
    {
        const string script = @"
netsh winsock reset | Out-String
netsh int ip reset | Out-String
netsh int ipv6 reset | Out-String
ipconfig /flushdns | Out-String
ipconfig /registerdns | Out-String";
        var (ok, output) = await Sys.PsAsync(script, 90000);
        HistoryService.Add("network_reset", "Network Reset", ok ? "success" : "finished with errors");
        return new ToolResult { Title = "Reset Network", Success = ok, Output = output + "\n\nRestart Windows to complete the reset." };
    }

    public static async Task<ToolResult> PingAsync(string target, int count = 4)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pinging {target} with 32 bytes of data:");
        sb.AppendLine();
        int sent = 0, recvd = 0;
        long min = long.MaxValue, max = 0, sum = 0;
        try
        {
            using var ping = new Ping();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(target, 2000);
                    sw.Stop();
                    sent++;
                    if (reply.Status == IPStatus.Success)
                    {
                        recvd++;
                        long t = reply.RoundtripTime;
                        min = Math.Min(min, t); max = Math.Max(max, t); sum += t;
                        sb.AppendLine($"Reply from {reply.Address}: bytes=32 time={t}ms TTL={reply.Options?.Ttl ?? 0}");
                    }
                    else sb.AppendLine($"Request timed out. ({reply.Status})");
                }
                catch (Exception ex) { sent++; sb.AppendLine($"Request failed: {ex.Message}"); }
                if (i < count - 1) await Task.Delay(700);
            }
        }
        catch (Exception ex) { sb.AppendLine($"Ping failed: {ex.Message}"); }

        sb.AppendLine();
        sb.AppendLine($"Packets: Sent = {sent}, Received = {recvd}, Lost = {sent - recvd} ({(sent > 0 ? 100.0 * (sent - recvd) / sent : 0):0} % loss)");
        if (recvd > 0)
            sb.AppendLine($"Approximate round trip times: minimum = {min}ms, maximum = {max}ms, average = {Math.Round((double)sum / recvd):0}ms");
        return new ToolResult { Title = $"Ping {target}", Success = recvd > 0, Output = sb.ToString() };
    }

    public static async Task<ToolResult> TracerouteAsync(string target, int maxHops = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tracing route to {target}");
        sb.AppendLine();
        bool reached = false;
        try
        {
            using var ping = new Ping();
            for (int ttl = 1; ttl <= maxHops && !reached; ttl++)
            {
                var opts = new PingOptions(ttl, true);
                string hopLine = $"{ttl,3}  ";
                var times = new List<long>();
                string addr = "*";
                for (int probe = 0; probe < 2; probe++)
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(target, 1500, new byte[32], opts);
                        if (reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired)
                        {
                            times.Add(reply.RoundtripTime);
                            addr = reply.Address.ToString();
                            if (reply.Status == IPStatus.Success) reached = true;
                            break;
                        }
                        else if (reply.Status == IPStatus.TimedOut) times.Add(-1);
                    }
                    catch { times.Add(-1); }
                }
                hopLine += times.Count > 0 ? string.Join("   ", times.Select(t => t < 0 ? "  *  " : $"{t,3} ms")) : "  *  ";
                hopLine += $"   {addr}";
                sb.AppendLine(hopLine);
                if (reached) break;
                await Task.Delay(250);
            }
        }
        catch (Exception ex) { sb.AppendLine($"Traceroute failed: {ex.Message}"); }
        sb.AppendLine();
        sb.AppendLine(reached ? "Trace complete." : "Reached maximum hops.");
        return new ToolResult { Title = $"Traceroute {target}", Success = reached, Output = sb.ToString() };
    }

    public static async Task<ToolResult> NsLookupAsync(string domain, string server, string recordType)
    {
        var sb = new StringBuilder();
        ushort type = DnsQuery.TypeFromName(recordType);
        if (string.IsNullOrWhiteSpace(server)) server = SettingsService.Current.MonitorTarget;
        sb.AppendLine($"Server:  {server}");
        sb.AppendLine($"Query:   {domain}  TYPE={recordType}");
        sb.AppendLine();
        var answer = await DnsQuery.QueryAsync(server, domain, type, 4000);
        if (answer.Ok)
        {
            sb.AppendLine($"Non-authoritative answer ({answer.Ms} ms):");
            if (answer.Records.Count == 0) sb.AppendLine("  (no records of this type)");
            foreach (var r in answer.Records) sb.AppendLine($"  {r}");
        }
        else
        {
            sb.AppendLine($"Query failed: {answer.Error}");
        }
        return new ToolResult { Title = $"NSLookup {domain}", Success = answer.Ok, Output = sb.ToString() };
    }
}
