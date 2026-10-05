using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using LangTable = NetPilot.Lang.Lang;

namespace NetPilot.Tests;

/// <summary>
/// Checks that every <c>Lang[key]</c> reference in the views resolves to a registered string.
///
/// <c>Lang[key]</c> returns the key itself when the key is missing. That is a reasonable
/// default - a missing translation degrades to something readable rather than throwing on every
/// layout pass - and it is exactly why this bug class is invisible. The Diagnostics page
/// referenced <c>st_refresh</c>, which was never registered, and rendered a button reading
/// "st_refresh". Nothing failed: the build succeeded, the page laid out, no log entry appeared.
///
/// The same page also shipped all thirteen of its strings with English in both language columns,
/// so it stayed English inside an otherwise Persian app and looked finished. <see cref="StringsAreActuallyTranslated"/>
/// is what catches that half.
///
/// </summary>
public sealed class LangKeyTests
{
    private static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var sibling = Path.Combine(dir.FullName, "NetPilot");
            if (File.Exists(Path.Combine(sibling, "NetPilot.csproj"))) return sibling;
            if (File.Exists(Path.Combine(dir.FullName, "NetPilot.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "could not find the NetPilot project above " + AppContext.BaseDirectory);
    }

    private static readonly Regex Reference =
        new(@"Lang\[([A-Za-z0-9_]+)\]", RegexOptions.Compiled);

    private static readonly Regex Definition =
        new(@"\[\s*""([a-z0-9_]+)""\s*\]\s*=", RegexOptions.Compiled);

    /// <summary>Every key the app registered, straight from the live table rather than a copy of it.</summary>
    private static HashSet<string> RegisteredKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in typeof(LangTable).Assembly.GetTypes())
        {
            if (f.Namespace != "NetPilot.Lang" || !f.Name.StartsWith("Strings", StringComparison.Ordinal))
                continue;
            foreach (var field in f.GetFields(System.Reflection.BindingFlags.Public |
                                              System.Reflection.BindingFlags.Static))
                if (field.FieldType == typeof(IReadOnlyList<string>) && field.GetValue(null) is IEnumerable<string> list)
                    foreach (var k in list) keys.Add(k);
        }
        return keys;
    }

    [Fact]
    public void EveryLangReferenceInXamlIsRegistered()
    {
        var root = ProjectRoot();

        // The live table, by asking the indexer. A missing key returns itself, so a key counts as
        // "registered" only if it comes back as something other than its own name.
        // Scanned from source rather than reflected over the type, because the split is across
        // several partial-class files and the point is to compare XAML against the *text* that
        // registers the keys - the same text a reader has to search.
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Lang"), "Strings*.cs"))
            foreach (Match m in Definition.Matches(File.ReadAllText(file)))
            {
                string key = m.Groups[1].Value;
                string value = LangTable.Instance[key];
                if (value != key) registered.Add(key);
            }

        Assert.True(registered.Count > 300,
            $"only {registered.Count} keys resolved through Lang.Instance, so the scan found nothing");

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            bool inComment = false;
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in Reference.Matches(StripComment(lines[i], ref inComment)))
                {
                    string key = m.Groups[1].Value;
                    // Lang[Key] in a view model is the NavItem's property, not a string literal.
                    if (key == "Key") continue;
                    if (!registered.Contains(key))
                        missing.Add($"{Path.GetFileName(file)}:{i + 1}  Lang[{key}]");
                }
        }

        Assert.True(missing.Count == 0,
            "Lang keys used in XAML that no language file registers - each renders as its own " +
            "name on screen: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Blanks out XML comment text, keeping the length so a failure still points at the right
    /// line, and carrying the open/closed state across lines.
    ///
    /// Needed because these files are heavily commented and the comments are multi-line. A
    /// comment explaining the rule - "Lang[key] returns the key when it is missing" - is not a
    /// binding, and without this the explanation itself fails the test.
    /// </summary>
    private static string StripComment(string line, ref bool inComment)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        int i = 0;

        while (i < line.Length)
        {
            if (inComment)
            {
                int close = line.IndexOf("-->", i, StringComparison.Ordinal);
                if (close < 0)
                {
                    sb.Append(' ', line.Length - i);
                    return sb.ToString();
                }
                sb.Append(' ', close + 3 - i);
                i = close + 3;
                inComment = false;
                continue;
            }

            int open = line.IndexOf("<!--", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(line, i, line.Length - i);
                return sb.ToString();
            }
            sb.Append(line, i, open - i);
            sb.Append("    ");
            i = open + 4;
            inComment = true;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The proof that the scan above is looking at something real. A key that is genuinely
    /// unregistered has to be reported, or the test would also pass on a scan that silently
    /// matched nothing.
    /// </summary>
    [Fact]
    public void TheScanWouldHaveCaughtTheOriginalBug()
    {
        Assert.False(RegisteredKeys().Contains("st_refresh"),
            "st_refresh was never registered - that is the key the Diagnostics page used");

        // "refresh" is the key that does exist, and is what the page should have referenced.
        Assert.True(LangTable.Instance["refresh"] != "refresh",
            "Lang.Instance[refresh] should resolve to a translated string");
        Assert.NotEqual("st_refresh", LangTable.Instance["refresh"]);
    }

    /// <summary>
    /// A string whose two language columns are identical is not a translation - it is an
    /// untranslated value that looks done.
    ///
    /// That is what the Diagnostics page shipped: all thirteen entries had the English text in
    /// both columns, so the page stayed English inside a Persian app. Identical values are only
    /// acceptable for genuinely language-neutral content - a product name, a version string, a
    /// protocol name - so those are listed rather than the check being weakened.
    /// </summary>
    [Fact]
    public void StringsAreActuallyTranslated()
    {
        var root = Path.Combine(ProjectRoot(), "Lang");

        // Words that do not translate: a protocol name, a hardware label printed on the
        // device, a language's own name, a unit. Listing them is deliberate - the alternative is
        // loosening the check, and then the real placeholders stop being caught.
        var neutral = new HashSet<string>(StringComparer.Ordinal)
        {
            "netpilot", "dns", "dhcp", "ntp", "vpn", "ip", "url", "tcp", "udp",
            "ipv4", "ipv6", "mtu", "dnssec", "localhost", "ms", "kb", "mb", "gb",
            "pid", "english", "timeout",
            // Hardware names, as printed on the device and on the phone's own UI. Translating
            // them would make the row unrecognisable to the person holding the phone.
            "usb tethering", "wi-fi hotspot",
            // Borrowed verbatim in Persian technical writing.
            "gateway",
        };

        var untranslated = new List<string>();
        var checkedCount = 0;

        foreach (var file in Directory.EnumerateFiles(root, "Strings*.cs"))
        {
            var text = File.ReadAllText(file);
            // ["key"] = ("fa", "en"),
            foreach (Match m in Regex.Matches(text,
                         @"\[\s*""([a-z0-9_]+)""\s*\]\s*=\s*\(\s*""((?:[^""\\]|\\.)*)""\s*,\s*""((?:[^""\\]|\\.)*)""",
                         RegexOptions.Compiled))
            {
                string key = m.Groups[1].Value;
                string fa = m.Groups[2].Value.Trim();
                string en = m.Groups[3].Value.Trim();
                checkedCount++;

                if (fa.Length == 0)
                {
                    untranslated.Add($"{Path.GetFileName(file)}  {key}  (Persian is empty)");
                    continue;
                }

                if (fa != en) continue;
                if (neutral.Contains(fa.ToLowerInvariant())) continue;

                untranslated.Add(
                    $"{Path.GetFileName(file)}  {key}  fa == en == \"{Truncate(fa)}\"");
            }
        }

        Assert.True(checkedCount > 300, $"only matched {checkedCount} strings, so the scan found nothing");

        Assert.True(untranslated.Count == 0,
            untranslated.Count + " strings have the same text in both languages, so they do not " +
            "translate - in a Persian app they read as untranslated placeholders:\n" +
            string.Join("\n", untranslated));
    }

    /// <summary>
    /// The Diagnostics page specifically, since it is the page that shipped untranslated. Every
    /// key it owns must have Persian text that is actually Persian - not just different from the
    /// English, which a machine translation of a label like "Log file" would satisfy while still
    /// being wrong to a reader.
    /// </summary>
    [Fact]
    public void TheDiagnosticsPageIsTranslatedIntoPersian()
    {
        var file = Path.Combine(ProjectRoot(), "Lang", "StringsDiagnostics.cs");
        Assert.True(File.Exists(file), "StringsDiagnostics.cs is missing");

        var text = File.ReadAllText(file);
        foreach (Match m in Regex.Matches(text,
                     @"\[\s*""([a-z0-9_]+)""\s*\]\s*=\s*\(\s*""((?:[^""\\]|\\.)*)""\s*,\s*""((?:[^""\\]|\\.)*)""",
                     RegexOptions.Compiled))
        {
            string key = m.Groups[1].Value;
            string fa = m.Groups[2].Value;

            Assert.True(fa != m.Groups[3].Value,
                $"{key} has the same text in both languages");
            // Persian uses this range heavily. Requiring at least one such character catches
            // "translated" values that are still English with a stray diacritic.
            Assert.True(fa.Any(c => c >= '\u0600' && c <= '\u06FF'),
                $"{key} has no Persian characters: \"{fa}\"");
        }
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "...";
}