#!/usr/bin/env bash
# Do OpenHW's tray icons survive GNOME's AppIndicator extension being turned off and on again (what a screen lock and
# unlock does)? A private headless GNOME Shell and D-Bus session, OpenHW on a private Xvfb display with a copy of the
# owner's tray settings; the desktop in use is never touched.
# Before 0.17.1 every icon's bus connection died when the extension came back (a callback read the wrong argument).
#   tools/tray-relock-test/run.sh [openhw executable or .dll]   (default: /usr/bin/openhw) → PASS/FAIL; exit 1 on FAIL
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
app="${1:-/usr/bin/openhw}"
items() { gdbus call --session -d org.kde.StatusNotifierWatcher -o /StatusNotifierWatcher -m org.freedesktop.DBus.Properties.Get \
    org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems 2>&1 | grep -o "':1\.[0-9]*'" | tr -d "'" | tr '\n' ' '; }
alive() { busctl --user list --unique 2>/dev/null | awk -v p="$1" '$2==p' | wc -l; }
if [[ "${2:-}" == --inner ]]; then
    dconf write /org/gnome/shell/enabled-extensions "['ubuntu-appindicators@ubuntu.com']"
    dconf write /org/gnome/shell/welcome-dialog-last-shown-version "'999'"
    gnome-shell --headless --virtual-monitor 1280x800 >"$OUT/shell.log" 2>&1 & shell=$!
    sleep 4
    if [[ "$app" == *.dll ]]; then DISPLAY=$XDISPLAY dotnet "$app" --tray >"$OUT/app.log" 2>&1 & else DISPLAY=$XDISPLAY "$app" --tray >"$OUT/app.log" 2>&1 & fi
    pid=$!
    sleep 12
    echo "start:   items [$(items)] openhw connections $(alive $pid)"
    for round in 1 2 3; do
        dconf write /org/gnome/shell/enabled-extensions "@as []"; sleep 2
        echo "off $round:   items [$(items)] openhw connections $(alive $pid)"
        dconf write /org/gnome/shell/enabled-extensions "['ubuntu-appindicators@ubuntu.com']"; sleep 6
        echo "on $round:    items [$(items)] openhw connections $(alive $pid)"
    done
    kill $pid $shell 2>/dev/null; wait 2>/dev/null
    exit 0
fi
out="$(mktemp -d)"; runtime="/run/user/$(id -u)/openhw-relock-test"; display=:78
mkdir -p "$out/config/openhw" "$out/data" "$out/cache" "$runtime"; chmod 700 "$runtime"
cp "$HOME/.config/openhw/settings.json" "$out/config/openhw/" 2>/dev/null
Xvfb $display -screen 0 1600x1000x24 >/dev/null 2>&1 & xvfb=$!
sleep 1
env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS XDISPLAY=$display XDG_RUNTIME_DIR="$runtime" \
    XDG_CONFIG_HOME="$out/config" XDG_DATA_HOME="$out/data" XDG_CACHE_HOME="$out/cache" \
    OUT="$out" timeout 120 dbus-run-session -- "$0" "$app" --inner 2>&1 | grep -E "^(start|off|on)" | tee "$out/rounds"
kill $xvfb; rm -rf "$runtime"
echo "logs: $out"
# Every "on" round must list the same items as at the start, and OpenHW must keep its connections.
first="$(sed -n 's/^start: *items \[\(.*\)\] openhw.*/\1/p' "$out/rounds")"
[[ -n "$first" ]] || { echo "FAIL: no tray items at the start"; exit 1; }
grep '^on' "$out/rounds" | grep -vqF "items [$first]" && { echo "FAIL: items missing after the extension came back"; exit 1; }
grep -q 'lost its bus connection' "$out/app.log" && { echo "FAIL: a tray connection was lost"; grep -m1 -A2 'lost its bus' "$out/app.log"; exit 1; }
echo PASS
