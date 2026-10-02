using System.Collections.Generic;

namespace NetPilot.Lang;

static partial class Strings
{
    public static void RegisterHistory() => Lang.Register(new Dictionary<string, (string, string)>
    {
        // ---- Network History ----
        ["nh_daily"] = ("روزانه", "Daily"),
        ["nh_weekly"] = ("هفتگی", "Weekly"),
        ["nh_monthly"] = ("ماهانه", "Monthly"),
        ["nh_total_usage"] = ("مجموع مصرف", "Total Usage"),
        ["nh_top_app"] = ("بیشترین مصرف", "Top Consumer"),
        ["nh_no_data"] = ("هنوز داده‌ای ذخیره نشده", "No data stored yet"),
        ["nh_today"] = ("امروز", "Today"),
        ["nh_yesterday"] = ("دیروز", "Yesterday"),
        ["nh_chart_usage"] = ("نمودار مصرف", "Usage Chart"),
        ["nh_top_apps"] = ("برنامه‌های پرمصرف", "Top Applications"),
        ["nh_avg"] = ("میانگین روزانه", "Daily Average"),
        ["nh_peak"] = ("بیشترین روز", "Peak Day"),
        ["nh_range_7"] = ("۷ روز اخیر", "Last 7 days"),
        ["nh_range_30"] = ("۳۰ روز اخیر", "Last 30 days"),
        ["nh_range_90"] = ("۹۰ روز اخیر", "Last 90 days"),

        // ---- Connection History ----
        ["ch_event"] = ("رویداد", "Event"),
        ["ch_dns_changed"] = ("تغییر DNS", "DNS Changed"),
        ["ch_dns_restored"] = ("بازگردانی DNS", "DNS Restored"),
        ["ch_limit_enabled"] = ("فعال‌شدن محدودیت", "Limit Enabled"),
        ["ch_limit_disabled"] = ("غیرفعال‌شدن محدودیت", "Limit Disabled"),
        ["ch_app_blocked"] = ("مسدودشدن برنامه", "Application Blocked"),
        ["ch_app_unblocked"] = ("رفع مسدودی برنامه", "Application Unblocked"),
        ["ch_network_reset"] = ("بازنشانی شبکه", "Network Reset"),
        ["ch_ip_renewed"] = ("تمدید IP", "IP Renewed"),
        ["ch_flush_dns"] = ("پاک‌کردن کش DNS", "DNS Flushed"),
        ["ch_profile_applied"] = ("اعمال پروفایل", "Profile Applied"),
        ["ch_schedule_created"] = ("ساخت زمان‌بندی", "Schedule Created"),
        ["ch_schedule_deleted"] = ("حذف زمان‌بندی", "Schedule Deleted"),
        ["ch_limit_applied"] = ("اعمال محدودیت", "Limit Applied"),
        ["ch_limit_removed"] = ("حذف محدودیت", "Limit Removed"),
        ["ch_no_events"] = ("رویدادی ثبت نشده", "No events yet"),
        ["ch_clear_all"] = ("پاک‌کردن همه", "Clear All"),
        ["ch_confirm_clear"] = ("همه رویدادها پاک شوند؟", "Clear all events?"),
        ["ch_filter"] = ("فیلتر", "Filter"),
    });
}
