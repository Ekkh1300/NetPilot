using System.Collections.Generic;

namespace NetPilot.Lang;

static partial class Strings
{
    public static void RegisterNet() => Lang.Register(new Dictionary<string, (string, string)>
    {
        // ---- Net Limiter ----
        ["nl_mode"] = ("حالت", "Mode"),
        ["nl_allow"] = ("مجاز", "Allow"),
        ["nl_limit"] = ("محدود", "Limit"),
        ["nl_block"] = ("مسدود", "Block"),
        ["nl_down_limit"] = ("سقف دانلود", "Download Limit"),
        ["nl_up_limit"] = ("سقف آپلود", "Upload Limit"),
        ["nl_add_rule"] = ("افزودن قانون", "Add Rule"),
        ["nl_rules"] = ("قوانین فعال", "Active Rules"),
        ["nl_no_rules"] = ("قانونی ثبت نشده", "No rules yet"),
        ["nl_apply_rule"] = ("اعمال قانون", "Apply Rule"),
        ["nl_app_path"] = ("مسیر برنامه", "Application Path"),
        ["nl_browse"] = ("انتخاب فایل…", "Browse…"),
        ["nl_rule_added"] = ("قانون اعمال شد", "Rule applied"),
        ["nl_rule_removed"] = ("قانون حذف شد", "Rule removed"),
        ["nl_app_blocked"] = ("برنامه مسدود شد", "Application blocked"),
        ["nl_app_unblocked"] = ("برنامه آزاد شد", "Application unblocked"),
        ["nl_blocked_active"] = ("مسدود (فعال)", "Blocked (active)"),
        ["nl_limit_active"] = ("محدود (فعال)", "Limit (active)"),
        ["nl_select_app"] = ("ابتدا برنامه را انتخاب کنید", "Select an application first"),
        ["nl_select_mode"] = ("حالت را انتخاب کنید", "Choose a mode"),
        ["nl_dl_ph"] = ("مثلاً 512 KB/s یا 1 MB/s", "e.g. 512 KB/s or 1 MB/s"),
        ["nl_ul_ph"] = ("مثلاً 128 KB/s", "e.g. 128 KB/s"),
        ["nl_invalid_rate"] = ("مقدار نامعتبر (مثال: 512 KB/s)", "Invalid value (e.g. 512 KB/s)"),
        ["nl_limit_enabled"] = ("محدودیت فعال شد", "Limit enabled"),
        ["nl_limit_disabled"] = ("محدودیت غیرفعال شد", "Limit disabled"),
        ["nl_current_processes"] = ("برنامه‌های فعال", "Running Applications"),

        // ---- Scheduled Limits ----
        ["sc_title"] = ("زمان‌بندی محدودیت", "Schedule Limits"),
        ["sc_add"] = ("افزودن زمان‌بندی", "Add Schedule"),
        ["sc_no_schedules"] = ("زمان‌بندی‌ای ثبت نشده", "No schedules yet"),
        ["sc_start"] = ("شروع", "Start"),
        ["sc_end"] = ("پایان", "End"),
        ["sc_daily"] = ("روزانه", "Daily"),
        ["sc_schedule_added"] = ("زمان‌بندی ذخیره شد", "Schedule saved"),
        ["sc_schedule_deleted"] = ("زمان‌بندی حذف شد", "Schedule deleted"),
        ["sc_schedule_toggled"] = ("وضعیت زمان‌بندی تغییر کرد", "Schedule status changed"),
        ["sc_active_now"] = ("اکنون فعال", "Active now"),
        ["sc_example_hint"] = ("مثال: استیم از ۰۰:۰۰ تا ۰۸:۰۰ محدود به 1 MB/s", "Example: Steam limited to 1 MB/s from 00:00 to 08:00"),
        ["sc_over_midnight"] = ("عبور از نیمه‌شب", "Crosses midnight"),

        // ---- Profiles ----
        ["pr_gaming"] = ("گیمینگ", "Gaming"),
        ["pr_browsing"] = ("وب‌گردی", "Browsing"),
        ["pr_create"] = ("ساخت پروفایل", "Create Profile"),
        ["pr_apply"] = ("اعمال پروفایل", "Apply Profile"),
        ["pr_applied"] = ("پروفایل اعمال شد", "Profile applied"),
        ["pr_duplicated"] = ("پروفایل کپی شد", "Profile duplicated"),
        ["pr_deleted"] = ("پروفایل حذف شد", "Profile deleted"),
        ["pr_contains_dns"] = ("DNS", "DNS"),
        ["pr_contains_limits"] = ("محدودیت‌ها", "Limits"),
        ["pr_contains_blocked"] = ("مسدودها", "Blocked"),
        ["pr_no_profiles"] = ("پروفایلی موجود نیست", "No profiles"),
        ["pr_active"] = ("پروفایل فعال", "Active Profile"),
        ["pr_select_dns"] = ("DNS این پروفایل", "Profile DNS"),
        ["pr_keep_current_dns"] = ("بدون تغییر DNS", "Keep current DNS"),
        ["pr_backup_created"] = ("پشتیبان از تنظیمات قبلی ساخته شد", "Backup of previous settings created"),
        ["pr_quick_note"] = ("هر پروفایل DNS، محدودیت‌ها و مسدودها را ذخیره می‌کند", "Each profile stores DNS, limits and blocks"),
    });
}
