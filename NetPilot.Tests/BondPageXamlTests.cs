using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NetPilot.ViewModels;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Checks that every binding on the multi-connection download page points at something the page's
/// own view models actually have.
///
/// A misspelled binding path does not fail to compile, and does not throw. WPF writes a line to
/// the trace log and draws the element with no value, so the page appears, looks complete, and
/// simply shows nothing where the data should be. That is the worst failure available to a page,
/// and it is the one nothing else here can catch: the resource-key tests only look at
/// StaticResource, and no test on this machine has ever drawn the page.
///
/// Bindings inside an item template are checked against the type of the items in the collection
/// the surrounding ItemsControl is bound to, not against the page - which is the whole reason a
/// typo there is easy to miss by eye. Bindings that name their own Source are skipped, since
/// those do not read from the view model at all.
/// </summary>
public sealed class BondPageXamlTests
{
    private const string Marker = "vm:BondViewModel";

    /// <summary>Collections on the page, and the type of the rows in them.</summary>
    private static readonly Dictionary<string, Type> RowTypes = new()
    {
        ["Networks"] = typeof(BondNetworkRow),
        ["Downloads"] = typeof(DownloadRow),
        ["Shares"] = typeof(NetworkShare),
    };

    private static bool HasMember(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) != null ||
        type.GetField(name, BindingFlags.Public | BindingFlags.Instance) != null;

    /// <summary>The path of the page's XAML, from the same project root the other scan uses.</summary>
    private static string PagesPath() =>
        Path.Combine(XamlResourceTests.ProjectRoot(), "Views", "Pages.xaml");

    /// <summary>
    /// Splits the page's template into the bindings made against the view model and the bindings
    /// made against each collection's rows.
    /// </summary>
    private static (List<(Type Type, string Where)> Page, List<(Type Type, string Where)> Items)
        CollectBindings()
    {
        var lines = File.ReadAllLines(PagesPath());

        int start = Array.FindIndex(lines, l => l.Contains(Marker));
        Assert.True(start >= 0, $"Pages.xaml has no DataTemplate for {Marker}");

        var page = new List<(Type, string)>();
        var items = new List<(Type, string)>();

        // The collection each open template holds rows of, innermost last. A null entry is the
        // page's own template, where bindings read from the view model.
        var regions = new Stack<string>();
        string pending = null;               // the most recently seen ItemsSource

        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i];

            // Bindings on this line belong to whichever template was already open when the line
            // began - a line cannot both open a template and use it.
            if (regions.Count > 0)
            {
                string region = regions.Peek();
                foreach (var markup in BindingMarkups(line))
                {
                    if (markup.Contains("Source=", StringComparison.Ordinal)) continue;

                    var root = RootOf(markup);
                    if (root == null) continue;

                    var where = $"{Path.GetFileName(PagesPath())}:{i + 1}  {root}";
                    if (region == null) page.Add((typeof(BondViewModel), where));
                    else if (RowTypes.TryGetValue(region, out var row)) items.Add((row, where));
                    // Anything else - an item template not reached through ItemsSource - is left
                    // alone rather than guessed at.
                }
            }

            var source = ItemsSourceOf(line);
            if (source != null) pending = source;

            foreach (Match _ in Regex.Matches(line, @"<DataTemplate[\s>]"))
            {
                regions.Push(i == start ? null : pending);
                if (i != start) pending = null;
            }

            for (int c = 0; c < Regex.Matches(line, "</DataTemplate>").Count && regions.Count > 0; c++)
                regions.Pop();

            if (regions.Count == 0 && i > start) break;
        }

        return (page, items);
    }

    /// <summary>Each balanced <c>{Binding ...}</c> in a piece of XAML, nested braces included.</summary>
    private static IEnumerable<string> BindingMarkups(string xaml)
    {
        int from = 0;
        while (true)
        {
            int i = xaml.IndexOf("{Binding", from, StringComparison.Ordinal);
            if (i < 0) yield break;

            int level = 0, j = i;
            for (; j < xaml.Length; j++)
            {
                if (xaml[j] == '{') level++;
                else if (xaml[j] == '}')
                {
                    if (--level == 0) { j++; break; }
                }
            }

            yield return xaml[i..j];
            from = j;                     // continue after this one, not from the start again
        }
    }

    /// <summary>The first name in the path - the part that has to exist on the view model.</summary>
    private static string RootOf(string markup)
    {
        var m = Regex.Match(markup, @"^\{Binding\s+(?:Path\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string ItemsSourceOf(string line)
    {
        var m = Regex.Match(line, @"ItemsSource\s*=\s*""\{Binding\s+(?:Path\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public void EveryBindingOnTheBondPagePointsAtSomethingThatExists()
    {
        var (page, _) = CollectBindings();

        // A scan that found nothing would pass every assertion below, so the count is part of it.
        Assert.True(page.Count > 10,
                    $"only found {page.Count} page-level bindings, so the scan did not really work");

        var missing = page.Where(b => !HasMember(b.Type, b.Where.Split("  ").Last()))
                           .Select(b => b.Where)
                           .ToList();

        Assert.True(missing.Count == 0,
                    "bindings on the Bond page that name a property it does not have - the page " +
                    "draws without them and shows nothing, with no error:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// The same, for what the lists show. Checked against the row type of the collection the
    /// surrounding ItemsControl is bound to, because a binding inside an item template reads from
    /// the item and from nothing else.
    /// </summary>
    [Fact]
    public void EveryBindingInTheBondPageListsPointsAtSomethingThatExists()
    {
        var (_, items) = CollectBindings();

        // Each of the three lists has to be found, or this is checking a subset by accident.
        foreach (var collection in RowTypes.Keys)
            Assert.Contains(items, b => b.Type == RowTypes[collection]);

        var missing = items.Where(b => !HasMember(b.Type, b.Where.Split("  ").Last()))
                           .Select(b => $"{b.Type.Name}: {b.Where}")
                           .ToList();

        Assert.True(missing.Count == 0,
                    "bindings inside the Bond page's lists that name a property the row does not " +
                    "have:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// The proof that the scan above would catch what it claims to.
    ///
    /// Without this, a change to the extraction - a regex that stopped matching, a loop that
    /// stopped tracking depth - would leave both tests passing forever while checking nothing,
    /// which is how a static check quietly stops being one.
    /// </summary>
    [Fact]
    public void TheScanWouldNoticeAPropertyThatIsNotThere()
    {
        var (page, items) = CollectBindings();

        Assert.False(HasMember(typeof(BondViewModel), "ThisPropertyDoesNotExist"),
                     "the member lookup is matching everything");
        Assert.False(HasMember(typeof(DownloadRow), "ThisPropertyDoesNotExist"),
                     "the member lookup is matching everything");

        // And the names it did find are real ones, so the lists above are not empty.
        Assert.Contains(page, b => b.Where.EndsWith("  Url", StringComparison.Ordinal));
        Assert.Contains(items, b => b.Type == typeof(DownloadRow) &&
                                    b.Where.EndsWith("  Percent", StringComparison.Ordinal));
    }
}
