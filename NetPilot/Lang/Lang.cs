using System.Collections.Generic;
using NetPilot.Core;
using NetPilot.Services;

namespace NetPilot.Lang;

/// <summary>
/// Central localization manager. Binds via indexer: {Binding Lang[dashboard]}.
/// Raising Item[key] for every key on language switch refreshes all bindings.
/// </summary>
public sealed class Lang : ObservableObject
{
    public static readonly Lang Instance = new();
    public static bool IsFa => Instance._lang == "fa";

    private string _lang = "fa";
    private static readonly Dictionary<string, string[]> Table = new();

    static Lang()
    {
        Strings.RegisterCore();
        Strings.RegisterDns();
        Strings.RegisterMonitor();
        Strings.RegisterNet();
        Strings.RegisterHistory();
        Strings.RegisterTools();
        Strings.RegisterMobile();
    }

    private Lang() { }

    public string Language => _lang;

    public string Flow => "LeftToRight"; // reserved

    public string this[string key] => Table.TryGetValue(key, out var v)
        ? (_lang == "fa" ? v[0] : v[1])
        : key;

    public void SetLanguage(string lang)
    {
        if (lang != "fa" && lang != "en") return;
        if (_lang == lang) return;
        _lang = lang;

        // WPF only understands the indexer *reset* name. Raising "Item[key]" for each
        // entry is silently ignored by every {Binding Lang[key]} binding, so the page on
        // screen kept its old language until it was rebuilt - which never happens, since
        // the page ViewModels are singletons. One "Item[]" resets every indexer binding
        // in a single pass (the old per-key loop also re-ran each page's language hook
        // once per key - 388 times - on every switch).
        Raise("Item[]");
        Raise(nameof(Language));
        Raise(nameof(FlowDirection));
        SettingsService.SaveLanguage(lang);
    }

    public System.Windows.FlowDirection FlowDirection =>
        _lang == "fa" ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;

    public static void Register(IDictionary<string, (string fa, string en)> entries)
    {
        foreach (var kv in entries) Table[kv.Key] = new[] { kv.Value.fa, kv.Value.en };
    }
}
