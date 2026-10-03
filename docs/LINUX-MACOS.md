# NetPilot on Linux and macOS

The desktop app is a WPF program, and WPF does not exist outside Windows — Microsoft has never
shipped a runtime pack for it (`docs/PORTING.md` has the evidence). So this is not "the same
app, built for another platform". It is `netpilotd`, a separate program that shares its logic
with the Windows build through `NetPilot.Core`.

> ### Verified on real machines
>
> Unlike the first build of this daemon, the released binaries are the ones that passed a
> continuous test suite running on a **real Linux kernel** (ubuntu-24.04, full root, real
> nftables and `tc`) and a **real Mac** (macos-14 arm64, real `networksetup`). Every commit to
> `master` runs that suite: `.github/workflows/daemon.yml`.
>
> What is *not* verified is anything needing real hardware and a second machine: a USB-tethered
> phone, a Wi-Fi hotspot, and bandwidth shaping under real load.

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

* `pf` has **no process matcher**. OpenBSD's `pbox` can ask which process sent a packet; macOS's
  `pf` cannot. A rule matches addresses, ports and protocols — there is no pf syntax meaning
  "block this app".
* There is **no `tc` equivalent**. Per-flow shaping needs a kernel extension or a Network
  Extension, which requires a signed native app, a provisioning profile and Apple's approval.
* The built-in **Application Firewall is inbound-only**, keyed on code signature, and cannot be
  scripted per port or per direction.

So `Capability()` returns `None` and both entry points refuse with that reason. We could have
shipped a toggle that animates and does nothing. We did not: a user who believes an app is
blocked, when it is not, has no way to find out.

### Why Linux enforces by uid, not by path

The kernel's packet matchers can match the socket owner (`meta skuid` in nftables, `skuid` in
tc's flower filter) and can match almost nothing else useful. So "block Steam" means "block what
runs as this user" here, and the app says so rather than pretending to be finer-grained than the
kernel.

Per-process traffic attribution is unavailable on both: `/proc` publishes no per-process network
byte counter and conntrack is aggregate. Rather than report connection counts as traffic, the
backend returns an empty list — an empty list is visibly absent, invented numbers are not.

---

## Running it

```bash
# Linux
./netpilotd                       # read-only: status only
sudo ./netpilotd                 # adds firewall rules and bandwidth limits

# macOS
./netpilotd
```

nft and tc need root. Without it the daemon starts, says so, and serves status — it does not
pretend to enforce anything.

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

## How this is tested

`.github/workflows/daemon.yml` runs on every push and covers:

* the shared logic on Linux, macOS **and** Windows — the classifier and the shaper have to agree
  everywhere, so they are tested everywhere;
* Linux, as root: that the generated nftables ruleset is accepted and appears in the kernel;
* Linux, as root: that **blocking a uid really cuts it off** — a listener on the machine, the
  test uid reached through `setpriv`, checked while blocking and again after unblocking, and with
  a check that the host itself is unaffected;
* Linux, as root: that `tc` installs the qdisc, and that the daemon verifies it is there rather
  than trusting an exit code;
* Linux: that the runner's firewall is left clean afterwards;
* macOS: correct interface names and counters, the VPN state, and that per-app rules refuse with
  the real reason where `pf` exists.

Every defect found this way was invisible to the compiler — a JSON serializer that dropped every
field, a `netstat` parser that reported MTU instead of the interface name, a limiter that
reported success while `tc -batch` installed nothing, and nft's atomic script semantics rejecting
the whole ruleset because of a `flush` on a table that did not exist yet.

## Known gaps

* No graphical interface. Everything is the API; see `docs/PORTING.md` for what a GUI port
  would involve.
* Per-process traffic attribution: unavailable on both platforms (see above).
* Per-target download shaping: not implemented on either.
* Only the `gsettings` path for the Linux system proxy is wired up, so a KDE or XFCE session
  needs the environment variable instead.
* Hotspot detection on Linux needs the driver to expose it; where it does not, the phone link is
  classified as `lan`, which still works but reads differently in the list.