#!/usr/bin/env bash
# Tests the tray-hover GNOME extension (packaging/gnome-extension) in a private headless GNOME Shell: its own D-Bus
# session, dconf, data and runtime dirs, so the desktop in use is never touched and no input is injected. fake_sni.py
# stands in for OpenHW (one item with OpenHW's id, one without); harness@test sets hover and checks the tooltip.
#   tools/tray-hover-test/run.sh            → PASS/FAIL lines; exit 1 on any FAIL. Screenshot: $out/hover.png
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

if [[ "${1:-}" == --inner ]]; then
    dconf write /org/gnome/shell/enabled-extensions \
        "['ubuntu-appindicators@ubuntu.com', 'openhw-tray-hover@russellmm.github.io', 'harness@test']"
    dconf write /org/gnome/shell/welcome-dialog-last-shown-version "'999'"
    gnome-shell --headless --virtual-monitor 1280x800 >"$HOVER_OUT/shell.log" 2>&1 &
    shell=$!
    sleep 2
    python3 -I "$here/fake_sni.py" >"$HOVER_OUT/sni.log" 2>&1 &
    items=$!
    for _ in $(seq 60); do
        grep -q 'HOVERTEST DONE' "$HOVER_OUT/shell.log" && break
        sleep 1
    done
    kill "$shell" "$items" 2>/dev/null || true
    wait 2>/dev/null || true
    exit 0
fi

work="$(mktemp -d)"
# Wayland socket paths max out at 108 bytes, so the runtime dir must be short (a long TMPDIR is too long).
runtime="/run/user/$(id -u)/openhw-hover-test"
trap 'rm -rf "$work" "$runtime"' EXIT
mkdir -p "$work"/{config,cache,out} "$work/data/gnome-shell/extensions" "$runtime"
chmod 700 "$runtime"
cp -r "$repo/packaging/gnome-extension/." "$here/harness@test" "$work/data/gnome-shell/extensions/"
out="${HOVER_OUT:-$repo/tools/tray-hover-test/out}"
mkdir -p "$out"

env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS \
    XDG_CONFIG_HOME="$work/config" XDG_DATA_HOME="$work/data" XDG_CACHE_HOME="$work/cache" XDG_RUNTIME_DIR="$runtime" \
    HOVER_OUT="$out" timeout 100 dbus-run-session -- "$here/run.sh" --inner >/dev/null 2>&1 || true

grep -o 'HOVERTEST .*' "$out/shell.log" || true
grep -E 'JS ERROR|openhw-tray-hover.*(rror|xception)' "$out/shell.log" && exit 1
grep -q 'HOVERTEST DONE' "$out/shell.log" || { echo "The test shell never finished (see $out/shell.log)."; exit 1; }
! grep -q 'HOVERTEST FAIL' "$out/shell.log"
