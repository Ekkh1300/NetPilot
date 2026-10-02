using System.Collections.Generic;

namespace NetPilot.Lang;

static partial class Strings
{
    public static void RegisterTools() => Lang.Register(new Dictionary<string, (string, string)>
    {
        // ---- Network Tools ----
        ["nt_flush_dns"] = ("پاک‌کردن کش DNS", "Flush DNS"),
        ["nt_renew_ip"] = ("تمدید آدرس IP", "Renew IP"),
        ["nt_reset_network"] = ("بازنشانی شبکه", "Reset Network"),
        ["nt_ping"] = ("پینگ", "Ping"),
        ["nt_traceroute"] = ("تریسرانت", "Traceroute"),
        ["nt_nslookup"] = ("ن‌س‌لاک‌آپ", "NSLookup"),
        ["nt_output"] = ("خروجی", "Output"),
        ["nt_target"] = ("آدرس مقصد", "Target"),
        ["nt_run"] = ("اجرا", "Run"),
        ["nt_running"] = ("در حال اجرا…", "Running…"),
        ["nt_done"] = ("عملیات انجام شد", "Operation completed"),
        ["nt_failed"] = ("عملیات ناموفق بود", "Operation failed"),
        ["nt_reboot_note"] = ("برای اعمال کامل بازنشانی، ویندوز را ری‌استارت کنید", "Restart Windows to complete the network reset"),
        ["nt_confirm_reset"] = ("شبکه بازنشانی شود؟ نیاز به ری‌استارت دارد.", "Reset the network? A restart will be required."),
        ["nt_clear"] = ("پاک کردن خروجی", "Clear Output"),
        ["nt_dns_server"] = ("سرور DNS", "DNS Server"),
        ["nt_record_type"] = ("نوع رکورد", "Record Type"),
        ["nt_hop"] = ("پرش", "Hop"),
        ["nt_timeout"] = ("timeout", "timeout"),

        // ---- Adapters ----
        ["ad_ethernet"] = ("اترنت", "Ethernet"),
        ["ad_wifi"] = ("وای‌فای", "Wi-Fi"),
        ["ad_virtual"] = ("مجازی", "Virtual"),
        ["ad_vpn"] = ("VPN", "VPN"),
        ["ad_ip"] = ("آدرس IP", "IP Address"),
        ["ad_gateway"] = ("گیت‌وی", "Gateway"),
        ["ad_link_speed"] = ("سرعت لینک", "Link Speed"),
        ["ad_mac"] = ("آدرس MAC", "MAC Address"),
        ["ad_no_adapters"] = ("کارت شبکه‌ای یافت نشد", "No adapters found"),
        ["ad_up"] = ("وصل", "Up"),
        ["ad_down"] = ("قطع", "Down"),
        ["ad_enable"] = ("فعال‌سازی", "Enable"),
        ["ad_disable"] = ("غیرفعال‌سازی", "Disable"),
        ["ad_enabled_msg"] = ("کارت شبکه فعال شد", "Adapter enabled"),
        ["ad_disabled_msg"] = ("کارت شبکه غیرفعال شد", "Adapter disabled"),
        ["ad_dhcp"] = ("خودکار", "DHCP"),
        ["ad_static"] = ("دستی", "Static"),

        // ---- Settings ----
        ["st_language"] = ("زبان رابط", "Interface Language"),
        ["st_persian"] = ("فارسی", "Persian"),
        ["st_english"] = ("English", "English"),
        ["st_minimize_to_tray"] = ("کوچک‌شدن به Tray", "Minimize to tray"),
        ["st_about"] = ("درباره", "About"),
        ["st_version"] = ("نسخه", "Version"),
        ["st_creator"] = ("سازنده", "Creator"),
        ["st_tray_hint"] = ("NetPilot در سینی ویندوز فعال می‌ماند", "NetPilot stays in the Windows tray"),
        ["st_saved"] = ("تنظیمات ذخیره شد", "Settings saved"),

        // ---- Tray ----
        ["tray_open"] = ("باز کردن NetPilot", "Open NetPilot"),
        ["tray_current_dns"] = ("DNS فعلی", "Current DNS"),
        ["tray_quick_dns"] = ("DNS سریع", "Quick DNS"),
        ["tray_restore"] = ("بازگردانی DNS اصلی", "Restore Original DNS"),
        ["tray_exit"] = ("خروج", "Exit"),
        ["tray_changed"] = ("DNS در Tray تغییر کرد", "DNS changed from tray"),
    });
}
