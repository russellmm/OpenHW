#!/usr/bin/env bash
# Exits OpenHW the way the owner does — tray icon → Exit — on a private Xvfb display and D-Bus session with a minimal
# tray host (watcher.py), and checks it exits cleanly (code 0, no unhandled exception).
#   tools/tray-exit-test/run.sh [openhw executable or .dll]   (default: /usr/bin/openhw)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
app="${1:-/usr/bin/openhw}"
if [[ "${2:-}" == --inner ]]; then
    python3 -I "$here/watcher.py" >"$OUT/watcher.log" 2>&1 & watcher=$!
    sleep 1
    if [[ "$app" == *.dll ]]; then dotnet "$app" --tray >"$OUT/app.log" 2>&1 & else "$app" --tray >"$OUT/app.log" 2>&1 & fi
    pid=$!
    python3 -I "$here/click_exit.py" >"$OUT/click.log" 2>&1
    for _ in $(seq 30); do kill -0 $pid 2>/dev/null || break; sleep 0.5; done
    if kill -0 $pid 2>/dev/null; then echo "still running" >"$OUT/exit"; kill $pid; else wait $pid; echo $? >"$OUT/exit"; fi
    kill $watcher 2>/dev/null
    exit 0
fi
out="$(mktemp -d)"; runtime="/run/user/$(id -u)/openhw-exit-test"; display=:79
mkdir -p "$out/config" "$runtime"; chmod 700 "$runtime"
Xvfb $display -screen 0 1600x1000x24 >/dev/null 2>&1 & xvfb=$!
sleep 1
env -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS DISPLAY=$display XDG_RUNTIME_DIR="$runtime" XDG_CONFIG_HOME="$out/config" \
    OUT="$out" timeout 90 dbus-run-session -- "$0" "$app" --inner >/dev/null 2>&1
kill $xvfb; rm -rf "$runtime"
cat "$out/click.log"
code="$(cat "$out/exit" 2>/dev/null || echo none)"
echo "exit code: $code"
if grep -q 'Unhandled exception' "$out/app.log"; then echo "FAIL: unhandled exception"; grep -A6 'Unhandled exception' "$out/app.log"; exit 1; fi
[[ "$code" == 0 ]] && echo PASS || { echo FAIL; tail -20 "$out/app.log"; exit 1; }
