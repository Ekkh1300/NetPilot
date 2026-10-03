# NetPilot on Linux and macOS

The desktop app is a WPF program, and WPF does not exist outside Windows — Microsoft has
never shipped a runtime pack for it ([`docs/PORTING.md`](PORTING.md) has the evidence). So
this is not "the same app, built for another platform". It is `netpilotd`, a separate program
that shares its logic with the Windows build through `NetPilot.Core`.

> ### Read this first: what has and has not been tested
>
> **These binaries have never been run on a real Linux or macOS machine.** No such machine was
> available when they were built.
>
> What *is* verified: all four RIDs compile, the shared logic has unit tests (121 of them,
> including the Linux and macOS interface names), and the Windows app still passes every test
> after the shared code was moved.
>
> What is **not** verified: that `nft` accepts our ruleset, that `tc` shapes anything, that
> `networksetup` restores what it captured, or that any of this does not take your network
> down. Treat it as a preview. Do not point it at a machine you cannot lose.

---

## What it can do, per platform

| | Linux | macOS | Windows |
|---|---|---|---|
| Link detection + traffic counters | yes | yes | yes (full app) |
| DNS control | yes (`resolvectl`) | yes (`networksetup`) | yes |
| System proxy (used by the phone tunnel) | yes (GNOME `gsettings`) | yes | yes |
| PC VPN state, 3-way | yes | yes | yes |
| Snapshot / restore | DNS + proxy | DNS + proxy | adapters + routes + DNS + VPN + proxy |
| **Per-target blocking** | **yes** — nftables, by uid | **no** — see below | yes |
| **Per-target upload limit** | **yes** — tc + flower, by uid | **no** — see below | yes |
| Per-target download limit | no | no | yes |
| Per-process traffic attribution | no | no | yes (ETW) |
| Graphical interface | no | no | yes |

### Why macOS cannot block or throttle one app

This is not an omission, it is the operating system:

* `pf` has **no process matcher**. OpenBSD's `pbox` can ask which process sent a packet;
  macOS's `pf` cannot. A rule matches addresses, ports and protocols — there is no pf syntax
  meaning "block this app".
* There is **no `tc` equivalent**. Per-flow shaping needs a kernel extension or a Network
  Extension, which requires a signed native app, a provisioning profile and Apple's approval.
* The built-in **Application Firewall is inbound-only**, keyed on code signature, and cannot be
  scripted per port or per direction.

So on macOS `Capability()` returns `None` and both entry points refuse with that reason. We
could have shipped a toggle that animates and does nothing. We did not: a user who believes an
app is blocked, when it is not, has no way to find out.

### Why Linux enforces by uid, not by path

The kernel's packet matchers can match the socket owner (`meta skuid` in nftables, `skuid` in
tc's flower filter) and can match almost nothing else useful. So "block Steam" means "block
what runs as this user" here, and the app says so rather than pretending to be finer-grained
than the kernel.

Per-process traffic attribution is unavailable on both: `/proc` publishes no per-process
network byte counter and conntrack is aggregate. Rather than report connection counts as
traffic, the backend returns an empty list — an empty list is visibly absent, invented numbers
are not.

---

## Running it

```bash
# Linux
./netpilotd                       # read-only: status only
sudo ./netpilotd                 # adds firewall rules and bandwidth limits

# macOS
./netpilotd
```

State goes to the platform's own location — `$XDG_STATE_HOME/netpilot` on Linux,
`~/Library/Application Support/NetPilot` on macOS.

On start it prints what it can do:

```
NetPilot daemon 1.2.2 on linux
  state       : /root/.local/state/netpilot/netpilot
  privileged  : no - firewall and limits are unavailable
  rules       : NOT AVAILABLE on this platform
  bridge      : http://+:8787/api/v1
  reachable   : yes, from the phone on this network
  hint        : re-run with sudo to enable blocking and bandwidth limits
```

Check it without a phone:

```bash
curl -s http://localhost:8787/api/v1/ping
curl -s http://localhost:8787/api/v1/status | jq
```

## Pairing with the Android app

The phone speaks the same ten endpoints as on Windows, so no Android change is needed. Open
**Phone Tunnel → PC** on the phone, point it at this machine's address and port 8787, and pair
with the code the daemon prints.

The phone cannot tell a daemon from the Windows app apart, which is the point of keeping the
endpoint paths in `NetPilot.Core`.

## Known gaps

* No graphical interface. Everything is the API; see `docs/PORTING.md` for what a GUI port
  would involve.
* Per-process traffic attribution: unavailable on both platforms (see above).
* Per-target download shaping: not implemented on either.
* Only the `gsettings` path for the Linux system proxy is wired up, so a KDE or XFCE session
  needs the environment variable instead.
* Hotspot detection on Linux needs the driver to expose it; where it does not, the phone link
  is classified as `lan`, which still works but reads differently in the list.