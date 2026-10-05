using System;
using System.Collections.Generic;

namespace NetPilot.Daemon.Backends;

/// <summary>
/// Validates the values the daemon hands to <c>ip</c>, <c>tc</c>, <c>nft</c>,
/// <c>resolvectl</c>, <c>gsettings</c> and <c>networksetup</c>.
///
/// The daemon exposes an HTTP API with no authentication, so every one of these values arrives
/// from the network. They used to be interpolated into a single argument string, which the target
/// program then split by its own rules - so an interface name, a resolver address or a proxy host
/// could carry extra arguments, and a value holding a shell metacharacter could become a
/// different command entirely.
///
/// Two layers, and both are needed:
///
/// 1. <see cref="Shell"/>'s list-argument overload, so each value is one argument whatever it
///    contains. That is what stops a value becoming a second argument.
/// 2. This class, so a value cannot express something other than an address in the first place.
///    That is what stops an interface name of <c>eth0; reboot</c> from being a syntactically
///    valid argument to something else.
///
/// Layer 1 alone would make injection impossible but would still let a caller ask the daemon to
/// operate on an interface named <c>--help</c>. Layer 2 alone would leave the call sites needing
/// careful quoting. Together neither has to be careful.
/// </summary>
internal static class Validate
{
    /// <summary>
    /// A network interface name.
    ///
    /// Real names on Linux are <c>eth0</c>, <c>wlan0</c>, <c>enp3s0</c>, <c>br-lan</c>,
    /// <c>veth1234@if7</c>, <c>ap0</c>, <c>wlp3s0</c>. Linux itself caps IFNAMSIZ at 15 visible
    /// characters and forbids <c>/</c> and whitespace, so this is close to what the kernel
    /// accepts - which is the point. A leading hyphen is rejected explicitly: it would be read as
    /// an option by <c>ip</c>, <c>tc</c> and <c>ifconfig</c> alike.
    /// </summary>
    public static bool IsInterfaceName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 40) return false;
        if (name[0] == '-') return false;
        if (name == "." || name == "..") return false;

        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == '.' || c == '-' || c == '_' || c == ':' || c == '@';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// A DNS server address: an IPv4 or IPv6 literal.
    ///
    /// Only literals, no hostnames. resolvectl takes an address and not a name, so accepting a
    /// hostname here would be accepting a value the tool cannot use anyway - and a name is a much
    /// larger space to sanitise than an address.
    ///
    /// <c>ip</c> and <c>resolvectl</c> both accept IPv6 with an optional zone suffix
    /// (<c>fe80::1%eth0</c>), so <c>%</c> is allowed only after an IPv6 literal.
    /// </summary>
    public static bool IsIpAddress(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > 64) return false;

        string s = address;

        // An IPv6 zone id, but only if the part before it is an IPv6 literal (it contains ':').
        int pct = s.IndexOf('%');
        if (pct >= 0)
        {
            if (!s.Substring(0, pct).Contains(':')) return false;
            string zone = s.Substring(pct + 1);
            if (zone.Length == 0) return false;
            foreach (char c in zone)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                      (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_'))
                    return false;
            s = s.Substring(0, pct);
        }

        bool sawColon = s.Contains(':');
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            bool ok = hex || c == '.' || c == ':';
            if (!ok) return false;
        }

        // "abcd" passes the character filter but is not an address.
        if (sawColon)
        {
            if (!System.Net.IPAddress.TryParse(s, out var v6)) return false;
            if (v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            // Require a real IPv6 literal: at least one colon *and* a hextet, or an IPv4-mapped
            // form with both. .NET parses "1.1.1" as a valid IPv6 address (::1.1.1), and
            // resolvectl would not - it would treat it as a malformed address and refuse, so
            // accepting it here would report a failure the user cannot act on.
            return s.Count(c => c == ':') >= 2 || s.Contains('.');
        }

        // Dotted quad only. IPAddress.TryParse accepts forms no resolver tool does - "1", "0x7f.1"
        // - so the shape is checked rather than only the parse.
        int dots = s.Count(c => c == '.');
        if (dots != 3) return false;
        foreach (var part in s.Split('.'))
            if (part.Length == 0 || part.Length > 3) return false;

        return System.Net.IPAddress.TryParse(s, out var v4) &&
               v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>
    /// A port number, as text.
    ///
    /// Returned as the canonical string rather than an int so callers cannot accidentally
    /// re-format it into something else on the way to the command line.
    /// </summary>
    public static bool TryPort(string port, out string canonical)
    {
        canonical = null;
        if (string.IsNullOrEmpty(port) || port.Length > 5) return false;
        foreach (char c in port)
            if (c < '0' || c > '9') return false;
        if (!int.TryParse(port, out int p)) return false;
        if (p < 1 || p > 65535) return false;
        canonical = p.ToString();
        return true;
    }

    /// <summary>
    /// A proxy as <c>host:port</c>, split and validated. The host may be a hostname or an IP
    /// literal - a proxy is commonly named rather than addressed, so hostnames are allowed here
    /// where they are not for a DNS server.
    /// </summary>
    public static bool TryHostPort(string value, out string host, out string port)
    {
        host = null;
        port = null;
        if (string.IsNullOrEmpty(value) || value.Length > 300) return false;

        string s = value.Trim();
        if (s.Length == 0) return false;

        // An optional http/https scheme, which is how the phone advertises its proxy.
        int schemeAt = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeAt >= 0)
        {
            string scheme = s.Substring(0, schemeAt);
            if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return false;
            s = s.Substring(schemeAt + 3);
        }

        // Split on the last colon, unless that colon belongs to a bracketed IPv6 literal.
        int lastColon = s.LastIndexOf(':');
        int closeBracket = s.IndexOf(']');
        bool ipv6 = closeBracket >= 0 && closeBracket > lastColon;
        if (!ipv6 && s.Count(c => c == ':') != 1) return false;
        if (lastColon <= 0) return false;

        string h = s.Substring(0, lastColon);
        string p = s.Substring(lastColon + 1);

        if (h.StartsWith("[") && h.EndsWith("]")) h = h.Substring(1, h.Length - 2);

        if (!IsProxyHost(h)) return false;
        if (!TryPort(p, out port)) return false;

        host = h;
        return true;
    }

    private static bool IsProxyHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 255) return false;
        if (host.StartsWith("-") || host.StartsWith(".")) return false;
        if (host.EndsWith(".")) return false;
        if (host.Contains("..")) return false;

        foreach (char c in host)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == '.' || c == '-' || c == '_' || c == ':';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Validates a list of DNS servers all at once, so a caller cannot validate the first and
    /// then append to the list unchecked.
    /// </summary>
    public static bool AreDnsServers(IReadOnlyList<string> servers)
    {
        if (servers == null) return false;
        if (servers.Count == 0 || servers.Count > 8) return false;
        foreach (var s in servers)
            if (!IsIpAddress(s)) return false;
        return true;
    }

    /// <summary>
    /// A macOS network service name.
    ///
    /// Different from a Linux interface name: these are user-editable labels like "Wi-Fi",
    /// "Ethernet", "USB 10/100/1000 LAN" and "Thunderbolt Bridge", so spaces and punctuation are
    /// normal and cannot simply be forbidden.
    ///
    /// What is forbidden is everything with meaning to <c>networksetup</c> or to a shell: quotes,
    /// backticks, <c>$</c>, <c>;</</c>, <c>&amp;</c>, <c>|</c>, braces, brackets, newlines. Those are
    /// the characters that made <c>-setdnsservers "{adapter}" {value}</c> a command-injection path
    /// - the value went in with no quoting at all, and the adapter name went in inside quotes that
    /// the value could close.
    /// </summary>
    public static bool IsMacServiceName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) return false;
        if (name.Trim().Length == 0) return false;
        if (name.StartsWith("-", StringComparison.Ordinal)) return false;

        foreach (char c in name)
        {
            if (char.IsControl(c)) return false;
            if (c == '"' || c == '\'' || c == '`' || c == '$' || c == ';' ||
                c == '&' || c == '|' || c == '{' || c == '}' || c == '<' || c == '>' ||
                c == '(' || c == ')' || c == '[' || c == ']' || c == '\\' || c == '*' ||
                c == '?' || c == '!' || c == '#' || c == '%' || c == '\r' || c == '\n')
                return false;
        }
        return true;
    }
}