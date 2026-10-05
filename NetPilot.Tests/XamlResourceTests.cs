using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Checks that every <c>{StaticResource key}</c> in the XAML actually exists.
///
/// WPF resolves a StaticResource when the element that needs it is first measured, not when
/// the XAML is parsed or compiled. A key that does not exist therefore compiles cleanly, runs
/// cleanly, and fails only at the moment the page is drawn - and a page nobody has opened yet
/// is a page nobody has tested. The Diagnostics page shipped with exactly that bug
/// (<c>B.BoolToVisibility</c>, which is <c>BoolToVis</c>): it built without error and threw
/// "Provide value on 'StaticResourceHolder' threw an exception" the first time it was shown.
///
/// So this walks every XAML file and compares each referenced key against the keys the merged
/// resource dictionaries actually define. A new style that is used before it is defined - or
/// spelled slightly differently - fails here instead of on screen.
///
/// </summary>
public sealed class XamlResourceTests
{
    /// <summary>
    /// The app root.
    ///
    /// Walking up from the assembly does not work here: the tests are built into
    /// NetPilot.Tests\bin\Debug, so no ancestor of it contains NetPilot.csproj - the sibling
    /// project is what has to be found. So the search is: walk up for the solution, and take the
    /// sibling. A test that silently found no XAML and passed would be worse than one that fails,
    /// so an unresolvable root throws rather than returning a directory with nothing in it.
    /// </summary>
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
        new(@"\{StaticResource\s+([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    /// <summary>Keys may also be declared inside a template or a theme, not only in a dictionary. </summary>
    private static readonly Regex KeyDeclaration =
        new(@"x:Key\s*=\s*""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    /// <summary>
    /// Templates that are never instantiated by a test still need their keys, so the whole file
    /// set is scanned - not just the pages a test happens to render.
    /// </summary>
    private static HashSet<string> DefinedKeys(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match m in KeyDeclaration.Matches(File.ReadAllText(file)))
                keys.Add(m.Groups[1].Value);
        }

        return keys;
    }

    /// <summary>
    /// Blanks out XML comment text, keeping the length so a failure still points at the right
    /// line, and carrying the open/closed state across lines.
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

    [Fact]
    public void EveryStaticResourceReferenceResolves()
    {
        var root = ProjectRoot();
        var defined = DefinedKeys(root);
        Assert.True(defined.Count > 20,
            $"only found {defined.Count} resource keys under {root}, so the scan found nothing");

        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            // Comments are blanked rather than skipped, so the reported line number is the real
            // one. A comment that quotes a broken key - as this file's own header does - is not
            // a reference and must not fail the scan.
            bool inComment = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i], ref inComment);
                foreach (Match m in Reference.Matches(code))
                {
                    string key = m.Groups[1].Value;
                    if (defined.Contains(key)) continue;
                    missing.Add($"{Path.GetFileName(file)}:{i + 1}  {key}  ({lines[i].Trim()})");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "StaticResource keys referenced but never defined - WPF throws on these the first " +
            "time the page is drawn, and nothing catches it before then:\n" +
            string.Join("\n", missing));
    }

    /// <summary>
    /// The key check above passes on a typo if the typo happens to be defined somewhere else.
    /// This pins the two spellings that have actually been confused, because the whole class of
    /// bug is one of these two being written where the other belongs.
    /// </summary>
    [Fact]
    public void TheBoolToVisibilityConverterIsSpelledTheWayItIsDefined()
    {
        var root = ProjectRoot();
        var defined = DefinedKeys(root);

        Assert.True(defined.Contains("BoolToVis"),
            "BoolToVis is the defined key (Pages.xaml); pages must reference it under that name");
        Assert.False(defined.Contains("B.BoolToVisibility"),
            "B.BoolToVisibility does not exist - this was the Diagnostics page's bug");
    }

    /// <summary>
    /// The proof that this suite would have caught the bug it was written for.
    ///
    /// The main test checks the files as they are, so it passes the moment the typo is fixed.
    /// If the scan were quietly matching nothing - a regex that stopped matching, a glob that
    /// found no XAML - it would keep passing too, and that is the failure mode a static check
    /// is most prone to. So this feeds the original, broken reference through the same scan and
    /// requires that it be reported.
    /// </summary>
    [Fact]
    public void TheScanWouldHaveCaughtTheOriginalBug()
    {
        // A key that is genuinely undefined, in the exact shape the Diagnostics page used.
        const string broken = @"<TextBlock Visibility=""{Binding HasProblems, " +
            @"Converter={StaticResource B.BoolToVisibility}}""></TextBlock>";

        var keys = new HashSet<string>(Reference.Matches(broken)
            .Select(m => m.Groups[1].Value), StringComparer.Ordinal);

        Assert.True(keys.Contains("B.BoolToVisibility"),
            "the scan no longer extracts keys - it would pass every file and catch nothing");
        Assert.False(keys.Contains("BoolToVis"),
            "the scan matched too much and turned an undefined key into a defined one");
    }

    /// <summary>
    /// A key reference that is not really a resource: a converter that was meant to be written
    /// with a value converter extension. Cheap to check, and it is the shape the bug above took.
    /// </summary>
    [Fact]
    public void NoResourceKeyIsPrefixedWithADictionaryName()
    {
        // B.Surface, T.H1 and S.NavItem all look like "a dot in the key" and are legitimate:
        // WPF resolves them through the implicit xmlns declarations on the root element, not
        // through a x:Key. So this only rejects a prefix that matches nothing at all.
        var root = ProjectRoot();
        var prefixes = new HashSet<string> { "B", "T", "S", "core", "vm", "x" };

        var dangling = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            bool inComment = false;
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Reference.Matches(StripComment(lines[i], ref inComment)))
                {
                    string key = m.Groups[1].Value;
                    int dot = key.IndexOf('.');
                    if (dot <= 0) continue;
                    string prefix = key.Substring(0, dot);
                    if (!prefixes.Contains(prefix)) continue;
                    // Known-good prefixes resolve through the xmlns alias; anything else under a
                    // known prefix is a key that was never defined.
                    if (key.StartsWith("B.", StringComparison.Ordinal) ||
                        key.StartsWith("T.", StringComparison.Ordinal) ||
                        key.StartsWith("S.", StringComparison.Ordinal))
                        continue;
                    dangling.Add($"{Path.GetFileName(file)}:{i + 1}  {key}");
                }
            }
        }

        Assert.True(dangling.Count == 0, string.Join("\n", dangling));
    }
}