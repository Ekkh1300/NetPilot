using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetPilot.Services;

public record DnsQueryAnswer(bool Ok, double Ms, List<string> Records, string Error = "");

/// <summary>Minimal DNS client over UDP (no system dependency) used by benchmark and NSLookup.</summary>
public static class DnsQuery
{
    public const ushort TypeA = 1, TypeNS = 2, TypeCNAME = 5, TypePTR = 12,
                      TypeMX = 15, TypeTXT = 16, TypeAAAA = 28;

    public static ushort TypeFromName(string name) => name?.ToUpperInvariant() switch
    {
        "AAAA" => TypeAAAA, "NS" => TypeNS, "CNAME" => TypeCNAME,
        "PTR" => TypePTR, "MX" => TypeMX, "TXT" => TypeTXT, _ => TypeA,
    };

    public static async Task<DnsQueryAnswer> QueryAsync(string server, string domain,
        ushort qtype = TypeA, int timeoutMs = 2000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            byte[] query = BuildQuery(domain, qtype, true, out ushort id);

            var (ok, resp, error) = await QueryUdpAsync(server, query, id, timeoutMs, ct);

            // TC bit: the resolver could not fit the answer into the UDP datagram, so the
            // counts in the header describe a payload we never received. Parsing it anyway
            // returned half an answer. Retry over TCP, which has no size limit.
            if (ok && resp != null && resp.Length >= 4 && (resp[2] & 0x02) != 0)
            {
                var tcp = await QueryTcpAsync(server, query, id, timeoutMs, ct);
                if (tcp.ok) { ok = true; resp = tcp.resp; error = ""; }
                else { ok = false; resp = null; error = tcp.error; }
            }

            sw.Stop();
            if (!ok)
                return new DnsQueryAnswer(false, sw.ElapsedMilliseconds, new(), error);

            return ParseResponse(resp, id, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { return new DnsQueryAnswer(false, timeoutMs, new(), "canceled"); }
        catch (Exception ex) { return new DnsQueryAnswer(false, sw.ElapsedMilliseconds, new(), ex.Message); }
    }

    /// <summary>One UDP round trip. Returns the raw datagram (validated for length, id and
    /// rcode is left to <see cref="ParseResponse"/>).</summary>
    private static async Task<(bool ok, byte[] resp, string error)> QueryUdpAsync(
        string server, byte[] query, ushort id, int timeoutMs, CancellationToken ct)
    {
        using var udp = new UdpClient();
        try
        {
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Client.SendTimeout = timeoutMs;
            udp.Connect(server, 53);              // only datagrams from this server are accepted

            var receiveTask = udp.ReceiveAsync();
            Task<int> sendTask = udp.SendAsync(query, query.Length);
            // Await the send first: an unreachable server must fail now instead of
            // silently burning the whole timeout, and the fault is never left unobserved.
            await sendTask;

            var done = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, ct));
            if (done != receiveTask)
            {
                // Close the socket so the pending receive completes instead of faulting
                // later against a disposed UdpClient.
                try { udp.Close(); } catch { }
                try { await receiveTask; } catch { }
                ct.ThrowIfCancellationRequested();
                return (false, null, "timeout");
            }

            return (true, (await receiveTask).Buffer, "");
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return (false, null, ex.Message);
        }
    }

    private static async Task<(bool ok, byte[] resp, string error)> QueryTcpAsync(
        string server, byte[] query, ushort id, int timeoutMs, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        try
        {
            var connect = tcp.ConnectAsync(server, 53);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs, ct)) != connect)
            {
                ct.ThrowIfCancellationRequested();
                return (false, null, "timeout");
            }
            await connect;

            var stream = tcp.GetStream();

            // RFC 1035: TCP messages carry a 2-byte length prefix.
            var framed = new byte[query.Length + 2];
            framed[0] = (byte)(query.Length >> 8);
            framed[1] = (byte)(query.Length & 0xFF);
            Buffer.BlockCopy(query, 0, framed, 2, query.Length);
            await stream.WriteAsync(framed, 0, framed.Length, ct);

            var lenBuf = new byte[2];
            if (!await ReadExactAsync(stream, lenBuf, 2, timeoutMs, ct))
                return (false, null, "timeout");
            int len = (lenBuf[0] << 8) | lenBuf[1];
            if (len <= 0 || len > 65535) return (false, null, "bad length");

            var resp = new byte[len];
            if (!await ReadExactAsync(stream, resp, len, timeoutMs, ct))
                return (false, null, "timeout");
            return (true, resp, "");
        }
        catch (Exception ex) when (ex is SocketException or System.IO.IOException or ObjectDisposedException)
        {
            return (false, null, ex.Message);
        }
    }

    private static async Task<bool> ReadExactAsync(System.IO.Stream s, byte[] buf, int count,
        int timeoutMs, CancellationToken ct)
    {
        int offset = 0;
        var clock = Stopwatch.StartNew();
        while (offset < count)
        {
            int remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(remaining);
            int n;
            try { n = await s.ReadAsync(buf, offset, count - offset, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
            if (n <= 0) return false;
            offset += n;
        }
        return true;
    }

    private static DnsQueryAnswer ParseResponse(byte[] resp, ushort id, double ms)
    {
        if (resp == null || resp.Length < 12) return new DnsQueryAnswer(false, ms, new(), "short response");

        ushort respId = (ushort)((resp[0] << 8) | resp[1]);
        if (respId != id) return new DnsQueryAnswer(false, ms, new(), "id mismatch");
        int rcode = resp[3] & 0x0F;
        if (rcode != 0) return new DnsQueryAnswer(false, ms, new(), $"rcode {rcode}");

        ushort qd = (ushort)((resp[4] << 8) | resp[5]);
        ushort an = (ushort)((resp[6] << 8) | resp[7]);
        int pos = 12;
        for (int i = 0; i < qd && pos < resp.Length; i++)
            pos = SkipName(resp, pos) + 4;

        var records = new List<string>();
        for (int i = 0; i < an && pos < resp.Length; i++)
        {
            pos = SkipName(resp, pos);
            if (pos + 10 > resp.Length) break;
            ushort type = (ushort)((resp[pos] << 8) | resp[pos + 1]);
            int rdlen = (resp[pos + 8] << 8) | resp[pos + 9];
            pos += 10;
            if (pos + rdlen > resp.Length) break;
            string rdata = DecodeRdata(resp, pos, rdlen, type);
            if (!string.IsNullOrEmpty(rdata)) records.Add(rdata);
            pos += rdlen;
        }
        return new DnsQueryAnswer(true, ms, records);
    }

    private static byte[] BuildQuery(string domain, ushort qtype, bool edns, out ushort id)
    {
        id = (ushort)Random.Shared.Next(1, 65535);
        var buf = new List<byte>();
        buf.Add((byte)(id >> 8)); buf.Add((byte)(id & 0xFF)); // ID
        buf.Add(0x01); buf.Add(0x00); // flags: RD
        buf.Add(0x00); buf.Add(0x01); // QDCOUNT
        buf.Add(0x00); buf.Add(0x00); // ANCOUNT
        buf.Add(0x00); buf.Add(0x00); // NSCOUNT
        buf.Add(0x00);                 // ARCOUNT hi
        buf.Add(edns ? (byte)0x01 : (byte)0x00); // ARCOUNT lo (1 = OPT pseudo-record)
        foreach (string label in domain.Trim('.').Split('.'))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(label);
            buf.Add((byte)b.Length);
            buf.AddRange(b);
        }
        buf.Add(0x00);
        buf.Add((byte)(qtype >> 8)); buf.Add((byte)(qtype & 0xFF)); // QTYPE
        buf.Add(0x00); buf.Add(0x01); // QCLASS IN

        if (edns)
        {
            // OPT pseudo-record (RFC 6891): advertises a 4096-byte UDP payload so normal
            // answers are not truncated at 512 bytes. Root name = empty.
            buf.Add(0x00);            // NAME
            buf.Add(0x00); buf.Add(0x29); // TYPE = OPT (41)
            buf.Add(0x10); buf.Add(0x00); // CLASS = payload size 4096
            buf.Add(0x00); buf.Add(0x00); buf.Add(0x00); buf.Add(0x00); // TTL (extended rcode/flags)
            buf.Add(0x00);            // RDLENGTH = 0
        }
        return buf.ToArray();
    }

    private static int SkipName(byte[] d, int pos)
    {
        while (pos < d.Length)
        {
            int len = d[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;
            pos += len + 1;
        }
        return pos;
    }

    private static string ReadName(byte[] d, ref int pos)
    {
        var parts = new List<string>();
        int jumps = 0, endPos = -1;
        while (pos < d.Length && jumps < 16)
        {
            int len = d[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | d[pos + 1];
                if (endPos < 0) endPos = pos + 2;
                pos = ptr;
                jumps++;
                continue;
            }
            parts.Add(System.Text.Encoding.ASCII.GetString(d, pos + 1, len));
            pos += len + 1;
        }
        if (endPos >= 0) pos = endPos;
        return string.Join(".", parts);
    }

    private static string DecodeRdata(byte[] d, int pos, int rdlen, ushort type)
    {
        try
        {
            switch (type)
            {
                case TypeA when rdlen == 4:
                    return $"{d[pos]}.{d[pos + 1]}.{d[pos + 2]}.{d[pos + 3]}";
                case TypeAAAA when rdlen == 16:
                    var parts = new List<string>();
                    for (int i = 0; i < 16; i += 2)
                        parts.Add(((d[pos + i] << 8) | d[pos + i + 1]).ToString("x"));
                    return string.Join(":", parts);
                case TypeMX:
                    int pref = (d[pos] << 8) | d[pos + 1];
                    int rp = pos + 2;
                    return $"{pref} {ReadName(d, ref rp)}";
                case TypeTXT:
                    var sb = new System.Text.StringBuilder();
                    int p = pos;
                    while (p < pos + rdlen)
                    {
                        int l = d[p++];
                        sb.Append(System.Text.Encoding.UTF8.GetString(d, p, Math.Min(l, pos + rdlen - p)));
                        p += l;
                    }
                    return sb.ToString();
                case TypeCNAME or TypeNS or TypePTR:
                    int np = pos;
                    return ReadName(d, ref np);
                default:
                    return "";
            }
        }
        catch { return ""; }
    }
}
