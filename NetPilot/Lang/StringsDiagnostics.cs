using System.Collections.Generic;

namespace NetPilot.Lang;

/// <summary>
/// Strings for the Diagnostics page.
///
/// Written as real Persian rather than English in both columns. The first version of this page
/// put the English text in both, which left the whole page English inside an otherwise Persian
/// app - and it read as finished, so nothing flagged it. A placeholder indistinguishable from a
/// translation is worse than an obvious one.
///
/// Placeholders are {0}..{n}, filled by string.Format in the view model. The Persian forms are
/// not transliterations of the English: the order of a label and its number differs, and getting
/// that wrong is what makes machine translation obvious to a reader.
///
/// These entries were previously in Strings.Mobile.cs, next to the phone-pairing strings,
/// because that is where the Diagnostics page was written. They moved here for the same reason
/// the file exists at all: one page, one file, so the next person editing the page can find its
/// text without knowing the history.
/// </summary>
static partial class Strings
{
    public static void RegisterDiagnostics() => Lang.Register(new Dictionary<string, (string, string)>
    {
        // Nav entry and page heading.
        ["diagnostics"] = ("داشبورد", "Diagnostics"),

        // The counters that decide whether the log below can be believed.
        ["dash_logger_file"] = ("فایل لاگ", "Log file"),
        ["dash_logger_written"] = ("نوشته: {0}", "written: {0}"),
        ["dash_logger_dropped"] = ("افتاده: {0}", "dropped: {0}"),
        ["dash_logger_queued"] = ("در صف: {0}", "queued: {0}"),

        ["dash_logger_entries"] = ("{0} ورودی", "{0} entries"),
        ["dash_logger_memory_only"] =
            ("(فقط در حافظه — پوشهٔ قابل نوشتنی نیست)",
             "(memory only — no writable directory)"),

        ["dash_logger_admin"] = ("مدیر سیستم", "administrator"),
        ["dash_logger_user"] = ("کاربر عادی", "a normal user"),
        ["dash_logger_runtime"] =
            ("محیط اجرا: {0} روی {1} — با دسترسی {2}",
             "Runtime: {0} on {1} — running as {2}"),

        // Shown only when entries were actually lost, which is the whole reason it exists: a log
        // with a hole in it that looks complete is worse than one that admits it.
        ["dash_logger_problem"] =
            ("{0} ورودی افتاده و {1} خطای نوشتن رخ داده است. لاگ زیر ناقص است؛ به همین دلیل شمارنده‌ها در همین صفحه آمده‌اند.",
             "{0} entries were dropped and {1} write errors occurred. The log below has gaps, which is why the counters are on the same page."),

        ["dash_logger_note"] =
            ("کدهای جفت‌سازی، توکن‌ها و نشانی‌های سخت‌افزاری پیش از نوشتن جایگزین می‌شوند، بنابراین همین لاگ را می‌توانید بدون تغییر در پروندهٔ پشتیبانی بگذارید.",
             "Pairing codes, tokens and hardware addresses are replaced before anything is written, so this log can be pasted into a support thread as it is."),

        // The search field's hint. Not a placeholder attribute: that would put the hint into the
        // field's own value, where it becomes something the user has to delete and is
        // indistinguishable from a term they typed.
        ["dash_logger_filter_hint"] = ("جست‌وجو در متن لاگ", "Search the log text"),
    });

    /// <summary>
    /// The keys this file owns.
    ///
    /// Listed so a test can assert each one is registered. <c>Lang[key]</c> returns the key
    /// itself when it is missing, so a key that is referenced in XAML but never registered shows
    /// up on screen as raw text - "st_refresh" printed on a button - and nothing else complains.
    /// The Diagnostics page shipped exactly that way. It needs a test, not an eye.
    /// </summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "diagnostics",
        "dash_logger_file",
        "dash_logger_written",
        "dash_logger_dropped",
        "dash_logger_queued",
        "dash_logger_entries",
        "dash_logger_memory_only",
        "dash_logger_admin",
        "dash_logger_user",
        "dash_logger_runtime",
        "dash_logger_problem",
        "dash_logger_note",
        "dash_logger_filter_hint",
    };
}