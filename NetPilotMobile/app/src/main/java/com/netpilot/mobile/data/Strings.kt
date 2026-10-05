package com.netpilot.mobile.data

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import java.util.Locale

/**
 * Bilingual string table (English / فارسی) — the mobile counterpart of the desktop app's
 * Lang service. Keys are stable; [L] is the composable accessor that recomposes when the
 * language changes.
 */
object Strings {

    /**
     * The port the PC bridge listens on, quoted in the troubleshooting hint.
     *
     * Hardcoding it in a string would be one more place to forget when the port changes, and
     * the mistake is exactly what the hint is about (8788 is this phone's own proxy port).
     */
    private val PcBridgePortHint: String get() = com.netpilot.mobile.pc.PcBridge.DEFAULT_PORT.toString()

    /** "en" or "fa". Observed by every composable that calls [L]. */
    var lang by mutableStateOf("en")
        private set

    fun setLanguage(code: String) {
        lang = if (code == "fa") "fa" else "en"
    }

    private val map: Map<String, Pair<String, String>> = listOf(
        // ---- shell / navigation
        "app_title" to ("NetPilot" to "NetPilot"),
        "nav_dashboard" to ("Dashboard" to "داشبورد"),
        "nav_dns" to ("DNS" to "DNS"),
        "nav_monitor" to ("Monitor" to "مانیتور"),
        "nav_apps" to ("Apps" to "برنامه‌ها"),
        "nav_tools" to ("Tools" to "ابزارها"),
        "nav_more" to ("More" to "بیشتر"),
        "menu_dns_manager" to ("DNS Manager" to "مدیریت DNS"),
        "menu_benchmark" to ("DNS Benchmark" to "آزمون DNS"),
        "menu_smart_dns" to ("Smart DNS" to "DNS هوشمند"),
        "menu_monitor" to ("Network Monitor" to "مانیتور شبکه"),
        "menu_per_app" to ("Per-App Usage" to "مصرف هر برنامه"),
        "menu_limiter" to ("Net Limiter" to "محدودکنندهٔ شبکه"),
        "menu_schedules" to ("Scheduled Limits" to "زمان‌بندی محدودیت"),
        "menu_history" to ("Network History" to "تاریخچهٔ شبکه"),
        "menu_events" to ("Connection History" to "تاریخچهٔ اتصال"),
        "menu_tools" to ("Network Tools" to "ابزارهای شبکه"),
        "menu_adapters" to ("Adapters" to "کارت‌های شبکه"),
        "menu_profiles" to ("Profiles" to "پروفایل‌ها"),
        // The bridge hands this phone's local tunnel to the PC. It never connects to a VPN
        // server, so the feature is not called a VPN - a Play listing that says "VPN"
        // invites policy questions the app cannot answer. Strings that really are about
        // somebody's VPN (mv_pc_vpn_*, mv_blocked_vpn) keep the word.
        "menu_mobile_vpn" to ("Phone Tunnel → PC" to "تونل گوشی → PC"),
        "menu_settings" to ("Settings" to "تنظیمات"),
        "menu_diagnostics" to ("Diagnostics" to "عیب‌یابی"),

        // ---- diagnostics / the log
        // Real Persian rather than English in both columns. The desktop's diagnostics page
        // shipped with English on both sides and stayed English inside an otherwise Persian app,
        // reading as finished - which is what this set of strings is here to avoid.
        //
        // The counter labels are written so the number follows the word, which is the Persian
        // order; a transliteration of "dropped: 0" would put the colon and number where a
        // Persian reader does not expect them.
        "diag_title" to ("Diagnostics" to "عیب‌یابی"),
        "diag_copy" to ("Copy report" to "کپی گزارش"),
        "diag_path" to ("Log file:" to "فایل لاگ:"),
        "diag_path_none" to (
            "(memory only - no writable directory)" to "(فقط در حافظه — پوشهٔ قابل نوشتنی نیست)"
        ),
        "diag_written" to ("written" to "نوشته"),
        "diag_dropped" to ("dropped" to "افتاده"),
        "diag_queued" to ("queued" to "در صف"),
        "diag_errors" to ("errors" to "خطا"),
        "diag_filter_hint" to ("Search the log text" to "جست‌وجو در متن لاگ"),
        "diag_empty" to ("Nothing logged at this level yet. The app writes here as it runs; if you were expecting an error, switch the level down to TRACE." to "در این سطح چیزی ثبت نشده است. اپ هنگام کار اینجا می‌نویسد؛ اگر منتظر خطا بودید، سطح را روی TRACE بگذارید."),

        // ---- common actions
        "apply" to ("Apply" to "اعمال"),
        "restore" to ("Restore" to "بازگردانی"),
        "save" to ("Save" to "ذخیره"),
        "cancel" to ("Cancel" to "لغو"),
        "delete" to ("Delete" to "حذف"),
        "edit" to ("Edit" to "ویرایش"),
        "add" to ("Add" to "افزودن"),
        "close" to ("Close" to "بستن"),
        "ok" to ("OK" to "تأیید"),
        "search" to ("Search" to "جستجو"),
        "sort" to ("Sort" to "مرتب‌سازی"),
        "enable" to ("Enable" to "فعال"),
        "disable" to ("Disable" to "غیرفعال"),
        "refresh" to ("Refresh" to "تازه‌سازی"),
        "start" to ("Start" to "شروع"),
        "stop" to ("Stop" to "توقف"),
        "running" to ("Running" to "در حال اجرا"),
        "stopped" to ("Stopped" to "متوقف"),
        "none" to ("None" to "هیچ"),
        "all" to ("All" to "همه"),
        "copy" to ("Copy" to "کپی"),
        "copied" to ("Copied" to "کپی شد"),
        "retry" to ("Retry" to "تلاش دوباره"),
        "unknown" to ("Unknown" to "نامشخص"),
        "yes" to ("Yes" to "بله"),
        "no" to ("No" to "خیر"),
        "back" to ("Back" to "بازگشت"),
        "clear" to ("Clear" to "پاک‌کردن"),
        "details" to ("Details" to "جزئیات"),

        // ---- dashboard
        "dash_network_health" to ("Network Health" to "سلامت شبکه"),
        "dash_connected" to ("Connected" to "متصل"),
        "dash_disconnected" to ("Disconnected" to "قطع"),
        "dash_download" to ("Download" to "دانلود"),
        "dash_upload" to ("Upload" to "آپلود"),
        "dash_ping" to ("Ping" to "پینگ"),
        "dash_packet_loss" to ("Packet Loss" to "اتلاف بسته"),
        "dash_active_dns" to ("Active DNS" to "DNS فعال"),
        "dash_system_dns" to ("System DNS" to "DNS سیستم"),
        "dash_dns_auto" to ("Automatic" to "خودکار"),
        "dash_quick_dns" to ("Quick DNS Switch" to "تعویض سریع DNS"),
        "dash_take_a_look" to ("Idle" to "غیرفعال"),
        "dash_traffic_live" to ("Live traffic" to "ترافیک لحظه‌ای"),
        "dash_vpn_on" to ("Tunnel active" to "تونل فعال"),
        "dash_vpn_off" to ("Tunnel off" to "تونل خاموش"),

        // ---- DNS manager
        "dns_current" to ("Current DNS" to "DNS فعلی"),
        "dns_presets" to ("Providers" to "ارائه‌دهندگان"),
        "dns_custom" to ("Custom" to "سفارشی"),
        "dns_add_custom" to ("Add custom DNS" to "افزودن DNS سفارشی"),
        "dns_name" to ("Name" to "نام"),
        "dns_primary" to ("Primary" to "اصلی"),
        "dns_secondary" to ("Secondary" to "پشتیبان"),
        "dns_favorites" to ("Favorites" to "مورد علاقه‌ها"),
        "dns_restore_system" to ("Restore system DNS" to "بازگردانی DNS سیستم"),
        "dns_applied" to ("DNS applied" to "DNS اعمال شد"),
        "dns_restored" to ("System DNS restored" to "DNS سیستم بازگردانده شد"),
        "dns_invalid_ip" to ("Invalid IP address" to "آدرس IP نامعتبر"),
        "dns_tunnel_hint" to ("Applied through the local tunnel — no root needed."
                to "از طریق تونل محلی اعمال می‌شود — بدون روت."),
        "dns_servers" to ("Servers" to "سرورها"),
        "dns_no_favorites" to ("No favourites yet" to "هنوز مورد علاقه‌ای نیست"),

        // ---- benchmark
        "bench_title" to ("DNS Benchmark" to "آزمون DNS"),
        "bench_run" to ("Run benchmark" to "اجرای آزمون"),
        "bench_running" to ("Testing resolvers…" to "در حال آزمون رزولورها…"),
        "bench_round" to ("Round" to "دور"),
        "bench_response" to ("Response" to "پاسخ"),
        "bench_avg" to ("Average" to "میانگین"),
        "bench_jitter" to ("Jitter" to "نوسان"),
        "bench_loss" to ("Loss" to "اتلاف"),
        "bench_rank" to ("Rank" to "رتبه"),
        "bench_best" to ("Fastest" to "سریع‌ترین"),
        "bench_finish" to ("Done" to "پایان"),
        "bench_no_results" to ("No results yet" to "هنوز نتیجه‌ای نیست"),
        "bench_selected" to ("Testing only favourites + applied" to "فقط مورد علاقه‌ها و DNS فعال"),
        "bench_all_resolvers" to ("All providers" to "همهٔ ارائه‌دهندگان"),
        "bench_apply_best" to ("Apply fastest" to "اعمال سریع‌ترین"),
        "bench_retest" to ("Test again" to "آزمون دوباره"),

        // ---- smart dns
        "smart_title" to ("Smart DNS" to "DNS هوشمند"),
        "smart_detect" to ("Detect current resolver" to "تشخیص رزولور فعلی"),
        "smart_suggest" to ("Suggested" to "پیشنهادی"),
        "smart_suggestion_hint" to ("Based on the last real benchmark on this network"
                to "بر اساس آخرین آزمون واقعی روی این شبکه"),
        "smart_run" to ("Run detection" to "اجرای تشخیص"),
        "smart_retest" to ("Re-test after change" to "آزمون مجدد پس از تغییر"),
        "smart_no_data" to ("Run a benchmark first" to "ابتدا یک آزمون اجرا کنید"),
        "smart_changed" to ("Applied %1\$s" to "%1\$s اعمال شد"),
        "smart_improve" to ("%1\$s ms faster than the current resolver"
                to "%1\$s میلی‌ثانیه سریع‌تر از رزولور فعلی"),

        // ---- monitor
        "mon_title" to ("Network Monitor" to "مانیتور شبکه"),
        "mon_live" to ("Live" to "زنده"),
        "mon_speed" to ("Speed" to "سرعت"),
        "mon_latency" to ("Latency" to "تأخیر"),
        "mon_total_down" to ("Total download" to "کل دانلود"),
        "mon_total_up" to ("Total upload" to "کل آپلود"),
        "mon_session" to ("Session" to "نشست"),
        "mon_since" to ("Since" to "از"),
        "mon_paused" to ("Paused" to "متوقف"),
        "mon_wifi" to ("Wi-Fi" to "وای‌فای"),
        "mon_cellular" to ("Cellular" to "دیتای همراه"),
        "mon_ethernet" to ("Ethernet" to "اترنت"),
        "mon_vpn" to ("Tunnel" to "تونل"),
        "mon_none" to ("No network" to "بدون شبکه"),

        // ---- per-app usage
        "apps_title" to ("Per-App Usage" to "مصرف هر برنامه"),
        "apps_grant" to ("Usage access is required" to "دسترسی آمار مصرف لازم است"),
        "apps_grant_desc" to ("Grant usage access so per-app totals can be read from Android."
                to "دسترسی آمار مصرف را بدهید تا مصرف هر برنامه از اندروید خوانده شود."),
        "apps_grant_action" to ("Open settings" to "باز کردن تنظیمات"),
        "apps_process" to ("Package" to "بسته"),
        "apps_rate" to ("Speed" to "سرعت"),
        "apps_total" to ("Total" to "جمع"),
        "apps_down" to ("Down" to "دانلود"),
        "apps_up" to ("Up" to "آپلود"),
        "apps_no_data" to ("No usage yet" to "هنوز مصرفی ثبت نشده"),
        "apps_sort_total" to ("Total" to "جمع"),
        "apps_sort_rate" to ("Live speed" to "سرعت لحظه‌ای"),
        "apps_sort_name" to ("Name" to "نام"),
        "apps_sort_down" to ("Download" to "دانلود"),
        "apps_sort_up" to ("Upload" to "آپلود"),

        // ---- net limiter
        "lim_title" to ("Net Limiter" to "محدودکنندهٔ شبکه"),
        "lim_allow" to ("Allow" to "مجاز"),
        "lim_limit" to ("Limit" to "محدود"),
        "lim_block" to ("Block" to "مسدود"),
        "lim_down_limit" to ("Download limit" to "محدودیت دانلود"),
        "lim_up_limit" to ("Upload limit" to "محدودیت آپلود"),
        "lim_unlimited" to ("Unlimited" to "نامحدود"),
        "lim_custom" to ("Custom" to "سفارشی"),
        "lim_kbs" to ("KB/s" to "کیلوبایت/ثانیه"),
        "lim_mbs" to ("MB/s" to "مگابایت/ثانیه"),
        "lim_engine_on" to ("Enforcement engine active" to "موتور اعمال فعال است"),
        "lim_engine_off" to ("Start the tunnel to enforce rules"
                to "برای اعمال قوانین تونل را روشن کنید"),
        "lim_empty" to ("No rules yet" to "هنوز قانونی نیست"),
        "lim_rules_active" to ("%1\$s active rules" to "%1\$s قانون فعال"),
        "lim_preset" to ("Presets" to "مقادیر آماده"),
        "lim_block_hint" to ("Blocked apps get no traffic while the tunnel is on"
                to "تا وقتی تونل روشن است برنامهٔ مسدود هیچ ترافیکی ندارد"),

        // ---- firewall engine (FIREWALL mode)
        "fw_title" to ("Firewall" to "فایروال"),
        "fw_start" to ("Start firewall" to "راه‌اندازی فایروال"),
        "fw_stop" to ("Stop firewall" to "توقف فایروال"),
        "fw_running" to ("Firewall is enforcing rules"
                to "فایروال در حال اجرای قوانین است"),
        "fw_dns_only" to ("DNS only — rules are not enforced"
                to "فقط DNS — قوانین اعمال نمی‌شوند"),
        "fw_note" to ("Firewall mode carries all traffic through this device, so apps " +
                "match the rules below. Blocking is immediate; limits use a token bucket " +
                "per direction, and UDP is throttled by dropping what exceeds the budget."
                to "حالت فایروال همهٔ ترافیک را از همین دستگاه عبور می‌دهد تا قوانین زیر " +
                "اجرا شوند. مسدودسازی فوری است؛ محدودیت با سطل توکن در هر جهت اعمال می‌شود " +
                "و ترافیک UDP با حذف بیش از سقف کنترل می‌شود."),
        "fw_needs_api29" to ("Rate limits and per-app blocking need Android 10 or newer"
                to "سقف سرعت و مسدودسازی برنامه‌ها به اندروید ۱۰ یا جدیدتر نیاز دارد"),
        "fw_blocked_n" to ("%1\$s packets blocked" to "%1\$s بسته مسدود شد"),
        "fw_relayed_n" to ("%1\$s flows relayed" to "%1\$s جیانه عبور داده شد"),
        "fw_unidentified_n" to ("%1\$s flows unidentified"
                to "%1\$s جیانه شناسایی نشده"),
        "fw_no_attribution" to ("This build does not report which app owns a connection, " +
                "so rules cannot be matched. Blocking is switched off rather than " +
                "blackholing the device."
                to "این سیستم‌عامل مالک اتصال را گزارش نمی‌دهد، پس تطبیق قوانین ممکن نیست. " +
                "به‌جای قطع کل دستگاه، مسدودسازی غیرفعال شد."),
        "fw_icmp_note" to ("ICMP (ping) is not relayed in firewall mode"
                to "در حالت فایروال ICMP (پینگ) عبور داده نمی‌شود"),

        // ---- schedules
        "sch_title" to ("Scheduled Limits" to "زمان‌بندی محدودیت"),
        "sch_add" to ("New schedule" to "زمان‌بندی جدید"),
        "sch_from" to ("From" to "از"),
        "sch_to" to ("To" to "تا"),
        "sch_limit" to ("Limit" to "محدودیت"),
        "sch_example" to ("Example: Steam 00:00 → 08:00 limited to 1 MB/s"
                to "مثال: استیم ۰۰:۰۰ تا ۰۸:۰۰ با محدودیت ۱ مگابایت/ثانیه"),
        "sch_empty" to ("No schedules" to "زمان‌بندی‌ای نیست"),
        "sch_pick_app" to ("Pick an app first" to "ابتدا یک برنامه انتخاب کنید"),
        "sch_apply_hint" to ("Schedules are re-evaluated every minute"
                to "زمان‌بندی‌ها هر دقیقه دوباره بررسی می‌شوند"),

        // ---- history
        "his_title" to ("Network History" to "تاریخچهٔ شبکه"),
        "his_daily" to ("Daily" to "روزانه"),
        "his_weekly" to ("Weekly" to "هفتگی"),
        "his_monthly" to ("Monthly" to "ماهانه"),
        "his_top_app" to ("Top app" to "پرمصرف‌ترین برنامه"),
        "his_no_data" to ("No history yet" to "هنوز تاریخچه‌ای نیست"),
        "his_range" to ("Range" to "بازه"),
        "his_combined" to ("Total usage" to "کل مصرف"),

        // ---- connection history
        "evt_title" to ("Connection History" to "تاریخچهٔ اتصال"),
        "evt_dns_changed" to ("DNS changed" to "DNS تغییر کرد"),
        "evt_dns_restored" to ("DNS restored" to "DNS بازگردانده شد"),
        "evt_limit_on" to ("Limit enabled" to "محدودیت فعال شد"),
        "evt_limit_off" to ("Limit disabled" to "محدودیت غیرفعال شد"),
        "evt_blocked" to ("Application blocked" to "برنامه مسدود شد"),
        "evt_unblocked" to ("Application unblocked" to "برنامه از مسدود خارج شد"),
        "evt_net_reset" to ("Network reset" to "بازنشانی شبکه"),
        "evt_profile" to ("Profile applied" to "پروفایل اعمال شد"),
        "evt_vpn_start" to ("Tunnel started" to "تونل شروع شد"),
        "evt_vpn_stop" to ("Tunnel stopped" to "تونل متوقف شد"),
        "evt_pc_pair" to ("PC paired" to "رایانه جفت شد"),
        "evt_pc_unpair" to ("PC unpaired" to "جفت‌سازی رایانه قطع شد"),
        "evt_pc_found" to ("PC discovered on LAN" to "رایانه در شبکه پیدا شد"),
        "evt_pc_share" to ("Sharing started" to "اشتراک‌گذاری آغاز شد"),
        "evt_pc_stop" to ("Sharing stopped" to "اشتراک‌گذاری متوقف شد"),
        "evt_fw_start" to ("Firewall started" to "فایروال شروع شد"),
        "evt_fw_unsupported" to ("Firewall needs Android 10+" to "فایروال به اندروید ۱۰+ نیاز دارد"),
        "evt_tun_lost" to (
            "Tunnel lost - connection restored" to "تونل از دست رفت - اتصال بازگشت"
            ),
        "evt_relay_lost" to (
            "Forwarding stopped - connection restored" to "ارسال‌سازی متوقف شد - اتصال بازگشت"
            ),
        "evt_empty" to ("Nothing recorded yet" to "هنوز رویدادی ثبت نشده"),

        // ---- network tools
        "tools_title" to ("Network Tools" to "ابزارهای شبکه"),
        "tools_flush" to ("Flush DNS cache" to "پاک‌سازی کش DNS"),
        "tools_renew" to ("Renew IP" to "تازه‌سازی IP"),
        "tools_reset_net" to ("Reset network" to "بازنشانی شبکه"),
        "tools_ping" to ("Ping" to "پینگ"),
        "tools_trace" to ("Traceroute" to "ترس‌روت"),
        "tools_nslookup" to ("NSLookup" to "NSLookup"),
        "tools_host" to ("Host" to "میزبان"),
        "tools_run" to ("Run" to "اجرا"),
        "tools_output" to ("Output" to "خروجی"),
        "tools_running" to ("Running…" to "در حال اجرا…"),
        "tools_done" to ("Finished" to "پایان"),
        "tools_empty" to ("Output will appear here" to "خروجی اینجا نمایش داده می‌شود"),
        "tools_flushed" to ("Resolver cache cleared" to "کش رزولور پاک شد"),
        "tools_renewed" to ("Network lease refreshed" to "اجارهٔ شبکه تازه شد"),
        "tools_hops" to ("hops" to "پرش"),
        "tools_ttl" to ("TTL" to "TTL"),

        // ---- adapters
        "adp_title" to ("Network Adapters" to "کارت‌های شبکه"),
        "adp_status" to ("Status" to "وضعیت"),
        "adp_ip" to ("IP address" to "آدرس IP"),
        "adp_gateway" to ("Gateway" to "درگاه"),
        "adp_dns" to ("DNS" to "DNS"),
        "adp_link_speed" to ("Link speed" to "سرعت لینک"),
        "adp_wifi" to ("Wi-Fi" to "وای‌فای"),
        "adp_ethernet" to ("Ethernet" to "اترنت"),
        "adp_cellular" to ("Cellular" to "دیتای همراه"),
        "adp_vpn" to ("Tunnel (NetPilot)" to "تونل (NetPilot)"),
        "adp_active" to ("Active" to "فعال"),
        "adp_inactive" to ("Inactive" to "غیرفعال"),
        "adp_ssid" to ("Network" to "شبکه"),
        "adp_bssid" to ("Access point" to "نقطهٔ دسترسی"),
        "adp_frequency" to ("Frequency" to "فرکانس"),
        "adp_none" to ("No adapter info available" to "اطلاعات کارتی در دسترس نیست"),

        // ---- health / orb
        "hlt_excellent" to ("Excellent" to "عالی"),
        "hlt_good" to ("Good" to "خوب"),
        "hlt_fair" to ("Fair" to "متوسط"),
        "hlt_poor" to ("Poor" to "ضعیف"),
        "hlt_dns" to ("DNS" to "DNS"),
        "hlt_latency" to ("Latency" to "تأخیر"),
        "hlt_loss" to ("Packet loss" to "اتلاف بسته"),
        "hlt_connection" to ("Connection" to "اتصال"),
        "hlt_stable" to ("Stable" to "پایدار"),
        "hlt_unstable" to ("Unstable" to "ناپایدار"),
        "hlt_na" to ("—" to "—"),
        "orb_idle" to ("Network idle" to "شبکه بی‌کار"),
        "orb_down" to ("Downloading" to "در حال دانلود"),
        "orb_up" to ("Uploading" to "در حال آپلود"),
        "orb_mixed" to ("Transferring" to "در حال انتقال"),
        "orb_off" to ("No connection" to "بدون اتصال"),

        // ---- profiles
        "prf_title" to ("Profiles" to "پروفایل‌ها"),
        "prf_gaming" to ("Gaming" to "گیمینگ"),
        "prf_browsing" to ("Browsing" to "وب‌گردی"),
        "prf_default" to ("Default" to "پیش‌فرض"),
        "prf_new" to ("New profile" to "پروفایل جدید"),
        "prf_contains" to ("DNS + limits + blocked apps" to "DNS + محدودیت‌ها + برنامه‌های مسدود"),
        "prf_applied" to ("Profile applied" to "پروفایل اعمال شد"),
        "prf_delete_confirm" to ("Delete this profile?" to "این پروفایل حذف شود؟"),
        "prf_empty" to ("No custom profiles" to "پروفایل سفارشی‌ای نیست"),
        "prf_builtin" to ("Built-in" to "داخلی"),

        // ---- settings
        "st_title" to ("Settings" to "تنظیمات"),
        "st_language" to ("Language" to "زبان"),
        "st_english" to ("English" to "انگلیسی"),
        "st_persian" to ("Persian (فارسی)" to "فارسی"),
        "st_creator" to ("Creator" to "سازنده"),
        "st_about" to ("About" to "درباره"),
        "st_version" to ("Version" to "نسخه"),
        "st_monitor_interval" to ("Monitor interval" to "بازهٔ نمونه‌برداری"),
        "st_battery" to ("Ignore battery optimisation" to "نادیده‌گرفتن بهینه‌سازی باتری"),
        "st_battery_hint" to ("Keeps background monitor and schedules alive"
                to "مانیتور پس‌زمینه و زمان‌بندی‌ها را زنده نگه می‌دارد"),
        "st_theme" to ("Appearance" to "ظاهر"),
        "st_dark" to ("Dark" to "تیره"),
        "st_export" to ("Export settings" to "خروجی تنظیمات"),
        "st_import" to ("Import settings" to "ورودی تنظیمات"),
        "st_import_empty" to (
            "Clipboard is empty - export first, then paste" to "کلیپ‌بورد خالی است - اول خروجی بگیرید"
            ),
        "st_reset" to ("Reset all data" to "بازنشانی همهٔ داده‌ها"),
        "st_data_reset" to ("All local data cleared" to "همهٔ داده‌های محلی پاک شد"),

        // ---- short labels used by the PC bridge page
        "backup_short" to ("Backup PC" to "پشتیبان رایانه"),
        "restore_short" to ("Restore PC" to "بازگردانی رایانه"),

        // ---- phone tunnel / PC bridge
        "mv_title" to ("Phone Tunnel → PC" to "تونل گوشی → PC"),
        "mv_desc" to ("Share this phone's tunnel with your Windows PC over USB, hotspot or LAN."
                to "تونل این گوشی را از طریق USB، هات‌اسپات یا LAN با رایانهٔ ویندوز به اشتراک بگذارید."),
        "mv_pc_host" to ("PC address" to "آدرس رایانه"),
        "mv_pc_port" to ("Port" to "پورت"),
        "mv_troubleshoot" to ("Cannot reach the PC" to "دسترسی به رایانه ممکن نیست"),
        "mv_ts1" to (
            "1 · Run NetPilot on Windows as administrator (it must listen on the network)" to
                "۱ · نات‌پایلوت را در ویندوز با دسترسی مدیر اجرا کنید (باید روی شبکه گوش بدهد)"
            ),
        "mv_ts2" to (
            "2 · The bridge port is ${PcBridgePortHint} - not 8788, which is this phone's own proxy" to
                "۲ · پورت پل ${PcBridgePortHint} است - نه ۸۷۸۸ که پروکسی خودِ گوشی است"
            ),
        "mv_ts3" to (
            "3 · One network (a shared Wi-Fi, the phone's hotspot or USB tethering) and the PC's firewall allows the port" to
                "۳ · یک شبکه (وای‌فای مشترک، هات‌اسپات یا اشتراک USB) و فایروال رایانه این پورت را باز کند"
            ),
        "mv_pair_code" to ("Pairing code" to "کد جفت‌سازی"),
        "mv_pair" to ("Pair" to "جفت‌سازی"),
        "mv_paired" to ("Paired" to "جفت شده"),
        "mv_not_paired" to ("Not paired" to "جفت نشده"),
        "mv_unpair" to ("Forget PC" to "فراموش کردن رایانه"),
        "mv_connect" to ("Connect" to "اتصال"),
        "mv_report" to ("Report state" to "گزارش وضعیت"),
        "mv_find" to ("Find PC" to "یافتن رایانه"),
        "mv_finding" to ("Searching the network…" to "در حال جستجوی شبکه…"),
        "mv_found" to ("PC found at %1\$s" to "رایانه پیدا شد: %1\$s"),
        "mv_not_found" to ("No NetPilot PC found — are both devices on the same network?"
                to "رایانه‌ای پیدا نشد — هر دو دستگاه روی یک شبکه هستند؟"),
        "mv_hint_refused" to ("Refused — start NetPilot on Windows as Administrator."
                to "رد شد — NetPilot ویندوز را با دسترسی Administrator اجرا کنید."),
        "mv_hint_unreachable" to ("Wrong address, or the two devices are on different networks."
                to "آدرس اشتباه است یا هر دو در شبکهٔ متفاوتی هستند."),
        "mv_hint_unresolved" to ("That address cannot be resolved — enter the PC's IP, or tap Find PC."
                to "این آدرس قابل ترجمه نیست — IP رایانه را وارد کنید یا «یافتن رایانه» را بزنید."),
        "mv_hint_timeout" to ("No answer in time — the PC may be asleep."
                to "پاسخی نرسید — شاید رایانه در خواب است."),
        "mv_shared" to ("PC now browses through this phone."
                to "رایانه اکنون از این گوشی استفاده می‌کند."),
        "mv_share_failed" to ("The PC refused the share" to "رایانه اشتراک‌گذاری را نپذیرفت"),
        "mv_link_note" to ("The link stays alive while paired, even in the background."
                to "تا زمانی که جفت هستید، پیوند حتی در پس‌زمینه زنده می‌ماند."),
        "mv_link_live" to ("PC linked · %1\$s" to "رایانه متصل · %1\$s"),
        "mv_link_wait" to ("PC not answering · %1\$s" to "رایانه پاسخ نمی‌دهد · %1\$s"),
        "mv_not_netpilot" to ("Something answers on that port, but it is not NetPilot"
                to "چیزی روی آن پورت پاسخ می‌دهد ولی NetPilot نیست"),
        "mv_err_auth" to ("Token rejected — pair again" to "توکن رد شد — دوباره جفت‌سازی کنید"),
        "mv_err_code" to ("Wrong or expired pairing code" to "کد جفت‌سازی اشتباه یا منقضی است"),
        "mv_err_busy" to ("Too many attempts — try again in a minute"
                to "تلاش زیاد — یک دقیقه بعد دوباره"),
        // Details the desktop answers with (translated here so the phone never shows a key)
        "mv_no_proxy" to ("The phone has not advertised a proxy yet"
                to "گوشی هنوز پراکسی‌ای اعلام نکرده است"),
        "mv_proxy_unreachable" to ("The phone's proxy does not answer — sharing not applied"
                to "پراکسی گوشی پاسخ نمی‌دهد — اشتراک‌گذاری اعمال نشد"),
        "mv_no_link" to ("The PC sees no link toward the phone"
                to "رایانه مسیری به گوشی نمی‌بیند"),
        "mv_blocked_vpn" to ("A VPN is active on the PC — NetPilot leaves it alone"
                to "روی رایانه وی‌پی‌ان فعال است — NetPilot دست نمی‌زند"),
        "mv_snapshot_none" to ("Nothing has been snapshotted yet" to "هنوز وضعیتی ذخیره نشده است"),
        "mv_share_started" to ("Sharing active" to "اشتراک‌گذاری فعال است"),
        "mv_snapshot_restored" to ("PC state restored" to "وضعیت رایانه بازگردانی شد"),
        "mv_share" to ("Start sharing" to "شروع اشتراک‌گذاری"),
        "mv_stop_share" to ("Stop sharing" to "توقف اشتراک‌گذاری"),
        "mv_proxy" to ("Local proxy" to "پراکسی محلی"),
        "mv_waiting" to ("Waiting for the PC…" to "در انتظار رایانه…"),
        "mv_hint_code" to ("Type the code shown in NetPilot on Windows"
                to "کدی را که در NetPilot ویندوز نشان داده می‌شود وارد کنید"),
        "mv_how" to ("How to connect" to "چگونه وصل شویم"),
        "mv_step1" to ("1 · Same Wi-Fi — or share the phone's hotspot / USB tethering"
                to "۱ · یک وای‌فای مشترک، یا هات‌اسپات/اشتراک USB گوشی"),
        "mv_step2" to ("2 · Open Phone Tunnel in NetPilot on Windows"
                to "۲ · در NetPilot ویندوز بخش Phone Tunnel را باز کنید"),
        "mv_step3" to ("3 · Enter the pairing code here and connect"
                to "۳ · کد جفت‌سازی را اینجا وارد کنید و وصل شویم"),
        "mv_snapshot_hint" to ("The PC's current state is snapshotted before any change."
                to "قبل از هر تغییر، وضعیت فعلی رایانه ذخیره می‌شود."),
        "mv_no_consent" to ("NetPilot never replaces the PC's VPN without your consent."
                to "NetPilot بدون اجازهٔ شما وی‌پی‌ان رایانه را جایگزین نمی‌کند."),
        "mv_error" to ("PC unreachable" to "رایانه در دسترس نیست"),

        // ---- notifications (tray equivalent)
        "notify_channel" to ("Quick DNS" to "DNS سریع"),
        "notify_tap" to ("Tap to open" to "برای باز کردن بزنید"),
        "notify_active" to ("Active: %1\$s" to "فعال: %1\$s"),

        // ---- misc status
        "err_no_network" to ("No network connection" to "اتصال شبکه‌ای وجود ندارد"),
        "err_permission" to ("Permission denied" to "دسترسی رد شد"),
        "err_unknown" to ("Something went wrong" to "مشکلی پیش آمد"),
        "loading" to ("Loading…" to "در حال بارگذاری…"),
        "seconds" to ("%1\$ds" to "%1\$d ثانیه"),
        "ms_unit" to ("%1\$d ms" to "%1\$d میلی‌ثانیه"),
        "unit_kbs" to ("KB/s" to "KB/s"),
        "unit_mbs" to ("MB/s" to "MB/s"),
        "unit_gbps" to ("Gbps" to "Gbps"),
        "unit_percent" to ("%1\$d%%" to "%1\$d%%"),
        "sample_docs" to ("Samples" to "نمونه")
    ).toMap()

    /** True when [key] exists — lets non-UI code decide if a server detail is translatable. */
    fun has(key: String): Boolean = map.containsKey(key)

    /** Translate [key]; falls back to the key itself so a missing entry is visible, not fatal. */
    fun raw(key: String): String {
        val pair = map[key] ?: return key
        return if (lang == "fa") pair.second else pair.first
    }
}

/** Composable string lookup — reads [Strings.lang] so it recomposes on language change. */
@androidx.compose.runtime.Composable
fun L(key: String): String = Strings.raw(key)

/** Non-composable lookup for services / non-UI code. */
fun LT(key: String): String = Strings.raw(key)

// ---------------------------------------------------------------------------- formatting

/** Human byte size, e.g. 1536 → "1.5 KB". Locale-independent digits. */
fun formatBytes(bytes: Long): String {
    if (bytes <= 0) return if (bytes < 0) "0 B" else "0 B"
    val units = arrayOf("B", "KB", "MB", "GB", "TB")
    var v = bytes.toDouble()
    var i = 0
    while (v >= 1024.0 && i < units.size - 1) { v /= 1024.0; i++ }
    return if (i == 0) "$bytes B"
    else String.format(Locale.US, "%.1f %s", v, units[i])
}

/** Human rate, e.g. 1_500_000 → "1.4 MB/s". */
fun formatRate(bytesPerSec: Long): String {
    if (bytesPerSec <= 0) return "0 B/s"
    val units = arrayOf("B/s", "KB/s", "MB/s", "GB/s")
    var v = bytesPerSec.toDouble()
    var i = 0
    while (v >= 1024.0 && i < units.size - 1) { v /= 1024.0; i++ }
    return if (i == 0) "$bytesPerSec B/s"
    else String.format(Locale.US, "%.1f %s", v, units[i])
}

/** Compact rate for dense chart labels. */
fun formatRateShort(bytesPerSec: Long): String = formatRate(bytesPerSec)

fun formatMs(ms: Int): String = if (ms < 0) "—" else "${ms} ms"

/** `NaN` is the "nothing measured yet" marker and reads as a dash, never as `NaN%`. */
fun formatPercent(p: Double): String =
    if (p.isNaN()) "—" else String.format(Locale.US, "%.1f%%", p)
