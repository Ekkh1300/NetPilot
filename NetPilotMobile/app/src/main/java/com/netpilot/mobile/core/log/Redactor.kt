package com.netpilot.mobile.core.log

/**
 * Removes secrets before anything reaches a file.
 *
 * This exists because people post logs in support threads, and because the phone holds a
 * pairing token that grants control of the PC's network. A log containing one hands the
 * machine to whoever can read the thread.
 *
 * It is deliberately narrow. Redacting too much destroys the diagnostic value: interface
 * names, addresses, byte counts and packet details are the reason the file exists, so none of
 * those are touched. What goes are credentials, pairing codes and hardware addresses.
 *
 * Mirrors NetPilot.Core.Logging.Redactor rule for rule. If one side gains a rule, the other
 * has to as well, or a log copied between the two would be redacted differently.
 */
object Redactor {

    const val MASK = "[redacted]"

    // "Authorization: Bearer <token>" - the pairing token travels this way.
    private val BEARER = Regex("""\b(Bearer\s+)[A-Za-z0-9._\-]{8,}""", RegexOption.IGNORE_CASE)

    // A JSON field named like a secret, with or without quotes on the key.
    private val JSON_SECRET = Regex(
        """(?i)"(token|pairingCode|pairing_code|pairingToken|secret|password|passwd|apiKey|api_key)"\s*:\s*"[^"]*""""
    )

    // A six-digit pairing code standing alone. Six bare digits are not something a log message
    // in this product legitimately contains.
    private val SIX_DIGIT_CODE = Regex("""(?<![\d.])\d{6}(?![\d.])""")

    // MAC addresses: hardware identity with no diagnostic value.
    private val MAC = Regex("""\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b""")

    @Synchronized
    fun apply(text: String?): String {
        if (text.isNullOrEmpty()) return text ?: ""
        // Order matters: the JSON rule has to consume a secret before the bare six-digit rule
        // can see a code inside it.
        var s = BEARER.replace(text) { m -> m.groupValues[1] + MASK }
        s = JSON_SECRET.replace(s) { m -> "\"" + m.groupValues[1] + "\":\"" + MASK + "\"" }
        s = MAC.replace(s, MASK)
        s = SIX_DIGIT_CODE.replace(s, MASK)
        return s
    }

    /**
     * Applies every rule to an exception's full text, including the stack trace and any cause.
     * A secret in a nested cause is still a secret.
     */
    @Synchronized
    fun apply(t: Throwable?): String {
        if (t == null) return ""
        val sb = StringBuilder()
        var e: Throwable? = t
        var depth = 0
        while (e != null && depth < 8) {
            if (depth > 0) sb.append(" ---> ")
            sb.append(e.javaClass.name).append(": ").append(e.message).append('\n')
            e.stackTrace.take(12).forEach { sb.append("\tat ").append(it).append('\n') }
            if (e is java.util.concurrent.ExecutionException && e.cause != null &&
                e.cause !== e && depth == 0
            ) {
                sb.append(" ---> ").append(e.cause!!.javaClass.name)
                    .append(": ").append(e.cause!!.message).append('\n')
            }
            e = e.cause
            depth++
        }
        return apply(sb.toString())
    }

    /** How many redactions a piece of text would trigger. Used by the tests, and shown in the
     * app's diagnostics so the user can see that something was masked rather than wondering
     * why a number looks odd. */
    @Synchronized
    fun countRedactions(text: String?): Int {
        if (text.isNullOrEmpty()) return 0
        return BEARER.findAll(text).count() +
            JSON_SECRET.findAll(text).count() +
            MAC.findAll(text).count() +
            SIX_DIGIT_CODE.findAll(text).count()
    }
}