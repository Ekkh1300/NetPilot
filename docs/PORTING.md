# Porting the desktop app to macOS and Linux — research findings

Date: 2026-10-03 · Status: **research only, nothing built**

This document exists so the decision to spend effort on macOS/Linux is made with facts rather
than hope. The short version: the desktop app **cannot be built for macOS or Linux**; it would
have to be rewritten, and the part that would block us is testing, not coding.

---

## 1. The hard fact

`NetPilot.csproj` declares:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>
```

WPF and WinForms exist only on Windows. .NET does not ship a runtime pack for them on any
other platform, so there is no flag that makes this a cross-platform build. Attempting it:

```
$ dotnet publish NetPilot\NetPilot.csproj -r linux-x64 --self-contained true
error NETSDK1082: There was no runtime pack for Microsoft.WindowsDesktop.App
                  available for the specified RuntimeIdentifier 'linux-x64'.

$ dotnet publish ... -r osx-arm64     → same error
$ dotnet publish ... -r osx-x64       → same error
```

This is not a misconfiguration. Microsoft has never shipped WPF outside Windows, so a
port is a rewrite of the presentation and platform layers, not a retarget.

## 2. What we would be rewriting

| Layer | Size |
|---|---|
| C# | 8,724 lines across 45 files |
| XAML | 2,615 lines across 5 files (Pages.xaml alone is 1,838) |
| Services | 4,558 lines, 22 files |
| ViewModels | 2,445 lines, 7 files |
| PowerShell helper call sites | 37, spread over 10 files |

## 3. Feature-by-feature portability

| Feature | Current implementation | Linux | macOS | Verdict |
|---|---|---|---|---|
| Phone tunnel → PC proxy | `HttpListener` on :8788 | same | same | **portable as-is** |
| API bridge `/api/v1/*` | `HttpListener` on :8787 | same | same | **portable as-is** |
| Pairing / tokens / JSON | in-process | same | same | **portable as-is** |
| Link detection | `Get-NetAdapter` | `/sys/class/net`, `nmcli` | `ifconfig -b`, `scutil` | rewrite, small |
| Traffic counters | `Get-NetAdapterStatistics` | `/proc/net/dev` | `netstat -ib` | rewrite, small |
| Set system proxy for sharing | `netsh winhttp set proxy` | `gsettings`, `nmcli` | `networksetup -setwebproxy` | rewrite, small |
| PC VPN detection | `Get-VpnConnection` | `nmcli con` | `scutil --nc list` | rewrite, medium |
| Per-app firewall | `INetFwPolicy2` COM + `netsh` | `nftables` | `pf` | rewrite, **large + needs root** |
| Bandwidth limits | `netsh` QoS policy store | `tc` / `nftables` | `pf` | rewrite, **large + needs root** |
| Per-process connection counts | `iphlpapi.dll` `GetExtendedTcpTable` | `/proc/net/tcp` | `lsof` | rewrite, medium |
| Network snapshot / restore | `netsh winsock reset`, `netsh int ip reset` | no real equivalent | no real equivalent | **not meaningfully portable** |
| ETW trace collector | `EtwCollector.cs`, 363 lines | no equivalent | no equivalent | **not portable** |
| Tray icon | WinForms `NotifyIcon` | no equivalent in Avalonia | same | rewrite |
| Installer | custom `Setup.exe`, writes registry | `.desktop` file | `.pkg`/dmg | rewrite |

Two things are portable. Eleven are not. The two most central features — per-app firewall and
bandwidth limits — are the ones with no library-free path and the ones that need root.

### The UI is the other half of the bill

Avalonia is the realistic UI framework (it consumes WPF-style XAML), but five custom controls
derive from `FrameworkElement` and hand-draw themselves:

| Control | Purpose |
|---|---|
| `SpeedChart` | live throughput sparkline |
| `ColumnChart` | per-app bar chart |
| `HBarChart` | usage bars |
| `DonutGauge` | circular usage gauge |
| `OrbControl` | the decorative status orb |

Plus 6 `IValueConverter`/`IMultiValueConverter` classes. Each of the five would be reimplemented
against Avalonia's draw API, and each is a visual component whose correctness can only be
confirmed by looking at it — which requires running it.

Encouraging detail: the XAML uses almost no exotic WPF constructs (no `BitmapSource`,
`WriteableBitmap`, `RenderOptions`, or `TextOptions` anywhere), so the markup conversion itself
is more tractable than the custom-drawn controls suggest.

## 4. What the Android phone actually needs from the PC

The phone speaks exactly ten endpoints, and this is the contract any port must honour:

| Endpoint | Portable? |
|---|---|
| `GET /api/v1/ping` | yes |
| `POST /api/v1/pair` | yes |
| `DELETE /api/v1/pair` | yes |
| `GET /api/v1/status` | partly — reports proxy state and, on Windows only, the PC VPN state |
| `POST /api/v1/hello` | yes |
| `POST /api/v1/report` | yes |
| `POST /api/v1/share` | yes, in proxy mode |
| `POST /api/v1/stop` | yes |
| `POST /api/v1/backup` | no — Windows `netsh` snapshot |
| `POST /api/v1/restore` | no — Windows `netsh` snapshot |

Eight of ten are portable in principle. The Android app already talks to a URL and a port, so
it would not need modification for a portable PC-side listener.

## 5. Three shapes the work could take

**A — Headless companion (no UI).** A small `net8.0` binary that serves the API and the proxy,
detects links, and reports counters. Roughly: 1 new project, ~6 new files, ~900 lines. Ships as
a single self-contained binary, which is genuinely easier than a Windows installer — no
registry, no shortcut, no elevation prompt beyond the firewall one. Delivers the tunnel, and
nothing else. Users would see only the share feature.

**B — Full GUI port (Avalonia).** Every row in the two tables above. This is weeks of work,
and each rewritten platform feature would be untestable here.

**C — Nothing.** Keep the desktop app Windows-only and say so plainly in the README.

## 6. The constraint that decides it

**There is no macOS or Linux machine available to test on.** Consequences, stated plainly:

- Every unit test I write for a Linux/macOS code path would run **on Windows**, against a
  branch of code that only executes there. That is not a real test of Linux behaviour.
- Compilation for `linux-x64` and `osx-arm64` can be verified. Execution cannot.
- The features that matter most — firewall and bandwidth limits — are the ones whose failure
  mode is "the user's internet stopped working." Shipping those untested would be a bad trade
  for any amount of code.
- The visual components cannot be eyeballed at all.

Shipping an untested firewall manager to someone's Mac is worse than not shipping one: the
feature's failure mode is a machine with no network and no explanation.

## 7. Recommendation

**Option A, but framed honestly and labelled as a preview.** It is the only shape whose value
concentrates in code that is genuinely portable, it can be compiled for both platforms, and it
can be tested end-to-end on Windows with the shared logic (which is where the real bugs have
been so far). Its release notes and README must state plainly: compiled for Linux/macOS, logic
tested on Windows, **not yet run on a real Linux or macOS machine** — and list which features
are Windows-only.

Option B should not start until either a test machine exists or the user accepts an explicitly
untested feature set that includes firewall and bandwidth limits.

Whatever is chosen, the README should state the supported platforms instead of leaving users to
discover it.