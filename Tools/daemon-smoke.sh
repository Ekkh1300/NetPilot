#!/usr/bin/env bash
# Exercises the daemon's privileged paths on the machine it is running on.
#
# Compiling proves the code is syntactically valid. It does not prove the kernel accepts the
# ruleset, that a drop rule actually drops, or that removing it actually restores traffic -
# and those are the things whose failure mode is "the user's internet is gone and nothing says
# why". So they are checked here, for real, rather than claimed.
#
# Usage:  Tools/daemon-smoke.sh --nft | --tc | --block | --unblock | --macos-capability | --macos-dns
set -uo pipefail

MODE="${1:-}"
BASE="daemon-smoke"
API="http://localhost:8787/api/v1"
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/netpilot/netpilot"
TEST_UID=65534

say()  { printf '%s\n' "$1"; }
result() { printf '%s=%s\n' "$1" "$2"; }

# nft and tc need CAP_NET_ADMIN - and so does *reading* an existing ruleset: an ordinary user
# cannot even run "nft list". A GitHub runner's user is not root, which is why the first
# attempt died with "cache initialization failed: Operation not permitted".
#
# So the privileged checks run the daemon as root. Where sudo is genuinely unavailable they
# report "unknown" rather than a pass, because "we could not test this" and "this works"
# must never print the same word.
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
  if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    SUDO="sudo"
  else
    say "no root available: the privileged paths cannot be tested here"
  fi
fi
root() { if [ -n "$SUDO" ]; then sudo "$@"; else "$@"; fi; }

start_daemon() {
  if [ -n "$SUDO" ]; then
    sudo -n "$PWD/$DAEMON" > "$BASE.log" 2>&1 &
  else
    "$PWD/$DAEMON" > "$BASE.log" 2>&1 &
  fi
  DAEMON_PID=$!
  wait_for_api
}

DAEMON="${DAEMON:-./out/netpilotd}"
DAEMON_PID=""

wait_for_api() {
  for _ in $(seq 1 40); do
    curl -sf "$API/ping" >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  return 1
}

stop_daemon() {
  [ -n "$DAEMON_PID" ] && kill "$DAEMON_PID" 2>/dev/null
  wait "$DAEMON_PID" 2>/dev/null
  DAEMON_PID=""
}

post() { curl -sf -X POST "$API/$1" -H 'Content-Type: application/json' -d "$2"; }

case "$MODE" in

--nft)
  rm -rf "$STATE"
  if ! start_daemon; then say "daemon did not start"; cat "$BASE.log"; stop_daemon; exit 1; fi
  post block "{\"uid\":$TEST_UID,\"blocked\":true}" > "$BASE-block.json" || true
  sleep 1
  stop_daemon

  echo "--- the daemon's own reply ---"; cat "$BASE-block.json"; echo

  if root nft list table inet netpilot >/dev/null 2>&1; then
    say "--- the ruleset the kernel actually has ---"
    root nft list table inet netpilot
    result NFT_RULES_ACCEPTED yes
  else
    say "nft could not report our table (this is what the kernel says):"
    root nft list table inet netpilot 2>&1 || true
    if [ -n "$SUDO" ]; then result NFT_RULES_ACCEPTED no; else result NFT_RULES_ACCEPTED unknown; fi
  fi
  ;;

--tc)
  rm -rf "$STATE"
  DEV=$(ip -4 route show default 2>/dev/null | tr ' ' '\n' | grep -A1 '^dev$' | tail -1)
  say "uplink: ${DEV:-<none>}"

  if ! start_daemon; then say "daemon did not start"; cat "$BASE.log"; stop_daemon; exit 1; fi
  post limit "{\"uid\":$TEST_UID,\"upBps\":131072}" > "$BASE-limit.json" || true
  sleep 1
  stop_daemon

  echo "--- the daemon's own reply ---"; cat "$BASE-limit.json"; echo

  if [ -z "${DEV:-}" ]; then
    say "no default route on this machine, so there is nothing to shape"
    result TC_COMMANDS_ACCEPTED unknown
  elif root tc qdisc show dev "$DEV" 2>/dev/null | grep -q htb; then
    say "--- qdisc on $DEV ---"; root tc qdisc show dev "$DEV"
    say "--- classes ---";          root tc class show dev "$DEV"
    result TC_COMMANDS_ACCEPTED yes
  else
    say "tc did not install the htb qdisc on $DEV"
    root tc qdisc show dev "$DEV" 2>&1 || true
    if [ -n "$SUDO" ]; then result TC_COMMANDS_ACCEPTED no; else result TC_COMMANDS_ACCEPTED unknown; fi
  fi
  # Leave the interface as we found it, whatever happened above.
  [ -n "${DEV:-}" ] && root tc qdisc del dev "$DEV" root 2>/dev/null
  ;;

--block)
  rm -rf "$STATE"

  # Measured against a listener on this machine, not against the internet.
  #
  # A CI runner may have no outbound route at all, and then "the blocked uid cannot reach
  # anything" is indistinguishable from "the block works" - which is why this reports
  # unknown rather than passing. Loopback traffic goes through the same nft output chain as
  # everything else, so a local target makes the test deterministic and offline.
  python3 -m http.server 18099 --bind 127.0.0.1 >/dev/null 2>&1 &
  LISTENER=$!
  sleep 1

  if ! curl -sf --max-time 5 -o /dev/null "http://127.0.0.1:18099/"; then
    say "could not start the local listener; the effectiveness test cannot run"
    kill $LISTENER 2>/dev/null
    result BLOCK_IS_EFFECTIVE unknown
    exit 0
  fi
  say "baseline: the host reaches the local listener"
  # The stderr is kept: a setpriv failure and a curl failure look identical from the exit
  # code alone, and guessing between them wastes a cycle.
  if timeout 15 root setpriv --reuid=$TEST_UID --regid=$TEST_UID --clear-groups \
       curl -sf --max-time 5 -o /dev/null "http://127.0.0.1:18099/" 2>"$BASE-setpriv.err"; then
    say "baseline: the test uid reaches it too"
  else
    say "the test uid cannot reach the listener even before blocking; cannot measure"
    say "setpriv/curl said: $(head -3 "$BASE-setpriv.err" 2>/dev/null | tr '\n' ' ')"
    say "the uid exists as: $(getent passwd $TEST_UID 2>/dev/null || echo '<no such uid>')"
    kill $LISTENER 2>/dev/null
    result BLOCK_IS_EFFECTIVE unknown
    exit 0
  fi

  if ! start_daemon; then say "daemon did not start"; cat "$BASE.log"; stop_daemon; kill $LISTENER 2>/dev/null; exit 1; fi
  post block "{\"uid\":$TEST_UID,\"blocked\":true}" >/dev/null || true
  sleep 1
  stop_daemon

  if root nft list chain inet netpilot guard 2>/dev/null | grep -q "skuid $TEST_UID"; then
    result BLOCK_RULE_PRESENT yes
  else
    result BLOCK_RULE_PRESENT no
  fi

  # The machine itself must still work, so the drop is specific to the blocked uid and the
  # rule has not taken the host's own traffic down with it.
  if curl -sf --max-time 5 -o /dev/null "http://127.0.0.1:18099/"; then
    say "the host still reaches the listener, so the rule is uid-specific"
  else
    say "the host lost its own connectivity - the rule is too broad"
    result BLOCK_IS_EFFECTIVE no
    kill $LISTENER 2>/dev/null
    exit 0
  fi

  if timeout 15 root setpriv --reuid=$TEST_UID --regid=$TEST_UID --clear-groups \
       curl -sf --max-time 5 -o /dev/null "http://127.0.0.1:18099/"; then
    result BLOCK_IS_EFFECTIVE no
    say "the blocked uid still reached the listener"
  else
    result BLOCK_IS_EFFECTIVE yes
    say "the blocked uid is cut off while the host itself still works"
  fi
  kill $LISTENER 2>/dev/null
  ;;

--unblock)
  rm -rf "$STATE"
  python3 -m http.server 18098 --bind 127.0.0.1 >/dev/null 2>&1 &
  LISTENER=$!
  sleep 1

  if ! start_daemon; then say "daemon did not start"; cat "$BASE.log"; stop_daemon; kill $LISTENER 2>/dev/null; exit 1; fi
  post block "{\"uid\":$TEST_UID,\"blocked\":true}" >/dev/null || true
  sleep 1
  post block "{\"uid\":$TEST_UID,\"blocked\":false}" >/dev/null || true
  sleep 1
  stop_daemon

  if root nft list chain inet netpilot guard 2>/dev/null | grep -q "skuid $TEST_UID"; then
    say "the rule is still present after unblock"
    result BLOCK_IS_EFFECTIVE yes
  else
    result BLOCK_IS_EFFECTIVE no
  fi
  if timeout 15 root setpriv --reuid=$TEST_UID --regid=$TEST_UID --clear-groups \
       curl -sf --max-time 5 -o /dev/null "http://127.0.0.1:18098/"; then
    say "the unblocked uid reaches the listener again - traffic was restored"
  else
    say "still unreachable after unblock; removing the rule did not restore traffic"
  fi
  kill $LISTENER 2>/dev/null
  root nft flush table inet netpilot 2>/dev/null
  ;;

--macos-capability)
  if ! start_daemon; then say "daemon did not start"; cat "$BASE.log"; stop_daemon; exit 1; fi
  post capability '{"uid":501}' > "$BASE-cap.json" || true
  stop_daemon

  echo "--- the daemon's own reply ---"; cat "$BASE-cap.json"; echo

  if grep -q '"support":"None"' "$BASE-cap.json"; then
    result MACOS_CAPABILITY none
  else
    result MACOS_CAPABILITY "$(grep -o '"support":"[^"]*"' "$BASE-cap.json" | head -1 | cut -d'"' -f4)"
  fi

  # A refusal that loses its reason is no better than no refusal. Checked with grep on the
  # raw JSON: a heredoc inside a case statement proved fragile, and this is the whole point
  # of the check, so it should not depend on cleverness.
  if grep -q 'Network Extension' "$BASE-cap.json" && grep -q 'daemon_unsupported' "$BASE-cap.json"; then
    result MACOS_REFUSES_WITH_REASON yes
    say "refused with the real reason (pf has no process matcher)"
  else
    say "the refusal did not explain why:"
    cat "$BASE-cap.json"
    result MACOS_REFUSES_WITH_REASON no
  fi
  ;;

--macos-dns)
  if ! networksetup -listallnetworkservices >/dev/null 2>&1; then
    say "networksetup is unavailable on this Mac"
    result MACOS_DNS_READABLE no
    exit 1
  fi
  # Read every service the way the backend does. On CI the first service is a disabled
  # placeholder, so this proves the command runs, not that a live service was changed.
  networksetup -listallnetworkservices | tail -n +2 | while read -r svc; do
    case "$svc" in ""|"*"*) continue ;; esac
    say "  service: $svc"
    networksetup -getdnsservers "$svc" 2>&1 | sed 's/^/    /'
  done
  result MACOS_DNS_READABLE yes
  ;;

*)
  say "usage: $0 --nft | --tc | --block | --unblock | --macos-capability | --macos-dns"
  exit 2
  ;;
esac

# Always leave no trace: these tests edit the firewall of the machine they run on.
if command -v nft >/dev/null 2>&1; then
  root nft flush table inet netpilot 2>/dev/null
fi
exit 0