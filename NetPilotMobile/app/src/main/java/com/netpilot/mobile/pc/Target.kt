package com.netpilot.mobile.pc

/**
 * Accepts whatever the user typed or pasted and returns the host and port to dial.
 *
 * Windows advertises its bridge address as `http://pc:8787`, and people paste that line
 * verbatim; handed to `URL()` it became `http://http://pc:8787:8787`, whose host parsed
 * as `http` and failed with UnknownHostException — reported to the user as "Wrong
 * address, or the two devices are on different networks." Scheme, path, whitespace and a
 * port pasted along with the host are absorbed here, so an address copied from either
 * side simply works.
 *
 * Deliberately a pure top-level function: this is the one part of the bridge that depends
 * on what a human types, so it lives where `TargetTest` can pin every shape of input
 * instead of being proven against a real phone.
 *
 * @param raw the field contents as typed.
 * @param fallbackPort the port to use when [raw] does not carry one.
 * @return the host (possibly blank when nothing usable was typed) and a port in 1..65535.
 */
fun normalizeTarget(raw: String, fallbackPort: Int): Pair<String, Int> {
    val fallback = fallbackPort.coerceIn(1, 65535)

    var s = raw.trim().replace(Regex("^https?://", RegexOption.IGNORE_CASE), "")
    val slash = s.indexOf('/')
    if (slash >= 0) s = s.substring(0, slash)
    s = s.trim().trimEnd(':')
    if (s.isEmpty()) return "" to fallback

    // Bracketed IPv6 — `[::1]:8787` — the port follows the closing bracket.
    if (s.startsWith("[")) {
        val close = s.indexOf(']')
        if (close > 1) {
            val rest = s.substring(close + 1)
            val p = if (rest.startsWith(":")) rest.substring(1).toIntOrNull() else null
            return s.substring(1, close) to (if (p != null && p in 1..65535) p else fallback)
        }
    }

    // host:port — never split a bare IPv6 literal: `2001:db8::1` has more than one colon.
    // A single colon is a separator even when what follows is not a number (`pc:abc`):
    // dropping the junk beats building `http://pc:abc:8787` out of it.
    if (s.count { it == ':' } == 1) {
        val colon = s.indexOf(':')
        val host = s.substring(0, colon).trim()
        val p = s.substring(colon + 1).toIntOrNull()
        return host to (if (p != null && p in 1..65535) p else fallback)
    }

    return s to fallback
}
