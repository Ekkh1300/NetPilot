using System.Collections.Generic;

namespace NetPilot.Lang;

static partial class Strings
{
    public static void RegisterMonitor() => Lang.Register(new Dictionary<string, (string, string)>
    {
        // ---- Network Monitor ----
        ["mon_download_speed"] = ("سرعت دانلود", "Download Speed"),
        ["mon_upload_speed"] = ("سرعت آپلود", "Upload Speed"),
        ["mon_ping"] = ("پینگ", "Ping"),
        ["mon_packet_loss"] = ("اتلاف بسته", "Packet Loss"),
        ["mon_live"] = ("زنده", "Live"),
        ["mon_uptime"] = ("مدت اتصال", "Uptime"),
        ["mon_total_down"] = ("کل دانلود", "Total Download"),
        ["mon_total_up"] = ("کل آپلود", "Total Upload"),
        ["mon_disconnected"] = ("قطع اتصال", "Disconnected"),
        ["mon_connected"] = ("متصل", "Connected"),
        ["mon_stable"] = ("پایدار", "Stable"),
        ["mon_unstable"] = ("ناپایدار", "Unstable"),
        ["mon_target"] = ("مقصد تست", "Test Target"),
        ["mon_interface"] = ("کارت فعال", "Active Adapter"),
        ["mon_latency"] = ("تأخیر", "Latency"),
        ["mon_idle"] = ("بی‌فعالیت", "Idle"),

        // ---- Per-App Usage ----
        ["pa_app"] = ("برنامه", "Application"),
        ["pa_down_speed"] = ("سرعت دانلود", "Download Speed"),
        ["pa_up_speed"] = ("سرعت آپلود", "Upload Speed"),
        ["pa_total_down"] = ("کل دانلود", "Total Download"),
        ["pa_total_up"] = ("کل آپلود", "Total Upload"),
        ["pa_connections"] = ("اتصالات", "Connections"),
        ["pa_search_ph"] = ("جستجوی برنامه…", "Search application…"),
        ["pa_no_data"] = ("هنوز داده‌ای جمع‌آوری نشده", "No data collected yet"),
        ["pa_pid"] = ("PID", "PID"),
        ["pa_gathering"] = ("در حال جمع‌آوری ترافیک…", "Collecting traffic…"),
        ["pa_desc"] = ("ترافیک لحظه‌ای هر برنامه از ETW ویندوز خوانده می‌شود", "Live per-app traffic read from Windows ETW"),

        // ---- Dashboard ----
        ["db_health"] = ("سلامت شبکه", "Network Health"),
        ["db_dns"] = ("DNS", "DNS"),
        ["db_latency"] = ("تأخیر", "Latency"),
        ["db_packet_loss"] = ("اتلاف بسته", "Packet Loss"),
        ["db_connection"] = ("اتصال", "Connection"),
        ["db_excellent"] = ("عالی", "Excellent"),
        ["db_good"] = ("خوب", "Good"),
        ["db_fair"] = ("متوسط", "Fair"),
        ["db_poor"] = ("ضعیف", "Poor"),
        ["db_quick_actions"] = ("اقدامات سریع", "Quick Actions"),
        ["db_quick_switch"] = ("تعویض سریع DNS", "Quick DNS Switch"),
        ["db_run_benchmark"] = ("اجرای بنچ‌مارک", "Run Benchmark"),
        ["db_view_history"] = ("مشاهده تاریخچه", "View History"),
        ["db_open_tools"] = ("ابزارهای شبکه", "Network Tools"),
        ["db_top_app_now"] = ("پرمصرف‌ترین برنامه", "Top App Now"),
        ["db_activity"] = ("فعالیت شبکه", "Network Activity"),
        ["db_offline"] = ("آفلاین", "Offline"),
        ["db_today"] = ("امروز", "Today"),
        ["db_state_good"] = ("شبکه در وضعیت مطلوب", "Network is in good shape"),
        ["db_state_bad"] = ("شبکه مشکل دارد", "Network has issues"),
        ["db_score_breakdown"] = ("جزئیات امتیاز", "Score Breakdown"),
    });
}
