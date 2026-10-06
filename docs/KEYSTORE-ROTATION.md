# The release keystore is public. Treat it as compromised.

## The current state, stated plainly

**The key is downloadable by anyone, right now.** Not "may still be reachable" - reachable.

```
blob  b4a6cc4a5205b04ff6954b5a040652c7f8a2cf06   2748 bytes
      https://api.github.com/repos/Ekkh1300/NetPilot/git/blobs/b4a6cc4a5205b04ff6954b5a040652c7f8a2cf06
```

Verified during this session. GitHub answers HTTP 200 for that object id, which means the
keystore can be fetched without cloning anything, without a GitHub account, and without any
history rewrite having been performed.

The password was not a barrier either. `keystore.properties` shipped in the same commit and read:

```
storeFile=netpilot-release.jks
storePassword=netpilot
keyAlias=netpilot
keyPassword=netpilot
```

So the key and its password were both public. Anyone who did neither has only to guess a single
word.

## What was done, and what it did not achieve

The files were removed from the working tree, `.gitignore` was extended, and the commit was
rewritten so that no reachable branch in `master` references them.

**That last part is cosmetic.** `git filter-branch` and a force-push change which commits a
reference points at; they do not delete the object from GitHub's store, and GitHub does not
garbage-collect on any schedule a project can rely on. The blob is addressed by content hash, so
the object id is stable and permanent - it is not an index entry that can be invalidated.

Rewriting history is the right response to a leaked *secret* only when the secret's owner can
still choose a new value, such as a password or a token. A signing key is different: the value
cannot be changed unilaterally, because every device that installed a build signed by it has the
old public key inside it. The only thing that can replace it is the store's own key-reset
process.

So: the rewrite reduced confusion for future readers. It did not close the hole, and this
paragraph originally said the opposite - that the commit "was rewritten so neither file appears
anywhere in master's history", which reads as though the exposure was resolved. It was not.

## What this allows

Anyone holding this keystore can produce an APK that Android installs as though it came from us,
with our package name and our signature. Enough to serve a modified app to existing users, and
publishing a newer version does not withdraw it.

**Unless Play App Signing is enabled** - in which case the key stored on device is Google's, not
this one, and a build signed with it cannot be installed at all. That check comes first, because
it decides whether this is a serious incident or an untidy one.

## What to do

**Step 1 - find out whether Play App Signing is already on.**

Play Console -> Test and release -> Setup -> App integrity -> App signing.

- If the page shows a **Google-held signing certificate**, you are protected. The leaked key can
  only produce an upload, not an installable build. Carry on with step 3.
- If it shows **your own certificate**, the exposure is live. Continue to step 2.

**Step 2 - if your own key is the signing key, enable Play App Signing.**

Mandatory for new apps since August 2021, so a published app is very likely already on it. If it
is not, enabling it for an existing app requires Google to verify you hold the current key -
which you do, though it also means their support process is involved. They retain the old signing
key for exactly this case.

Until that is done, treat any release signed with the current key as publishing from a public
credential.

**Step 3 - move the upload key.**

Once Google holds the signing key, what is on disk is only an *upload* key, which cannot produce
an installable app. Rotate it freely. A replacement has already been generated:

```
netpilot-release-2026.jks    3072-bit RSA, PKCS12
fingerprint  5b20c69907d85469e03f550f07cfc994f6eb729856db4e3656ae1d4f1b5f7260
```

Its password is a 24-character random string, stored outside the repository. Replace the leaked
one with it, and put the password in CI secrets rather than in a file.

**Never commit the replacement.** `.gitignore` now covers `*.jks`, `*.keystore`, `*.p12` and
`*.pfx` as well as `keystore.properties`, and `keystore.properties.example` documents the shape.

## A note on the builds that were already published

Releases 1.2.0 through 1.2.3 are signed with the leaked key, because a differently-signed update
is rejected by the store and this one had to remain an upgrade of 1.2.0. Signing them with
anything else was not an option - the only fix for a leaked signing key is the store's reset
process, and until that completes, continuity of the update path depends on the old key.

This is a real trade and it was worth stating rather than quietly shipping a differently-signed
build that would have failed at upload with a message nobody reading the changelog would connect
to a credential leak.

## Why the old password was a finding in its own right

`storePassword=netpilot` is guessable from the repository URL alone. So even before publication
the keystore was not protected by anything secret - the "secret" was the project name. A key whose
passphrase is the project name is a key anyone can open, whether or not it was ever committed.

That is the part worth not repeating: a commit was the cause of the publication, but the password
was the reason the publication mattered.

