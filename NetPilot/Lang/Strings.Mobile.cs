using System.Collections.Generic;

namespace NetPilot.Lang;

static partial class Strings
{
    public static void RegisterMobile() => Lang.Register(new Dictionary<string, (string, string)>
    {
        // ---- nav ----
        // The feature moves traffic through a tunnel that runs *on this phone* and hands it to
        // the desktop. It is not a VPN and never connects to a VPN server, so it is not
        // called one - a store listing that says "VPN" invites policy questions it cannot
        // answer. Strings about the PC's *own* VPN (mv_pc_vpn_*, mv_guard_*) keep the word.
        ["mobile_vpn"] = ("تونل گوشی → PC", "Phone Tunnel → PC"),

        // ---- page ----
        ["mv_title"] = ("تونل گوشی → PC", "Phone Tunnel → PC"),
        ["mv_subtitle"] = ("اشتراک‌گذاری تونل گوشی با این رایانه", "Share this phone's tunnel with this PC"),

        // ---- status tiles ----
        ["mv_mobile_connected"] = ("موبایل متصل", "Mobile Connected"),
        ["mv_mobile_disconnected"] = ("موبایل قطع", "Mobile Disconnected"),
        ["mv_vpn_active"] = ("تونل فعال", "Tunnel Active"),
        ["mv_vpn_inactive"] = ("تونل غیرفعال", "Tunnel Inactive"),
        ["mv_vpn_unknown"] = ("تونل نامشخص", "Tunnel Unknown"),
        ["mv_pc_connected"] = ("PC متصل", "PC Connected"),
        ["mv_pc_disconnected"] = ("PC قطع", "PC Disconnected"),
        ["mv_pc_vpn_on"] = ("VPN رایانه فعال", "PC VPN Active"),
        ["mv_pc_vpn_off"] = ("VPN رایانه غیرفعال", "PC VPN Inactive"),
        ["mv_traffic"] = ("وضعیت ترافیک", "Traffic Status"),
        ["mv_peers"] = ("دستگاه محلی", "Local Devices"),

        // ---- status tile headers ----
        ["mv_h_mobile"] = ("موبایل", "Mobile"),
        ["mv_h_phone_vpn"] = ("تونل گوشی", "Phone Tunnel"),
        ["mv_h_pc_vpn"] = ("VPN رایانه", "PC VPN"),
        ["mv_h_pc"] = ("رایانه", "Computer"),
        ["mv_h_traffic"] = ("ترافیک لینک", "Link Traffic"),

        // ---- link kinds ----
        ["mv_link_kind_usb"] = ("USB Tethering", "USB Tethering"),
        ["mv_link_kind_hotspot"] = ("Wi-Fi Hotspot", "Wi-Fi Hotspot"),
        ["mv_link_kind_lan"] = ("شبکه محلی", "Local Network"),
        ["mv_link_kind_other"] = ("سایر", "Other"),

        // ---- method ----
        ["mv_method"] = ("روش انتقال", "Transfer Method"),
        ["mv_method_auto"] = ("خودکار (پیشنهادی)", "Auto (recommended)"),
        ["mv_method_usb"] = ("USB Tethering", "USB Tethering"),
        ["mv_method_hotspot"] = ("Wi-Fi Hotspot", "Wi-Fi Hotspot"),
        ["mv_method_lan"] = ("اتصال مستقیم PC و Android", "Direct PC ↔ Android"),
        ["mv_method_proxy"] = ("پروکسی روی شبکه محلی", "Proxy over local network"),

        // ---- links ----
        ["mv_links"] = ("لینک‌های در دسترس", "Available Links"),
        ["mv_no_links"] = ("لینکی پیدا نشد. USB یا Hotspot گوشی را فعال کنید.",
                           "No link found. Enable USB tethering or the hotspot on your phone."),
        ["mv_link_ip"] = ("آدرس IP", "IP Address"),
        ["mv_link_gateway"] = ("Gateway", "Gateway"),
        ["mv_link_status"] = ("وضعیت لینک", "Link Status"),
        ["mv_up"] = ("فعال", "Up"),
        ["mv_down"] = ("غیرفعال", "Down"),

        // ---- actions ----
        ["mv_share"] = ("اشتراک‌گذاری اتصال", "Share Connection"),
        ["mv_stop_share"] = ("پایان اشتراک‌گذاری", "Stop Sharing"),
        ["mv_sharing_now"] = ("ترافیک از مسیر گوشی عبور می‌کند", "Traffic is routed through the phone"),
        ["mv_share_on"] = ("اشتراک فعال", "Sharing On"),
        ["mv_new_code"] = ("کد جدید", "New Code"),
        ["mv_capture"] = ("گرفتن نسخه پشتیبان", "Capture Backup"),
        ["mv_restore"] = ("بازگردانی وضعیت", "Restore State"),
        ["mv_snapshot_none"] = ("هنوز نسخه‌ای ذخیره نشده است.", "No backup captured yet."),
        ["mv_snapshot_at"] = ("آخرین نسخه", "Last backup"),

        // ---- safety guard ----
        ["mv_guard_title"] = ("محافظ VPN رایانه", "PC VPN Guard"),
        ["mv_guard_body"] = ("برای تغییر مسیر پیش‌فرض باید VPN فعال رایانه را به‌روشنی تأیید کنید. تا زمانی که تأیید نکنید، هیچ VPN یا آداپتوری قطع یا جایگزین نمی‌شود.",
                             "Changing the default route requires an explicit confirmation while a PC VPN is active. Until you confirm, no VPN or adapter is disconnected or replaced."),
        ["mv_guard_confirm"] = ("اجازه تغییر مسیر پیش‌فرض را می‌دهم",
                                "I allow changing the default route"),
        ["mv_guard_blocking"] = ("تأیید VPN رایانه لازم است.", "PC VPN confirmation required."),

        // ---- android bridge / api ----
        ["mv_api_title"] = ("پل اندروید", "Android Bridge"),
        ["mv_api_port"] = ("پورت", "Port"),
        ["mv_pairing"] = ("کد جفت‌سازی", "Pairing Code"),
        ["mv_api_running"] = ("در حال اجرا", "Running"),
        ["mv_api_stopped"] = ("متوقف", "Stopped"),
        ["mv_paired"] = ("دستگاه جفت شد", "Device paired"),
        ["mv_not_paired"] = ("دستگاهی جفت نشده", "No device paired"),
        ["mv_device"] = ("دستگاه", "Device"),
        ["mv_api_start"] = ("شروع سرویس", "Start Service"),
        ["mv_api_stop"] = ("توقف سرویس", "Stop Service"),
        ["mv_api_hint"] = ("اپ اندروید از این آدرس و کد برای اتصال استفاده می‌کند. احراز هویت با توکن انجام می‌شود. "
                           + "اگر آدرس را نمی‌بینید، در گوشی دکمهٔ «یافتن رایانه» را بزنید.",
                           "The Android app connects to this address using the code below. Requests are authenticated with a bearer token. "
                           + "If you cannot see an address, tap \"Find PC\" on the phone."),
        ["mv_api_address"] = ("آدرس", "Address"),
        ["mv_mobile_report"] = ("گزارش گوشی", "Phone Report"),
        ["mv_no_report"] = ("هنوز گزارشی نرسیده", "No report yet"),

        // ---- messages ----
        ["mv_blocked_vpn"] = ("عملیات لغو شد: VPN فعال رایانه بدون تأیید شما تغییر نمی‌کند.",
                              "Blocked: your active PC VPN is not changed without confirmation."),
        ["mv_no_link"] = ("هیچ لینک موبایلی انتخاب نشده است.", "No mobile link selected."),
        ["mv_no_proxy"] = ("برای حالت پروکسی، اپ اندرویدی باید جفت شده و آدرس پروکسی خود را گزارش کند. "
                           + "در غیر این صورت از حالت «خودکار» استفاده کنید.",
                           "Proxy mode needs the paired Android app to report its proxy address. "
                           + "Use Auto mode otherwise."),
        ["mv_proxy_unreachable"] = ("آدرس پراکسیِ اعلام‌شده از سوی گوشی جواب نمی‌دهد؛ برای حفظ اتصال همین رایانه، "
                                    + "اشتراک‌گذاری اعمال نشد.",
                                    "The proxy address the phone advertised does not answer; sharing was not "
                                    + "applied, so this PC keeps its own connection."),
        ["mv_snapshot_saved"] = ("نسخه پشتیبان ذخیره شد.", "Backup captured."),
        ["mv_snapshot_restored"] = ("وضعیت شبکه بازگردانی شد.", "Network state restored."),
        ["mv_share_started"] = ("اشتراک‌گذاری فعال شد.", "Sharing started."),
        ["mv_restore_failed"] = (
            "هیچ آداپتوری بازگردانی نشد؛ ممکن است سیاست سیستم مانع تغییر metric ها شده باشد.",
            "No adapter could be restored - a system policy may be blocking the metric change."),
        ["mv_restore_partial"] = (
            "بازگردانی کامل نبود؛ بخشی از آداپتورها تغییر نکرد.",
            "Restore was incomplete - some adapters were left unchanged."),
        ["mv_share_stopped"] = ("اشتراک‌گذاری پایان یافت.", "Sharing stopped."),
        ["mv_busy"] = ("لطفاً صبر کنید…", "Please wait…"),
        ["mv_done"] = ("انجام شد.", "Done."),
        ["mv_error"] = ("خطا", "Error"),

        // ---- snapshot contents ----
        ["mv_backup_card"] = ("پشتیبان‌گیری شبکه", "Network Backup"),
        ["mv_backup_hint"] = ("وضعیت آداپتورها، مسیرها، DNS و VPN رایانه پیش از هر تغییر ذخیره می‌شود.",
                              "Adapter, route, DNS and VPN state is captured before any change."),
        ["mv_backup_adapters"] = ("آداپتورها", "Adapters"),
        ["mv_backup_routes"] = ("مسیر پیش‌فرض", "Default Routes"),
        ["mv_backup_vpn"] = ("اتصال‌های VPN", "VPN Entries"),
        ["mv_status_card"] = ("وضعیت اتصال", "Connection Status"),
        ["mv_links_card"] = ("لینک موبایل", "Mobile Link"),
        ["mv_ready"] = ("آماده", "Ready"),
    });
}
