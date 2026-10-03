# Changelog

All notable changes to both apps are recorded here. The Android and Windows apps ship
together and share a version number.

## 1.2.2

### Fixed — Android
* **The tunnel fragmented every packet and crawled.** The tunnel interface was created with
  **MTU 4096**, which nothing on a real network carries: the phone wrote packets up to 4 KB
  into the tunnel, they were IP-fragmented on the first hop, and reassembly ate the
  throughput. From the user's side it read as "the PC sharing works, but the speed is bad".
  The MTU is now **1400** — what WireGuard and the mainstream Android tunnels use, because it
  survives Wi-Fi, cellular and a second encapsulation without fragmenting.

### Changed — Windows
* **A failed service start said nothing.** A bind that failed was swallowed: the page sat at
  "stopped" with no reason, which is indistinguishable from a dead "Stop Service" button. A
  bind blocked by the port still being held is now retried briefly (8 × 250 ms), and any
  remaining failure is shown instead of swallowed. 4 new tests drive the same
  `Start`/`Stop` calls the toggle button makes.

## 1.2.1

### Fixed — Windows
* **The app reported a PC VPN that was not connected.** An adapter counted as "VPN active"
  merely by being Up with an IPv4 address, which a stale or idle virtual adapter satisfies.
  Detection now requires a usable IPv4, and the third state — Up without one — is reported as
  **Unknown** (grey) rather than active, and does not block sharing. `Get-VpnConnection`'s
  own `Connected` status remains authoritative.

## 1.2.0

### Fixed — Android
* **The desktop never rode the phone's VPN.** The LAN proxy the PC dials lives inside the
  app's own package, which the tunnel excludes to avoid loops, so its upstream sockets
  left through the phone's normal interface. Every outbound socket now calls
  `VpnService.protect()` and is put back into the tunnel.
* **DNS lookups raced one dead resolver after another.** The selected resolver got 2.5 s,
  then each system fallback got 1.5 s in turn — so a host whose first resolver had gone
  quiet paid that on *every* lookup. All resolvers are now asked at once and the first real
  answer wins: worst case went from the sum of every timeout to the slowest resolver.
* **Idle connections died mid-session.** The proxy's upstream timeout was 20 s, shorter
  than any browser keep-alive or a websocket client, so idle HTTPS connections were torn
  down and websockets expired on a timer. Now 120 s, still bounded.

### Fixed — Windows
* **The bridge deadlocked on startup.** Starting it blocked the UI thread on a PowerShell
  child with `.GetResult()`. The listener came up and the firewall rule was created, but
  the accept loop never started, so every request from the phone hung until it timed out —
  reported by the app as "PC unreachable". Startup is now fire-and-forget.
* **Restoring the network claimed success it did not have.** The restore script printed
  `OK` regardless, so the UI said "restored" while the machine kept our interface metrics,
  and the next share captured those wrong numbers as the new baseline. It now reports how
  many adapters were really restored, and distinguishes complete, partial and failed.
* **The proxy probe rejected live proxies.** It handed the host name to `TcpClient`, which
  tries the resolver's addresses under one deadline: on a host whose IPv6 loopback answers
  nothing, the `::1` attempt consumed the whole budget and `127.0.0.1` — which was
  listening — was never tried. Each address now gets its own attempt.
* **The internet limiter stopped applying limits after a restart.** `Stop()` left the
  cancelled token source in place, so the next `Start()` skipped creating the worker and
  every rule queued afterwards was dropped while the UI still showed them as enabled.

### Changed
* The phone → PC feature is now called **Phone Tunnel → PC**, and the phone's tunnel status
  reads "Tunnel" rather than "VPN": it never connects to a VPN server, so calling it one
  overstated it.
* The in-app connection guide lists every supported path (shared Wi-Fi, phone hotspot, USB
  tethering, ADB) instead of Wi-Fi only.

### Tests
* New `NetPilot.Tests` project: 43 tests covering the bridge's HTTP contract, its startup
  and shutdown, the proxy probe, the restore/share safety rules, and integration tests
  that change real DNS servers and real Windows QoS policies and put them back.
* Android: 92 unit tests.
* `Tools\verify.ps1` runs everything and fails if the suite changed the machine.

## Earlier releases

Development history before 1.2.0 was not tracked in a changelog; see the commit log.