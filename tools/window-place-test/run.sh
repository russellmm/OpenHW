#!/usr/bin/env bash
# Where does OpenHW's main window open with nothing saved? (0.17.1 and earlier: wherever mutter put a window asking
# for the desktop's (0, 0) — the left, secondary monitor; 0.17.2: centred on the primary.) A private headless GNOME
# Shell (mutter + Xwayland, like the desktop) with two 1920×1080 monitors, the primary on the RIGHT (as the owner's
# DP-2 is; set by primary_right.py), and OpenHW with an empty private config. The desktop in use is never touched.
#   tools/window-place-test/run.sh [openhw executable or .dll]   (default: /usr/bin/openhw) → PASS/FAIL
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
app="${1:-/usr/bin/openhw}"
geom() { timeout 3 xwininfo -id "$1" 2>/dev/null | awk '/Absolute upper-left X/{x=$NF} /Absolute upper-left Y/{y=$NF} END{print x, y}'; }
win() { for _ in $(seq 40); do id=$(timeout 3 xdotool search --name "$1" 2>/dev/null | tail -1); [[ -n "$id" ]] && { echo "$id"; return; }; sleep 0.5; done; }
# Only the main window: keyboard input through XTEST, and opening a second window, freeze the headless Xwayland
# (seen 2026-10-26, with every build), so the other windows are checked on the desktop.
quit() { kill $pid 2>/dev/null; wait $pid 2>/dev/null; }
start_app() {
    if [[ "$app" == *.dll ]]; then dotnet "$app" >>"$OUT/app.log" 2>&1 & else "$app" >>"$OUT/app.log" 2>&1 & fi
    pid=$!
}
if [[ "${2:-}" == --inner ]]; then
    dconf write /org/gnome/shell/welcome-dialog-last-shown-version "'999'"
    gnome-shell --headless --virtual-monitor 1920x1080 --virtual-monitor 1920x1080 >"$OUT/shell.log" 2>&1 & shell=$!
    sleep 5
    export DISPLAY="$(grep -o 'Using public X11 display :[0-9]*' "$OUT/shell.log" | tail -1 | awk '{print $NF}')"
    [[ -n "$DISPLAY" ]] || { echo "no Xwayland display"; kill $shell; exit 0; }
    export XAUTHORITY="$(ls "$XDG_RUNTIME_DIR"/.mutter-Xwaylandauth.* 2>/dev/null | head -1)"
    xrandr --listmonitors >/dev/null # Xwayland up first: mutter tells it the primary only on a change after that
    python3 -I "$here/primary_right.py" >>"$OUT/shell.log" 2>&1; sleep 3
    echo "monitors: $(xrandr --listmonitors | tail -n +2 | awk '{print $2, $3}' | tr '\n' ';')"
    start_app
    main=$(win "OpenHW Sensors"); sleep 4
    echo "main: $(geom "$main")"
    quit; kill $shell 2>/dev/null; wait 2>/dev/null
    exit 0
fi
out="$(mktemp -d)"; runtime="/run/user/$(id -u)/openhw-place-test"
mkdir -p "$out"/{config,data,cache} "$runtime"; chmod 700 "$runtime"
env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR="$runtime" \
    XDG_CONFIG_HOME="$out/config" XDG_DATA_HOME="$out/data" XDG_CACHE_HOME="$out/cache" \
    OUT="$out" timeout 150 dbus-run-session -- "$0" "$app" --inner 2>&1 | grep -E "^(monitors|main|no )" | tee "$out/result"
rm -rf "$runtime"
echo "logs: $out"
x="$(sed -n 's/^main: \([0-9]*\) .*/\1/p' "$out/result")"
[[ -n "$x" && "$x" -ge 1920 && "$x" -lt 3840 ]] && echo "PASS: on the primary monitor" || { echo "FAIL: main window at x=${x:-?}, not on the primary (1920–3839)"; exit 1; }
