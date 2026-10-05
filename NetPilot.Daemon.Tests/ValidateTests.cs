using System.Collections.Generic;
using NetPilot.Daemon.Backends;
using Xunit;

namespace NetPilot.Daemon.Tests;

/// <summary>
/// The daemon shells out to <c>ip</c>, <c>tc</c>, <c>nft</c>, <c>resolvectl</c>,
/// <c>gsettings</c>, <c>networksetup</c> and <c>nmcli</c>, and it exposes an HTTP API with no
/// authentication. Every interface name, resolver address and proxy host it passes to those tools
/// therefore arrives from the network.
///
/// They used to be interpolated into a single argument string, which the target program split by
/// its own rules. That made it possible for a caller on the LAN to turn an interface name into
/// extra arguments, or into a different command entirely, running as whatever the daemon runs as -
/// root, for the parts that need it.
///
/// These tests cover the two layers that close it:
/// <list type="bullet">
/// <item>validation, so a value cannot express anything but an address;</item>
/// <item>argument passing, so a value is one argument whatever it contains.</item>
/// </list>
///
/// Both are needed. Validation alone leaves a caller able to name an interface "--help";
/// argument passing alone leaves a caller able to name one "eth0; reboot".
///
/// </summary>
public sealed class ValidateTests
{
    // ------------------------------------------------------------------ interfaces

    [Theory]
    [InlineData("eth0")]
    [InlineData("wlan0")]
    [InlineData("enp3s0")]
    [InlineData("br-lan")]
    [InlineData("veth1234@if7")]
    [InlineData("ap0")]
    [InlineData("wlp3s0")]
    [InlineData("docker0")]
    [InlineData("tun0")]
    [InlineData("eth0.100")]
    public void ARealInterfaceNameIsAccepted(string name)
    {
        Assert.True(Validate.IsInterfaceName(name), $"rejected a real interface name: {name}");
    }

    [Theory]
    [InlineData("eth0 --state up", "space split it into two arguments")]
    [InlineData("--help", "read as an option, not a device")]
    [InlineData("-s", "read as an option, not a device")]
    [InlineData("eth0;reboot", "command separator")]
    [InlineData("eth0 && reboot", "conditional operator")]
    [InlineData("eth0 | tee /tmp/x", "pipeline")]
    [InlineData("$(reboot)", "substitution")]
    [InlineData("`reboot`", "backtick substitution")]
    [InlineData("eth0\nreboot", "newline")]
    [InlineData("eth0/reboot", "path separator")]
    [InlineData("..", "traversal")]
    [InlineData(".", "current directory")]
    [InlineData("", "empty")]
    public void AnInterfaceNameThatIsNotOneIsRejected(string name, string why)
    {
        Assert.False(Validate.IsInterfaceName(name), $"accepted an interface name that {why}: {name}");
    }

    // ------------------------------------------------------------------ DNS servers

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("178.22.122.100")]
    [InlineData("127.0.0.53")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%eth0")]
    public void ARealResolverAddressIsAccepted(string address)
    {
        Assert.True(Validate.IsIpAddress(address), $"rejected a real address: {address}");
    }

    [Theory]
    [InlineData("dns.google", "resolvectl takes an address, not a name")]
    [InlineData("1.1.1.1 --state=up", "space split it into two arguments")]
    [InlineData("1.1.1.1;reboot", "command separator")]
    [InlineData("$(reboot)", "substitution")]
    [InlineData("abcd", "not an address at all")]
    [InlineData("1.1.1", "not an address at all")]
    [InlineData("1.1.1.1.1", "not an address at all")]
    [InlineData("", "empty")]
    [InlineData("::gg", "not hexadecimal")]
    public void AResolverAddressThatIsNotOneIsRejected(string address, string why)
    {
        Assert.False(Validate.IsIpAddress(address), $"accepted an address that {why}: {address}");
    }

    [Fact]
    public void ADnsListIsValidatedAsAWhole()
    {
        // Validating the first and appending unchecked afterwards is how a list like this ends up
        // half-validated, so the list is what gets validated.
        Assert.True(Validate.AreDnsServers(new[] { "1.1.1.1", "8.8.8.8" }));
        Assert.False(Validate.AreDnsServers(new[] { "1.1.1.1", "8.8.8.8; reboot" }));
        Assert.False(Validate.AreDnsServers(new string[0]));
        Assert.False(Validate.AreDnsServers(null));
    }

    // ------------------------------------------------------------------ ports

    [Theory]
    [InlineData("1", "1")]
    [InlineData("80", "80")]
    [InlineData("8080", "8080")]
    [InlineData("65535", "65535")]
    public void ARealPortIsAccepted(string port, string canonical)
    {
        Assert.True(Validate.TryPort(port, out string c));
        Assert.Equal(canonical, c);
    }

    [Theory]
    [InlineData("0", "port zero")]
    [InlineData("65536", "above 65535")]
    [InlineData("-1", "negative")]
    [InlineData("80 81", "two ports in one field")]
    [InlineData("80;reboot", "command separator")]
    [InlineData("http", "not a number")]
    [InlineData("", "empty")]
    public void APortThatIsNotOneIsRejected(string port, string why)
    {
        Assert.False(Validate.TryPort(port, out _), $"accepted a port that is {why}: {port}");
    }

    // ------------------------------------------------------------------ host:port

    [Theory]
    [InlineData("192.168.1.50:8080", "192.168.1.50", "8080")]
    [InlineData("phone.local:3128", "phone.local", "3128")]
    [InlineData("http://192.168.1.50:8080", "192.168.1.50", "8080")]
    [InlineData("https://phone.local:3128", "phone.local", "3128")]
    public void ARealProxyIsAccepted(string value, string host, string port)
    {
        Assert.True(Validate.TryHostPort(value, out string h, out string p), $"rejected: {value}");
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("x'; gsettings set org.gnome.system.proxy ignore-hosts \"['localhost']\"; '", "injected a second command")]
    [InlineData("host:80 extra", "trailing junk")]
    [InlineData("host:99999", "port out of range")]
    [InlineData("host", "no port at all")]
    [InlineData(":8080", "no host")]
    [InlineData("file://etc/passwd", "not an http scheme")]
    [InlineData("host:-1", "negative port")]
    public void AProxyThatIsNotOneIsRejected(string value, string why)
    {
        Assert.False(Validate.TryHostPort(value, out _, out _), $"accepted a proxy that {why}: {value}");
    }

    // ------------------------------------------------------------------ macOS services

    [Theory]
    [InlineData("Wi-Fi")]
    [InlineData("Ethernet")]
    [InlineData("USB 10/100/1000 LAN")]
    [InlineData("Thunderbolt Bridge")]
    [InlineData("iPhone USB")]
    public void ARealMacServiceNameIsAccepted(string name)
    {
        // macOS service names are user-editable labels, so spaces are normal and forbidding them
        // would reject every real adapter.
        Assert.True(Validate.IsMacServiceName(name), $"rejected a real service name: {name}");
    }

    [Theory]
    [InlineData("Wi-Fi\"; reboot; \"", "closed the quotes around it")]
    [InlineData("Wi-Fi' ; reboot; '", "single-quote context")]
    [InlineData("Wi-Fi`reboot`", "backtick")]
    [InlineData("Wi-Fi$(reboot)", "substitution")]
    [InlineData("Wi-Fi; reboot", "command separator")]
    [InlineData("Wi-Fi & reboot", "background operator")]
    [InlineData("Wi-Fi | tee /tmp/x", "pipeline")]
    [InlineData("-setdnsservers", "read as an option")]
    [InlineData("Wi-Fi\nreboot", "newline")]
    [InlineData("Wi-Fi{0}", "braces")]
    public void AMacServiceNameThatIsNotOneIsRejected(string name, string why)
    {
        Assert.False(Validate.IsMacServiceName(name), $"accepted a service name that {why}: {name}");
    }

    // ------------------------------------------------------------------ argument passing

    /// <summary>
    /// The other layer: how an argument string is split.
    ///
    /// This is the exact behaviour that let an injected value become extra arguments, so it is
    /// pinned here rather than left implicit. A value containing a space stays one argument only
    /// if it is quoted - which is why every call site with external input now uses the list
    /// overload instead, where no splitting happens at all.
    /// </summary>
    [Fact]
    public void AnUnquotedSpaceSplitsIntoTwoArguments()
    {
        // Demonstrates the original defect rather than endorsing it: this is what a name of
        // "eth0 --state up" became.
        var parts = Shell.SplitArguments("addr show eth0 --state up");
        Assert.Equal(new[] { "addr", "show", "eth0", "--state", "up" }, parts);
    }

    [Fact]
    public void AQuotedSpaceStaysInsideOneArgument()
    {
        var parts = Shell.SplitArguments("getdnsservers \"Wi-Fi\" 8080");
        Assert.Equal(new[] { "getdnsservers", "Wi-Fi", "8080" }, parts);
    }

    [Fact]
    public void AnExplicitlyEmptyArgumentIsKept()
    {
        // Dropping it would silently change the meaning of a command.
        var parts = Shell.SplitArguments("cmd \"\" tail");
        Assert.Equal(new[] { "cmd", "", "tail" }, parts);
    }

    [Fact]
    public void AnEmptyArgumentStringProducesNoArguments()
    {
        Assert.Empty(Shell.SplitArguments(""));
        Assert.Empty(Shell.SplitArguments("   "));
        Assert.Empty(Shell.SplitArguments(null));
    }

    // ------------------------------------------------------------------ the scan

    /// <summary>
    /// Guards against the fix quietly regressing: no call site may pass an interpolated string
    /// where an argument contains external input.
    ///
    /// A source scan rather than a runtime check, because the thing being prevented is a shape in
    /// the code - and a shape is exactly what a test cannot observe after the fact.
    /// </summary>
    [Fact]
    public void NoBackendInterpolatesIntoAShellArgumentString()
    {
        var dir = FindBackendDirectory();
        var offenders = new List<string>();

        foreach (var file in dir.GetFiles("*.cs"))
        {
            string text = File.ReadAllText(file.FullName);
            var lines = text.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//")) continue;

                int call = line.IndexOf("Shell.Run", StringComparison.Ordinal);
                if (call < 0) call = line.IndexOf("Shell.RunAsync", StringComparison.Ordinal);
                if (call < 0) continue;

                // The second argument is the one that used to carry the interpolation. An
                // interpolated string there is the whole defect.
                var match = System.Text.RegularExpressions.Regex.Match(
                    line[(call)..], @"Shell\.Run(?:Async)?\(\s*""[^""]*""\s*,\s*\$""");
                if (match.Success)
                    offenders.Add($"{file.Name}:{i + 1}  {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "a backend passes an interpolated string as the argument list, which the target " +
            "program re-splits:\n" + string.Join("\n", offenders));
    }

    private static DirectoryInfo FindBackendDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "NetPilot.Daemon", "Backends");
            if (Directory.Exists(candidate)) return new DirectoryInfo(candidate);
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "could not find NetPilot.Daemon/Backends above " + AppContext.BaseDirectory);
    }
}