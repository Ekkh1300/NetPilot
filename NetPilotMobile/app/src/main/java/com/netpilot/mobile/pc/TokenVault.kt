package com.netpilot.mobile.pc

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.netpilot.mobile.core.log.NpLog
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Encrypts the PC pairing token at rest.
 *
 * The token is the whole security boundary between the phone and the desktop. It is a 256-bit
 * secret that authorises every `/api/v1` call on the PC - including changing the machine's
 * system-wide proxy - and it was being written to `np_pc.json` as a bare JSON string in the
 * app's private storage.
 *
 * "Private storage" is not a security control. It is app-scoped, which means:
 *
 * - a rooted phone, which is common enough that it is not exotic, reads the file directly;
 * - any component with the app's UID, including a library the app depends on, reads it freely;
 * - an `adb backup`, or a device-to-device transfer on an unencrypted filesystem, carries it;
 * - and it sits in a file that the app's own diagnostics and bug reports have been known to
 *   collect.
 *
 * The token outlives the threat that produced it, too: it is reissued on unpair, not on every
 * launch, so a copy taken today is still valid next month.
 *
 * So the token is sealed with a key held in the Android Keystore, which never leaves the
 * hardware's key store and is not readable even by the app's own process. What is written to disk
 * is ciphertext plus an IV, and the ciphertext is useless on its own and on any other device -
 * the key is device- and app-specific and, for Tink's purposes, non-exportable.
 *
 * ## Why not EncryptedSharedPreferences
 *
 * The AndroidX library would do this, and it would be less code. It also has to be a separate
 * store from the rest of the app's settings, which means migrating every existing install's
 * token across two storage systems and keeping them consistent - and a token that fails to
 * migrate silently un-pairs the phone, which looks like a bug to the user rather than like a
 * security decision.
 *
 * Encrypting one field in place keeps the document layout unchanged, so `Store` still reads and
 * writes the same file and nothing else has to know this exists.
 *
 * ## What this does and does not protect against
 *
 * It protects the token at rest. It does not protect it in memory while the app is running, and
 * it does not defend against a compromised process - an attacker with code execution inside the
 * app can call the Keystore. Those are different problems, and claiming otherwise would be
 * dishonest. This closes the gap that a copied file or a backup leaves open, which is the one
 * that has actually been reachable.
 */
object TokenVault {

    private const val KEY_ALIAS = "netpilot.pc.token"
    private const val TRANSFORMATION = "AES/GCM/NoPadding"
    private const val IV_BYTES = 12          // GCM's standard nonce length
    private const val TAG_BITS = 128

    /** Marks a value this class produced, so plaintext written by an older build is detectable. */
    private const val PREFIX = "v1:"

    /**
     * Seals [plaintext] for storage.
     *
     * Returns a string of the form `v1:<base64 iv>:<base64 ciphertext>`. If the key cannot be
     * created or the cipher cannot be initialised, the plaintext is returned unchanged rather
     * than an error being raised - see [writeFallbackIsAccepted] for why that is the lesser
     * evil, but it should not happen on any device this runs on.
     */
    fun seal(plaintext: String): String {
        if (plaintext.isEmpty()) return ""

        return try {
            val cipher = Cipher.getInstance(TRANSFORMATION).apply {
                init(Cipher.ENCRYPT_MODE, key())
            }
            val iv = cipher.iv
            val out = cipher.doFinal(plaintext.toByteArray(Charsets.UTF_8))
            PREFIX + b64(iv) + ":" + b64(out)
        } catch (t: Throwable) {
            NpLog.error(
                "pclink",
                "could not encrypt the PC token; it is being stored in the clear as a result. " +
                    "This should not happen on a normal device - see TokenVault for what it costs",
                t
            )
            plaintext
        }
    }

    /**
     * Opens a stored value.
     *
     * Returns null when the value is not something this class produced and cannot be read - a
     * ciphertext from a different device, a tampered file, or a key the user has since locked
     * out. The caller treats that as "not paired" rather than as a failure, because the only
     * safe response to a token nobody can decrypt is to pair again.
     */
    fun open(stored: String): String? {
        if (stored.isEmpty()) return null

        // A value written before this existed: still usable, so an upgrade does not un-pair
        // everyone. Re-sealing it on the next write moves it forward.
        if (!stored.startsWith(PREFIX)) return stored

        return try {
            val rest = stored.substring(PREFIX.length)
            val sep = rest.indexOf(':')
            if (sep <= 0) return null

            val iv = unb64(rest.substring(0, sep)) ?: return null
            val body = unb64(rest.substring(sep + 1)) ?: return null

            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, iv))
            String(cipher.doFinal(body), Charsets.UTF_8)
        } catch (t: Throwable) {
            // Almost always "wrong key" - the token was sealed on another device, or the key was
            // invalidated. Not an error worth a stack trace: the outcome is the same, which is
            // that the phone has to pair again.
            NpLog.warn("pclink", "the stored PC token could not be decrypted, so pairing has to be redone: ${t.javaClass.simpleName}")
            null
        }
    }

    /** True when [stored] was produced by [seal]. */
    fun isSealed(stored: String): Boolean = stored.startsWith(PREFIX)

    /**
     * The key, created on first use.
     *
     * `setUserAuthenticationRequired` is deliberately not set: the token has to be usable while
     * the phone is locked, because the tunnel and the PC link both keep running then and a
     * prompt on every read would be worse than the threat. The value is still not exportable,
     * which is the property that matters for a file someone can copy off the device.
     */
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                // Required on API 23+ and harmless above it.
                .setRandomizedEncryptionRequired(true)
                .build()
        )
        return gen.generateKey()
    }

    private fun b64(b: ByteArray): String =
        android.util.Base64.encodeToString(b, android.util.Base64.NO_WRAP)

    private fun unb64(s: String): ByteArray? = try {
        android.util.Base64.decode(s, android.util.Base64.NO_WRAP)
    } catch (_: Throwable) {
        null
    }
}