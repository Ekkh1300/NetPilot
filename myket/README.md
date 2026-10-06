# NetPilot 1.2.3 - signed with the 1.2.0 key

Built so Google Play accepts it as an update to the release already on the store.

## Files

| File | What it is |
|---|---|
| `NetPilot-1.2.3.aab` | **Upload this one.** Google Play has required an app bundle for new apps and updates since August 2021. |
| `NetPilot-1.2.3.apk` | A plain APK, for sideloading or for a device that will not take a bundle. |

`versionCode` is 4. Play requires it to be **higher** than whatever is already published - 1.2.0 was versionCode 3, so 4 is the next value and is already set in the build.

## The signature

Both files carry this SHA-256:

```
3f4c03af3427a1b4238453f71a2789e86f731705aafe3ccf85f6c8f36d5ba971
```

That is the key `netpilot-release.jks`, the one 1.2.0 was signed with. Play rejects an update signed
by a different key, which is the error you hit - the build was using the key generated on 2026-10-06
instead. `keystore.properties` now points back at the original.

Verified rather than assumed:

```
NetPilot-1.2.3.apk  Signer #1 certificate SHA-256 digest:
                     3f4c03af3427a1b4238453f71a2789e86f731705aafe3ccf85f6c8f36d5ba971
NetPilot-1.2.3.aab  SHA256: 3F:4C:03:AF:34:27:A1:B4:23:84:53:F7:1A:27:89:E8:6F:73:17:05:AA:FE:3C:CF:85:F6:C8:F3:6D:5B:A9:71
```

## Please read this part

**The signing key for this app is compromised, and publishing 1.2.3 does not fix it.**

`keystore.properties` and `netpilot-release.jks` were both committed to the public repository
before this session started. Anyone who cloned it - and anyone who mirrored it - holds the key that
signs this update.

What that means in practice:

- **Before you publish**, enable **Play App Signing**. Play then holds the key that signs what users
  install, and what you hold is only an *upload* key that cannot produce an installable build on
  its own. That closes the hole for good and is the only real fix.
- Play App Signing has been mandatory for new apps since August 2021, so if 1.2.0 is on the store
  it is very likely already on. Check under **Test and release → Setup → App integrity**. If the
  page shows a Google-held signing certificate rather than your own, you are already protected and
  publishing is fine.
- If it is **not** on, enabling it now is a change to an existing app and Google will have to
  verify you own the current key. They keep the old signing key on file for exactly this, so
  follow their instructions rather than expecting it to be instant.

A newer key was generated on 2026-10-06 (`netpilot-release-2026.jks`, 3072-bit, a 24-character
random password rather than the old `netpilot`). It is **not** used for this build, because Play
would reject it. It is the key to adopt *after* Play App Signing is confirmed, as the upload key.

If Play App Signing cannot be enabled, the exposure is real and worth acting on: keeping the
release password off the repository from here, and treating any future key change as the
credential incident it would be.

## Permissions that will be reviewed

Two declarations draw a declaration form the first time an app ships them, and both are honest here:

- **`QUERY_ALL_PACKAGES`** - this is the one most likely to be challenged. It exists so per-app
  usage can be attributed, which does require knowing which apps exist. If Play refuses, remove it
  and the per-app usage screen degrades rather than the app failing.
- **`PACKAGE_USAGE_STATS`** - "special access", needing a justification screen. Usage data, for the
  usage screen. Usually accepted.
- **`FOREGROUND_SERVICE_SPECIAL_USE`** - Android 14+ requires the reason to be stated in Play
  Console. The tunnel and the paired-PC status report are the reason.

## Not verified

The Linux and macOS GUI binaries have never been rendered - no emulator or device was available to
build this. The Android APK is built, signed and its signature checked, but it has not been run on a
physical device from this machine either.