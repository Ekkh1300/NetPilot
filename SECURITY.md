# Security Policy

## Supported versions

| Version | Supported |
|---------|-----------|
| 1.2.x   | yes       |
| < 1.2   | no        |

## Reporting a vulnerability

Please report security issues privately rather than opening a public issue:
use **Security → Report a vulnerability** on the repository page, or email the
maintainer. Include what you did, what you expected, and what happened.

You can expect an acknowledgement within a few days and a fix or an explanation
for anything reproducible.

## Please do not

* Do not publish the signing keystore, `keystore.properties`, or any password. If one has
  been committed by mistake, rotate the key before anything else — an exposed key means
  anyone can ship an update that phones install as if it were ours.
* Do not run the test suite with a real share in progress. `Tools\verify.ps1` records the
  machine's proxy, DNS and routing state before and after, and refuses to pass if the
  suite changed anything — but a test that drives real network settings is not something
  to point at a machine you care about.

## How the app handles credentials

* The desktop bridge token is regenerated on every app start and is only handed out in
  exchange for the six-digit pairing code shown in the UI.
* Five failed pairing attempts per minute, then the code has to be reissued.
* Unauthenticated requests are rejected before any handler runs, so an attacker cannot
  make the machine do work by hammering the API.
* Before anything on the PC is changed, the current state is snapshotted and can be
  restored.

## Scope

The bridge listens on the local network by design. Anyone who can reach port 8787 on the
PC can attempt pairing, which is why the rate limit and the short-lived token exist. Keep
the machine on a trusted network, and let the app's own firewall rule do its job.