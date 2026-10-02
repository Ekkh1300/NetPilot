using System.Collections.Generic;

namespace NetPilot.Lang;

static partial class Strings
{
    public static void RegisterDns() => Lang.Register(new Dictionary<string, (string, string)>
    {
        // ---- DNS Manager ----
        ["dns_current"] = ("DNS فعلی سیستم", "Current System DNS"),
        ["dns_servers_list"] = ("سرورهای DNS", "DNS Servers"),
        ["dns_add_custom"] = ("افزودن DNS سفارشی", "Add Custom DNS"),
        ["dns_custom_name"] = ("نام DNS", "DNS Name"),
        ["dns_primary"] = ("سرور اصلی", "Primary Server"),
        ["dns_secondary"] = ("سرور فرعی (اختیاری)", "Secondary Server (optional)"),
        ["dns_invalid_ip"] = ("آدرس IP معتبر نیست", "Invalid IP address"),
        ["dns_name_required"] = ("نام را وارد کنید", "Enter a name"),
        ["dns_applied"] = ("DNS با موفقیت اعمال شد", "DNS applied successfully"),
        ["dns_restore_done"] = ("DNS قبلی بازگردانی شد", "Original DNS restored"),
        ["dns_no_backup"] = ("نسخه پشتیبانی از DNS قبلی موجود نیست", "No DNS backup found"),
        ["dns_flushed"] = ("کش DNS پاک شد", "DNS cache flushed"),
        ["dns_search_ph"] = ("جستجوی DNS…", "Search DNS…"),
        ["dns_favorite"] = ("علاقه‌مندی", "Favorite"),
        ["dns_favorites_only"] = ("فقط علاقه‌مندی‌ها", "Favorites only"),
        ["dns_adapter"] = ("کارت شبکه", "Adapter"),
        ["dns_provider"] = ("ارائه‌دهنده", "Provider"),
        ["dns_auto"] = ("خودکار (DHCP)", "Automatic (DHCP)"),
        ["dns_delete_custom"] = ("حذف این DNS سفارشی؟", "Delete this custom DNS?"),
        ["dns_select_hint"] = ("یک DNS را برای اعمال انتخاب کنید", "Select a DNS to apply"),
        ["dns_backup_saved"] = ("نسخه پشتیبان ذخیره شد", "Backup saved"),

        // ---- Benchmark ----
        ["bm_run"] = ("شروع بنچ‌مارک", "Run Benchmark"),
        ["bm_stop"] = ("توقف", "Stop"),
        ["bm_running"] = ("در حال تست…", "Testing…"),
        ["bm_rounds"] = ("تعداد تست‌ها", "Rounds"),
        ["bm_results"] = ("نتایج", "Results"),
        ["bm_rank"] = ("رتبه", "Rank"),
        ["bm_response_time"] = ("زمان پاسخ", "Response Time"),
        ["bm_packet_loss"] = ("اتلاف بسته", "Packet Loss"),
        ["bm_avg"] = ("میانگین", "Average"),
        ["bm_chart"] = ("نمودار مقایسه‌ای", "Comparison Chart"),
        ["bm_no_results"] = ("هنوز نتیجه‌ای ثبت نشده", "No results yet"),
        ["bm_best"] = ("سریع‌ترین پاسخ", "Best"),
        ["bm_best_hint"] = ("سریع‌ترین پاسخ", "Fastest response"),
        ["bm_dns_failed"] = ("پاسخی دریافت نشد", "No response"),
        ["bm_summary"] = ("خلاصه", "Summary"),
        ["bm_tested"] = ("تست‌شده", "Tested"),
        ["bm_select_all"] = ("انتخاب همه", "Select All"),
        ["bm_compare_note"] = ("پاسخ واقعی سرور DNS به درخواست اندازه‌گیری می‌شود", "Measures real DNS server reply time"),

        // ---- Smart DNS ----
        ["sd_title"] = ("تحلیل و پیشنهاد هوشمند", "Smart Analysis & Suggestions"),
        ["sd_scan"] = ("اسکن DNSهای سریع", "Scan Fastest DNS"),
        ["sd_scanning"] = ("در حال اسکن…", "Scanning…"),
        ["sd_recommendation"] = ("پیشنهاد", "Recommendation"),
        ["sd_no_recommend"] = ("ابتدا اسکن را اجرا کنید", "Run a scan first"),
        ["sd_current_dns"] = ("DNS فعلی", "Current DNS"),
        ["sd_apply_suggested"] = ("اعمال پیشنهاد", "Apply Suggestion"),
        ["sd_retest"] = ("تست مجدد بعد از تغییر", "Re-test After Change"),
        ["sd_improvement"] = ("بهبود", "Improvement"),
        ["sd_scan_done"] = ("اسکن کامل شد", "Scan complete"),
        ["sd_faster_count"] = ("DNS سریع‌تر از فعلی", "DNSs faster than current"),
        ["sd_analyzing"] = ("در حال تحلیل وضعیت فعلی…", "Analyzing current state…"),
    });
}
