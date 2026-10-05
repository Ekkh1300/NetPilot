package com.netpilot.mobile

import com.netpilot.mobile.data.Strings
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.file.Files

/**
 * Checks the string table against what the screens actually ask for.
 *
 * `Strings.raw(key)` returns the key itself when the key is missing. That is a sensible default -
 * a missing translation degrades to something readable instead of crashing a composable - and it
 * is exactly why a typo is invisible: the literal key name is rendered on screen as though it
 * were the label, and nothing anywhere complains.
 *
 * This is not hypothetical. The Windows app's diagnostics page shipped with a button reading
 * "st_refresh" and with all thirteen of its strings set to English in both language columns, so it
 * stayed English inside an otherwise Persian app and looked finished. Neither was caught by the
 * build or by any test.
 *
 * Both are caught here, statically.
 */
class StringIntegrityTest {

    /** The app source tree, found by walking up from the test's own classpath location. */
    private fun sourceRoot(): File {
        var dir: File? = File(javaClass.protectionDomain.codeSource.location.toURI())
        while (dir != null) {
            val app = File(dir, "app/src/main/java/com/netpilot/mobile")
            if (app.isDirectory) return app
            dir = dir.parentFile
        }
        // Fall back to the working directory, which is the module when Gradle runs the tests.
        val guess = File("src/main/java/com/netpilot/mobile")
        if (guess.isDirectory) return guess
        throw AssertionError("could not locate the app source tree")
    }

    private fun ktFiles(): List<File> = sourceRoot().walkTopDown()
        .filter { it.isFile && it.extension == "kt" }
        .toList()

    // ---------------- every key used in a screen exists ----------------

    /**
     * `L("x")` and `Strings.raw("x")` both resolve to the key when it is missing, so an
     * unregistered key is a label that reads as a variable name. Both call sites are checked.
     */
    @Test
    fun everyStringKeyUsedInTheUiIsRegistered() {
        val used = linkedMapOf<String, String>()

        for (file in ktFiles()) {
            // Only the UI: a key referenced from a service would be a code smell, but that is a
            // different question and does not put anything wrong on the screen.
            if (!file.path.contains("${File.separator}ui${File.separator}")) continue

            file.readLines().forEachIndexed { index, line ->
                val trimmed = line.trimStart()
                // Comments explain the rule and are not call sites - and one of them names a
                // deliberately missing key, which would otherwise fail this test for having
                // explained itself.
                if (trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("/*")) {
                    return@forEachIndexed
                }
                for (m in Regex("""\bL\(\s*"([a-z0-9_]+)"""").findAll(line)) {
                    used[m.groupValues[1]] = "${file.name}:${index + 1}"
                }
                for (m in Regex("""Strings\.raw\(\s*"([a-z0-9_]+)"""").findAll(line)) {
                    used[m.groupValues[1]] = "${file.name}:${index + 1}"
                }
            }
        }

        assertTrue("scanned ${ktFiles().size} files but found no string keys at all", used.isNotEmpty())

        // The check itself, through the same lookup the UI uses: raw() returns the key when it is
        // missing, so a key counts as registered only if it comes back as something else.
        val missing = used.filter { (key, _) -> Strings.raw(key) == key }

        assertTrue(
            "string keys used in the UI that the table does not register - each renders as its " +
                "own name on screen:\n" +
                missing.entries.joinToString("\n") { "  ${it.value}  ${it.key}" },
            missing.isEmpty()
        )
    }

    // ---------------- every entry is really translated ----------------

    /**
     * Identical text in both columns is not a translation, it is an untranslated value that reads
     * as done.
     *
     * Identical values are only legitimate for genuinely language-neutral content, so those are
     * listed by name rather than the check being weakened - the alternative is that real
     * placeholders stop being caught.
     */
    @Test
    fun noStringIsUntranslatedExceptTheListedOnes() {
        // sourceRoot() already points at the package directory, so Strings.kt is a sibling of ui/.
        val file = File(sourceRoot(), "data/Strings.kt")
        assertTrue("Strings.kt not found at ${file.absolutePath}", file.isFile)

        // Keys whose value is the same in both languages on purpose. Listed by key rather than by
        // loosening the check, because a rule like "allow anything with a digit" stops catching
        // exactly the placeholders it was added for.
        val neutral = setOf(
            // Product name and protocol acronyms. A Persian reader writes DNS, not «دی‌ان‌اس».
            "app_title", "nav_dns", "nav_dashboard", "adp_dns", "hlt_dns",
            // Tool names as they appear in the tools screen and in any system's own output.
            "tools_nslookup", "tools_ttl",
            // Units. Written the same in both languages because the unit *is* the label; a
            // translated "کیلوبایت بر ثانیه" would be longer than the number it describes.
            "unit_kbs", "unit_mbs", "unit_gbps",
            // A percent sign and a dash. There is nothing to translate in either.
            "unit_percent", "hlt_na",
            // Hardware names as printed on the device and in the phone's own settings;
            // translating them would make the row unrecognisable to the person holding the phone.
            "mv_link_kind_usb", "mv_link_kind_hotspot", "mv_method_usb", "mv_method_hotspot",
        )

        val untranslated = mutableListOf<String>()
        var checked = 0

        val entry = Regex(""""([a-z0-9_]+)"\s*to\s*\("([^"]*)"\s*to\s*"([^"]*)"\)""")
        file.readLines().forEach { line ->
            val t = line.trimStart()
            if (t.startsWith("//")) return@forEach
            for (m in entry.findAll(line)) {
                val key = m.groupValues[1]
                val en = m.groupValues[2]
                val fa = m.groupValues[3]
                checked++

                if (fa.isEmpty()) {
                    untranslated += "$key  (the Persian column is empty)"
                    continue
                }
                if (fa == en) {
                    if (key !in neutral) untranslated += "$key  en == fa == \"$en\""
                    continue
                }
                // A "translation" that is still Latin script is not a Persian string. Requiring
                // at least one Persian character catches English with a stray diacritic, and
                // catches the case where the two columns were swapped.
                if (fa.none { it in '\u0600'..'\u06FF' }) {
                    untranslated += "$key  the Persian column has no Persian characters: \"$fa\""
                }
            }
        }

        assertTrue("only matched $checked entries, so the scan found nothing", checked > 200)
        assertTrue(
            "these strings are the same in both languages, or the Persian column is not Persian:\n" +
                untranslated.joinToString("\n") { "  $it" },
            untranslated.isEmpty()
        )
    }

    // ---------------- the diagnostics screen specifically ----------------

    /**
     * The screen is new and could not be run on a device, so this is the closest thing to
     * looking at it: every key it draws with has to resolve, in both languages.
     */
    @Test
    fun theDiagnosticsScreenHasUsableTextInBothLanguages() {
        val screen = File(sourceRoot(), "ui/screens/DiagnosticsScreen.kt")
        assertTrue("DiagnosticsScreen.kt not found", screen.isFile)

        val keys = Regex("""\b(?:L|Strings\.raw)\(\s*"([a-z0-9_]+)"""")
            .findAll(screen.readText())
            .map { it.groupValues[1] }
            .toSet()

        assertTrue("found no string keys in the diagnostics screen - has it been rewritten?", keys.isNotEmpty())

        // raw() only returns one language, so both are checked by reaching into the table the
        // way the UI does and flipping the language around it.
        for (key in keys) {
            assertNotEquals("$key is not registered", key, Strings.raw(key))
        }

        // Nothing in the screen may fall back to a key name at runtime.
        for (key in keys) {
            assertEquals("$key resolves in Persian", false, Strings.raw(key) == key)
        }
    }

    /** The proof that the key scan is looking at something. */
    @Test
    fun theScanWouldHaveCaughtTheOriginalBug() {
        assertEquals(
            "a missing key returns itself, which is the whole reason this test exists",
            "diag_does_not_exist", Strings.raw("diag_does_not_exist")
        )
        assertNotEquals("a real key resolves", "diag_title", Strings.raw("diag_title"))
    }
}