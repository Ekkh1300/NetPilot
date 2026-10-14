using System.Collections.Generic;

namespace NetPilot.Lang;

static partial class Strings
{
    public static void RegisterBond() => Lang.Register(new Dictionary<string, (string, string)>
    {
        ["bond_download"] = ("دانلود چنداتصالی", "Multi-Connection Download"),
        ["bond_subtitle"] = ("فایل را روی همهٔ اتصال‌های فعال همزدانلود کنید",
                             "Download a file across all your active connections at once"),
        ["bond_networks"] = ("اتصال‌های قابل استفاده", "Connections you can use"),
        ["bond_url"] = ("آدرس فایل", "File address"),
        ["bond_url_hint"] = ("آدرس http یا https فایل را اینجا بگذارید",
                             "Paste the http or https address of the file here"),
        ["bond_destination"] = ("پوشهٔ ذخیره", "Save to"),
        ["bond_all_files"] = ("همهٔ فایل‌ها", "All files"),
        ["bond_browse"] = ("انتخاب پوشه", "Choose folder"),
        ["bond_start"] = ("شروع دانلود", "Start download"),
        ["bond_cancel_all"] = ("توقف همه", "Cancel all"),
        ["bond_cancel"] = ("توقف", "Cancel"),

        ["bond_downloads"] = ("دانلودها", "Downloads"),
        ["bond_no_downloads"] = ("هنوز دانلودی اضافه نشده است", "No downloads yet"),
        ["bond_no_networks"] = ("اتصال قابل استفاده‌ای پیدا نشد", "No usable connection found"),
        ["bond_skipped"] = ("کنار گذاشته شد", "Skipped"),
        ["bond_saving_to"] = ("ذخیره در", "Saving to"),
        ["bond_carried_by"] = ("سهم هر اتصال", "Share per connection"),
        ["bond_streams"] = ("اتصال همزمان", "Parallel connections"),

        // The warnings. Both describe something the machine did, not something the user did
        // wrong, and both would otherwise look like the feature simply not working.
        ["bond_shared_uplink"] = ("این اتصال‌ها یک مسیر مشترک دارند، پس سریع‌تر از یکی نمی‌شوند",
                                  "These connections share one path, so they will not be faster than one"),
        ["bond_wifi_off"] = ("ویندوز وای‌فای را به‌محض وصل‌شدن کابل خاموش کرده است",
                             "Windows turned Wi-Fi off as soon as the cable was plugged in"),
        ["bond_restore_wifi"] = ("روشن کردن دوبارهٔ وای‌فای", "Turn Wi-Fi back on"),
        ["bond_pick_one"] = ("دست‌کم یک اتصال را انتخاب کنید", "Select at least one connection"),
        ["bond_takes_time"] = ("بهتر است دانلود بلند نباشد؛ چون با هر اتصال جداگانه است",
                               "Short downloads gain least, since each connection is used separately"),

        ["bond_state_running"] = ("در حال دانلود", "Downloading"),
        ["bond_state_done"] = ("تمام شد", "Done"),
        ["bond_state_failed"] = ("ناموفق", "Failed"),
        ["bond_state_cancelled"] = ("متوقف شد", "Stopped"),
    });
}
