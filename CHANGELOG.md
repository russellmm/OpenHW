# Changelog

Versioning: semantic, pre-1.0 — **minor** for a round of new features, **patch** for fixes only. The version is set
once, in `Directory.Build.props`; each release is tagged `vX.Y.Z`. The app shows `X.Y.Z · <git revision>`.

## Unreleased — Windows 11 port (2026-11-17)

Linux behaviour unchanged; details in [docs/windows-port.md](docs/windows-port.md).

- Fan Control no longer freezes while the service is slow: its status poll runs in the background (Windows; Linux unchanged).
- GPU power limits on Windows confirmed live (2026-11-18).
- Windows: BIOS Setup shows this machine's values (UEFI variables read by the service, read-only).
- Windows: the WireView Pro II group is laid out as in HWiNFO (per-pin rows under their connector row).
- Windows: the board VR (XDPE19283B) reads the memory rail on PAGE 1, switching PAGE and restoring it per read; Settings → Safety → "VR Page Select (PMBus write)" turns that write off.
- Windows: Settings switches OpenHW doesn't act on (Safety, SMBus / I2C) are grayed.
- OpenHW builds, tests and runs on Windows 11: System (PDH), NVIDIA (NVML), NVMe SMART/temperatures/activity
  (unelevated IOCTLs, drive ids as on Linux) and network readers; Win32 tray icons with tooltips and menus, alerts as
  toasts, single instance, start at login.
- **OpenHW Helper** Windows service (named pipe): the BMC over Windows' IPMI driver (sensors, states, event log, fan
  zones, thresholds), Redfish over the BMC's LAN address bound to the wired port, NVIDIA fans.
- **Power → Power Limits on Windows**: the CPU socket limit through AMD's own HSMP driver and each NVIDIA card's limit,
  live and kept (applied when the service starts).
- `packaging\windows\build.ps1` (self-contained win-x64, mirrored to `C:\StandAlone\OpenHW`) and `install.ps1`.
- **Windows sensors**: CPU Tctl and CCD temperatures (PawnIO, k10temp's registers), the CPU Enhanced group (HSMP),
  WireView Pro II (COM port) and the UPS (USB HID), each in the same rows as on Linux.
- **Windows: effective clocks, ASUS EC temperatures, hardware error counts**: per-core and average effective clocks
  (APERF), the WRX90E's VRM1/VRM2/USB thermistor from its embedded controller, and WHEA corrected/fatal counts.
- **Windows: CPU package and cores power (RAPL)** through PawnIO's signed AMDFamily17, Linux's rows.
- **Windows: Fan Control no longer freezes** when the BMC answers on another wired port: every port on its subnet is
  tried (never Tailscale), and a failed fan-source read is kept a minute instead of retried every 2 s.
- **Windows: the board VR** (Infineon XDPE19283B on the WRX90E-SAGE SE) through PawnIO's SMBus, the same rows as
  HWiNFO's; Hardware Check shows the BIOS version and memory size and Windows-specific notes.
- **Settings → Driver Management on Windows**: start and stop the OpenHW Helper service, no admin prompt.
- **BMC sensors no longer drop out while Fan Control is open (Windows)**: fan sources are read over HTTPS once per
  service run instead of every 2 s, one IPMI request at a time in the service, and a failed read keeps the BMC group
  for 30 s while the app retries.
- **Windows tray icons stay where you drag them**: each icon's id comes from its sensor, and changing one icon no
  longer removes and re-adds the others. Move left/right are hidden on Windows (drag instead).
- **Larger tray digits on Windows**: they fill the 24 px tile (narrowed rather than shrunk), unit smaller and dropped
  after three characters, no colour fringes. Whole numbers only, as before.
- **One fan configuration for Linux and Windows** (the BMC keeps whatever the last OS wrote): `fan-curves.json` and
  `fan-profiles/` move to `C:\StandAlone\OpenHW\configs` = `/mnt/DriveC/StandAlone/OpenHW/configs` (per-user folder
  when it isn't there; `OPENHW_SHARED_CONFIG` overrides). The file in use is saved relative to it; the first start
  copies the OS's own files in unless the folder already has a live file. **This changes where Linux keeps them.**

## 0.39.3 — 2026-11-17

- Large tray tiles: bigger text — digits 19 px (was 15), unit 14 px (was 11) on the same 22 px tile (owner: "room to
  grow the size of the text").
- Tray tiles show **whole numbers** (rounded): "9.12W" → "9W", "1.25" → "1"; up to 9,999 in full, then "13k".

## 0.39.2 — 2026-11-17

- **Large tray tiles actually show**: with a tile file the item now sends no pixmaps. GNOME's AppIndicators draws a
  wide icon file itself and then still falls back to the pixmap, which replaced it — so in 0.39.0/0.39.1 the switch
  changed nothing on the panel (owner's screenshots, switch on and off).

## 0.39.1 — 2026-11-17

- Large tray tiles: each icon's tile files are keyed by the icon itself (not its position while the list is still
  being built at start), and files of removed icons are deleted — 0.39.0 left a few dozen stale SVGs at start-up.

## 0.39.0 — 2026-11-17

- **Large tray tiles** (Tray Icons → "Large tiles", on by default): each tray value is drawn at panel text size —
  bold Ubuntu Sans, like an indicator's text label ("225 W") — on a wider rounded tile in its colour, instead of
  being squeezed into a 22 px square. How: GNOME's AppIndicators extension shows an icon *file* in a user-writable
  folder at its own size when it's at least 1.5× as wide as tall; OpenHW writes each tile as an SVG under
  `$XDG_RUNTIME_DIR/openhw/tray-<pid>/` (sharp at 2× scale), sets the item's IconName to it, and writes a new file only
  when the value, unit or colour changes. The square pixmaps stay for other tray hosts and when the switch is off.

## 0.38.2 — 2026-11-17

- UPS rows show whole numbers ("248 W", "26 V", "32 min" instead of "248.000 W"). A sensor can now carry its own
  default decimal places (`HardwareItem.Decimals`); Customize values still overrides it per row.

## 0.38.1 — 2026-11-17

- The WireView group's title no longer doubles the brand ("Thermal Grizzly Thermal Grizzly WireView Pro II"): the
  device's build info already names itself "Thermal Grizzly WireView Pro II".

## 0.38.0 — 2026-11-17

- **Thermal Grizzly WireView Pro II** (the 12V-2x6 power meter between the PSU cable and a GPU, USB 0483:5740): its
  own sensor group, keyed by the device's USB serial — not by the GPU, so moving it to another card changes nothing.
  Each of the six 12 V wires' voltage, current and power; total power and current, average voltage, **current
  imbalance** (busiest wire vs the average, blank near idle); connector temperatures (in/out, plus external probes
  when plugged in); the PSU's sense-pin power limit; fault counts now and logged; the WireView's own supply and fan.
  Read through openhw-helper (**protocol 19**, `wireview`; the serial port is root-only) with read commands only
  (vendor data, build info, sensor values — never config, calibration, flash, fault clearing, reset or bootloader).
  The link has no checksum: out-of-step frames are recognised and dropped. Helper unit: `DeviceAllow=char-ttyACM rw`.
  Protocol facts from the community wireview-linux project; the code is OpenHW's own (`tools/wireview-probe/probe.py`
  is the standalone read-only check).
- **UPS** through Network UPS Tools (upsd on 127.0.0.1:3493, read-only `LIST` commands, no root): a group per UPS with
  input/output voltage (and frequency where reported), load, output power, apparent power, power rating, battery
  charge / voltage / runtime, and On Battery / Low Battery / Replace Battery / Overloaded. Shown for the CyberPower
  CP2000PFCRM2U.
- Every new row has a plain-language tooltip.

## 0.37.0 — 2026-11-17

- **File → BIOS Setup…: the BIOS emulator, inside OpenHW, with this machine's settings.** The standalone Python/Qt
  emulator (`/mnt/SOLIDIGM/Claude/BIOS`) ported to C#/Avalonia: the ASUS UEFI BIOS Utility (Advanced Mode) screen for
  the Pro WS WRX90E-SAGE SE — all 371 pages and ~7,600 items with every choice, factory default, help text and
  show/hide rule decoded from the BIOS 1405 capsule; tabs, breadcrumbs, details panel (plain-English explanation,
  meaning of each choice, exact allowed values with checked input, glossary, why an item is hidden or locked),
  search (F9), favorites (F3/F4), hidden items (Ctrl+H), Load Optimized Defaults (F5), ASUS User Profiles, Last
  Modified, "Changed from defaults" with text export, Q-Fan (F6), hot keys. The engine matches the Python one item for
  item (5,905 reachable items: same state, value and path).
- **Real values**: read from the machine's UEFI variables (`/sys/firmware/efi/efivars`, no root needed) — Setup,
  Ai Tweaker, AMD CBS / Overclocking / PBS, Server Mgmt and the hardware-detection variables the emulator used to
  guess (34 variables on the WRX90E); the rest fall back to factory defaults. The platform profile (CPU, cores, CCDs,
  base clock, boot entries, X710 MACs) is probed; the Hardware Monitor panel shows OpenHW's live BMC readings.
- **Read-only, with a plan**: changes on screen are a plan; F10 saves it (`~/.config/openhw/bios/plan.json`, history
  in `history.jsonl`) to enter in BIOS Setup; a planned value the machine has taken drops off by itself. Writing the
  firmware is designed for (behind `IFirmwareVariables`) but not offered.
- The Redfish list stays as **File → BIOS Settings from the BMC…**. The capsule decoder moved to `tools/bios-data/`
  (reproduces `bios-1405.json` byte for byte).

## 0.36.1 — 2026-11-17

- Hardware Check: the open BMC Redfish access is a plain note ("BMC access"), not a red warning — the owner keeps it
  open on purpose — so it no longer opens the check by itself. How to lock it down stays in the note.

## 0.36.0 — 2026-11-17

- **File → BIOS Settings…** (read-only): every BIOS Setup option the BIOS publishes to the BMC when Setup → "Publish
  HII Resources" is on — 1,295 on the WRX90E (BIOS 1405, registry BiosAttributeRegistryA5496.14.5.0) — with label,
  current value, default, internal name and help text. Search (all words, any field) and "Only changed from default".
  Read over Redfish through the helper (new `biossettings`); passwords are never included. Setup's checkboxes, which
  AMI publishes as true/false values with "None"/"Disabled" choices, show as Enabled/Disabled.
- **Hardware Check: BIOS settings changed since the saved snapshot** ("Enable Hibernation: Disabled → Enabled"; the
  first 12, then a count). The snapshot (`~/.config/openhw/hardware.json`) keeps the settings; it's also kept current
  at start whenever nothing changed, even when a warning opens the check.
- **Hardware Check warns when the BMC's Redfish service answers this PC without a password** (host interface in
  "AuthNone" mode, credential bootstrapping as HostInterfaceAdministrator — as found on the WRX90E), with how to lock
  it down. New helper `bmcaccess`.
- **Redfish reads fall back to the stored BMC login** when the BMC asks for one (401/403), so fan sources and BIOS
  settings keep working once the host interface requires authentication. Gzip-stored registry files are read.
- Helper **protocol 18**.

## 0.35.0 — 2026-11-17

- **Fan alarm thresholds**: Fan Control → a BMC fan card's ⋮ → **Alarm thresholds…** sets the RPM below which the BMC
  logs the fan as slow (lower non-critical), failing (lower critical) and stopped (lower non-recoverable). The BMC's
  defaults (360/360/0 RPM on the WRX90E) made CHA_FAN2 at a 20 % curve log "lower critical" at every boot. Only values
  the BMC can store are offered (120 RPM steps), in order; Apply writes once (IPMI Set Sensor Thresholds) and reads
  back; the thresholds a fan had before OpenHW first changed them are saved in
  `/var/lib/openhw/bmc-thresholds-original.json` for **Restore original**. Helper **protocol 17**: `bmcthresh`,
  `bmcthresh <sensor> <lnr> <lcr> <lnc>`, `bmcthreshoriginal <sensor>`; writes are logged in the helper's journal.

## 0.34.0 — 2026-11-17

- **File → BMC Status & Event Log…** (helper protocol 16, read-only): the BMC's discrete sensors — CPU_ECC,
  Memory_Train_ERR, PSU presence/AC/over-temperature, watchdog, BIOS/CPU/memory/PCIe hardware change — with their
  asserted states in words (IPMI generic and sensor-specific tables), and its System Event Log, newest first, decoded
  (time, sensor by SDR name, event, deasserted). The log is walked only when the BMC's "last added"/"last cleared"
  time changes. Nothing clears the log or changes the BMC. New helper commands `bmcstates` and `bmcsel`.
- **GPU fans on slot-sensor curves don't wait for the BMC.** The BMC answers ~20 s after OpenHW starts (and slowly
  after a BMC reset); until then a GPU fan whose curve follows a BMC PCIe slot sensor (= the card's core temperature)
  sat on automatic. Now, while the only missing readings are slot sensors, the curve reads the fan's own card's core
  temperature (NVML), and goes back to the slot sensor once it reads (both logged).
- **Fans are given back at once when OpenHW is stopped** (`pkill`, logout, systemd stop): SIGTERM and SIGINT run the
  same exit as the tray's Exit, so a restarted OpenHW no longer has its sets refused as "controlled by another client"
  until the old instance's 15 s lease runs out. "released N fan(s) (exiting)" in the journal.

## 0.33.2 — 2026-11-17

- **Fan diagnostics in the journal** (`journalctl --user -t openhw`, lines "openhw: fans: …"): a fan taken (with its
  curve and temperature), a GPU fan the helper no longer holds, a refused duty (once per reason), a curve without
  readings (once, and how long until they're back), a release. Sensor readers slower than 2 s are named
  ("openhw: sensors: … took N s"); the helper logs when a lend suspends the GPU fans and when they're available again.
  For finding why the second lend's GPU fans came back after ~57 s instead of ~10 s.
- **GPU Lending's names are OpenHW's**: the hook's record is `/run/openhw-lending/state.json`, its journal tag
  `openhw-lending`, and its libvirt launcher `qemu.d/50-openhw-lending` (OpenHW's launcher under the old name
  `50-windows-gpu` is removed, so the handoff never runs twice).

## 0.33.1 — 2026-11-17

- **GPU fans come back to their curves after a lend.** A lend hands every GPU fan back to its card's own control (not
  only the lent card's), and the helper doesn't tell the app; the curve runner only sent a new speed when the curve's
  output changed, so after Return the fans stayed on automatic until a temperature moved (seen in the first lend on
  the WRX90E: about a minute). Now the runner asks the helper every 5 polls which GPU fans it still holds and sets any
  it lost again at once.

## 0.33.0 — 2026-11-17

- **GPU Lending follows the card for its whole handoff.** The libvirt hook that does the hardware handoff was
  RTXinVM's, with the card hard-coded: after the board swap it still named slot E1 — and the UUID of the *other* PRO
  6000. OpenHW now ships its own hook (`/usr/lib/openhw/lending/hook.py`; `qemu.d/50-windows-gpu` runs it) that reads
  the card (GPU UUID + the slot the helper found it in) and the VM from `card-filter.json`, and checks that the VM
  passes that card through. RTXinVM's launcher is kept in `/etc/openhw/lending-off/50-windows-gpu.rtxinvm`. Lend
  refuses while OpenHW's hook isn't in place.
- **Desktop audio leaves the lent card's HDMI audio alone**: the helper writes a WirePlumber rule for the chosen card
  (`/etc/wireplumber/wireplumber.conf.d/51-openhw-lending.conf`; RTXinVM's per-user rule named the old slot). The
  page lists it with the other lending files, and when WirePlumber already holds the audio it says to restart it.
- **Hardware Check stops flagging harmless boot-limit lines**: once OpenHW's drop-in runs every line with `-` (each
  may fail alone), nvidia-power-limit.service's lines for empty slots, or for cards that have their own "at boot"
  value, are no longer a warning — that warning reopened the check at every start.

## 0.32.0 — 2026-11-17

- **Bulk sensors**: a graph curve can follow **several temperatures — the hottest counts**, exactly what the BMC does
  with a fan's ticked sensors (as in its web console). The temperature source is now a checkbox list (BMC sensors first,
  each with its live reading) with **All DIMMs** and **All PCIe slots** toggles; the card shows "Hottest of N: …". On a
  BMC fan such a curve runs on the BMC exactly (no approximation).
- **Mix cards show what the BMC would run**: a small preview — white = the mix with every sensor at the same
  temperature, green dashed = the fitted BMC curve, a dot at the hottest sensor now — and a ⚠ note when the BMC can't
  keep the mix: Min / Average over different sensors, or different curves on different sensors with Max (the BMC never
  runs slower than the mix, but can run faster). How far the 5-point curve sits above it is shown too.

## 0.31.0 — 2026-11-17

- **Drag and drop to reorder** both the fan cards and the curve cards: press on a card's background or a label, drag,
  and an orange bar on the card under the pointer shows which side it lands on. The card's own controls (names,
  dropdowns, buttons, the curve preview) keep their clicks. Curve cards remember their order too (and it's kept in
  configurations); new curves still sort flat, graph, mix after the ones you've placed. The ⋮ Move items stay.

## 0.30.0 — 2026-11-17

- **Reorder the fan cards**: every fan card's **⋮** has Move left, Move right, Move to start and Move to end (as OpenFan's
  card menu). The order is saved (and kept in configurations); a fan that's away — a card lent to the VM — keeps its
  place for when it's back. BMC fans' ⋮ still has Edit BMC curve… and Restore original curve below.

## 0.29.0 — 2026-11-17

- **Fan configurations, as in OpenFan**: Fan Control's **⋮** menu — Create new configuration… (Ctrl+N), Save
  configuration (Ctrl+S), Save configuration as… (Ctrl+Shift+S), Load configuration… (Ctrl+L). A configuration holds the
  curves, which fan runs which, and the fans' names (`~/.config/openhw/fan-profiles/` by default). The file in use is
  in the window's title and, as in OpenFan, every change also saves there.
- Loading (or starting new) says first what happens: NVIDIA and OpenHW-driven fans follow at once; **BMC fans keep the
  curve the BMC has until Apply** (one write each) — and **Apply all to the BMC (n)** writes them together after a
  confirm. Nothing is written to the BMC by loading alone.
- Verified: both RTX PRO 6000s' fans follow their curves through NVML (slot 5 at 31 °C → 35 %, slot 1 at 33 °C → 36 %).

## 0.28.1 — 2026-11-17

- Fixed: + Flat / + Graph / + Mix did nothing since 0.27.0 — their menu (New / Duplicate) was filled only as it opened,
  and an empty menu never opens. It's now built on each click, then shown.
- Verified on the real BMC: CHA_FAN1 runs the owner's "MemoryA1" curve (DIMMA1, 50:30 55:52 60:74 **80**:100 100:100):
  **point 4 at 80 °C is accepted** — the 70 °C rule is gone for good.

## 0.28.0 — 2026-11-17

- **Friendly fan names**: click a fan card's name to rename it (e.g. CHA_FAN1 → "Memory"); the tooltip always shows
  the original name and id; an empty name goes back to the original. The names are used across Fan Control (curve
  cards' "Fans:", dialogs, messages) and saved in fan-curves.json.
- Clearer wording on what a BMC-run curve costs: **one write to put it on the BMC** (two if its sensors change), like
  Save in the BMC web console, **then none** while it runs; only changing it again writes again. ("No flash writes"
  read as if applying wrote nothing.)

## 0.27.0 — 2026-11-17

- **Fan Control… in the tray menu** (after Power Limits…).
- **+ Flat / + Graph / + Mix open a menu**: a new curve, or **Duplicate** any curve of that kind already made (named
  "… copy", "… copy 2"; a duplicated graph opens in the editor). Fans keep the curve they had.
- The orange buttons stay orange under the pointer.

## 0.26.1 — 2026-11-17

- Fixed: a fan card's RPM (and a curve card's temperature) froze when its group was collapsed in the sensor window.
  While Fan Control is open, every fan's RPM and duty and every curve's sensors stay live. (No RPM calibration, as in
  OpenFan: the BMC and the NVIDIA driver report every fan's real RPM.)

## 0.26.0 — 2026-11-17

- **One Fan Control screen, like OpenFan's Home** (owner's request). Power → Fan Control… now holds both: **Controls** — a
  card per fan with a **Curve** box and the curve it runs (picking a curve arms the fan; Monitor = none) — and
  **Curves** — flat cards with − / + , graph cards with their temperature source, the live output, a small preview of
  the curve with the sensor's current reading as a dot on it, and Edit (the full graph editor), mix cards with their
  function and curves (add / ×). + Flat / + Graph / + Mix make new ones. Each card says whether it runs on the BMC (no
  flash writes) or costs flash writes on a BMC fan. Power → Fan Curves… is gone (merged in).
- **NVIDIA fans are controls too**: every fan of every NVIDIA card gets a card and can run any curve, through the
  NVIDIA driver (NVML) in openhw-helper — **helper protocol 15**: `gpufans`, `gpufanset`, `gpufanrelease`, leases
  renewed by `fanbeat`. 0 % = the card's own automatic control; other duties are clamped to the card's minimum.
  Nothing is stored on the card, so there's no write budget; a fan goes back to the card's own control when you
  untick it, OpenHW exits, its lease runs out (15 s), the helper stops, or the card is lent to a VM. Refused while
  OpenFan's helper runs (it writes the same fans).
- A BMC fan's own curve (the old Edit…) and Restore original moved to the card's ⋮ menu.

## 0.25.0 — 2026-11-17

- **Fan curves like OpenFan's** (fan control stage 2, part 2). **Power → Fan Curves…**: make **graph** curves (any
  number of points over any temperature OpenHW reads; drag, click to add, hysteresis °C/s, max speed, axis range — the
  OpenFan editor, ported), **flat** curves and **mixes** (max / min / average of other curves). **Power → Fan
  Control…** → pick a curve per fan.
- **Where a curve runs is decided by its sensors.** Only the BMC's own sensors (CPU Package, T_Sensor, LAN, the eight
  DIMMs, the PCIe slots) → it **runs on the BMC**: OpenHW turns it into the BMC's sensor choice and five points, writes
  them once, and nothing is written afterwards. The editor draws that BMC version (green, dashed) over your curve; it's
  never slower than drawn (a mix becomes one curve over the hottest of its sensors). Any other sensor (an NVIDIA GPU
  via NVML, a drive, …) → **OpenHW drives the fan**, with a warning first: every duty change is a write to the BMC's
  flash, so duties move in 5 % steps, faster at once, slower only after a minute, behind the curve's hysteresis, and
  Fan Control shows the writes per day. Without readings for 20 s, or when OpenHW exits, the fan goes back to its BMC
  curve.
- **The PCIe slot sensors read the GPUs**: measured 2026-11-17 (`tools/gpu-slot-test/`), PCIE01/04/05 follow the core
  temperature of the GPU in slot 1 (PRO 6000, F1), 4 (5060 Ti) and 5 (PRO 6000, C1) within about 1 °C, live — so
  GPU-following curves run on the BMC too.
- **Point 4 of a BMC curve is free** (it was held at 70 °C): the BMC web console edits points A–D; only point E sits at
  100 °C. If this firmware ever refuses a point 4 other than 70 °C, OpenHW refits with 70 and remembers. Fan Control →
  Edit… lets you set point 4 too.
- Curves are saved in `~/.config/openhw/fan-curves.json`.

## 0.24.3 — 2026-11-16

- Fixed: Fan Control's Edit… and BMC login dialogs opened at the far left of the secondary monitor. Every OpenHW
  dialog — those two, Rename, Customize values, the color picker, Main Settings, About and message boxes — now opens
  centred on the primary monitor the first time and then where you last put it, like the main windows.
- Verified on the real BMC with 0.24.2: a fan's sensor change applies with no error (CHA_FAN1 follows DIMMA1_Temp).

## 0.24.2 — 2026-11-16

- Fixed: changing which sensors a fan follows reported "Redfish 400: … Oem/PWM/1/Temperature1 is a required property"
  even though the BMC had applied the change. The BMC wants each fan entry it's sent to be complete, and refuses a
  whole list over the unused 8th slot (while still applying the rest). OpenHW now sends only the one fan's complete
  entry, as read, with its sensors changed (plus the mode).
- **Hardware Check no longer opens at every start** for things you've already seen: closing it marks what it showed as
  seen, and it opens by itself again only when something new needs attention (a change, a missing sensor, a new
  warning). File → Hardware Check… still opens it any time.

## 0.24.1 — 2026-11-16

- Fixed: changing which sensors a fan follows failed with "Redfish 400: The property Oem/Mode is a required property"
  — the WRX90E's BMC wants the fan mode in every change. OpenHW now sends the mode as it reads it (so it never
  changes). Nothing was written by the failed attempts.

## 0.24.0 — 2026-11-16

- **Fan curves the BMC runs** (fan control stage 2, part 1 — the owner's hybrid plan). Power → Fan Control… → **Edit…**
  per fan: tick which of the BMC's own temperature sensors it follows (CPU Package, TR1/T_Sensor, LAN, each DIMM slot
  DIMMA1–H1, each PCIe slot PCIE01–07; several = the hottest counts) and set its 5-point curve (the firmware fixes the
  last two points at 70 and 100 °C). **Apply writes once**; the BMC keeps the setting (in its flash — verified with a
  BMC cold reset) and runs it from then on, with or without OpenHW. No ongoing writes.
- **Restore original**: the curve a fan had before OpenHW first changed it is kept (`/var/lib/openhw/fan-original.json`).
- **Floors**: CPU_FAN never below 20 %, W_PUMP+ never below 50 % in a configured curve.
- **Sources go through Redfish** (this firmware refuses the IPMI source command): the BMC's `Thermal → Oem.PWM[n].Source`,
  a bitmask over its temperature sensors (bit = sensor number − 1), decoded with the owner. **Give OpenHW the BMC
  login…** hands a BMC user to the helper (typed, or read from a file with the user and password on two lines); the
  helper checks it with a real login and keeps it root-only (`/etc/openhw/bmc-login`). The BMC's certificate is pinned
  on first use. Curves themselves still go over IPMI and need no login.
- openhw-helper protocol 14: `fancurve`, `fanoriginal`, `fansources`, `fansource`, `bmclogin`, `bmclogout`. Its sandbox
  now allows network access to the BMC's link-local host interface (169.254.0.0/16) only.
- Fan Control shows which sensors each fan follows; the page and BMC sensor names show underscores correctly.

## 0.23.0 — 2026-11-16

- **Fan driver, stage 1** (owner's plan: one fan driver in openhw-helper that OpenHW — and, until it's retired,
  OpenFan — use). On the ASUS Pro WS WRX90E-SAGE SE the BMC drives every fan header (the Super I/O only measures
  them), so the driver works through the BMC's own fan zones: CPU_FAN (+ CPU_OPT), CHA_FAN1–5 and W_PUMP+, with
  ASUS's OEM IPMI commands — ported from OpenFan 0.4.6's tested driver onto OpenHW's own IPMI code (no `ipmitool`,
  no network, no BMC login). Writes only on the validated board, BMC identity, firmware 2.01 and zone layout.
- A program **takes a fan over** by asking for a duty (1–100 %; the firmware refuses 0 %), written as a flat curve and
  read back. The fan goes back to the BMC's own curve when that program **releases it, stops renewing (15 s), the
  helper stops, or — after a crash — the helper starts again** (each curve is journaled in `/var/lib/openhw` before
  the first change). One program per fan; nothing is taken over while OpenFan's own BMC driver holds fans.
- **Flash protection**: a faster duty is written at once; a slower one only 30 s after the last write and in steps of
  at least 3 %, waiting as "pending" meanwhile. The number of writes per fan is counted and shown.
- **Power → Fan Control…** shows each fan's live RPM, who controls it, its active curve and its write count.
  `openhw --fan-test bmc:<n> <1–100> --seconds N` checks one fan: takes it, holds the duty, gives it back and compares
  the curve. Curves of your own (any sensor, as in OpenFan) come in the next stage.
- openhw-helper protocol 13: `fans`, `fanset`, `fanrelease`, `fanbeat`; the unit gets `StateDirectory=openhw`.

## 0.22.0 — 2026-11-14

- **Board profile for the ASUS Pro WS WRX90E-SAGE SE** (Super I/O NCT6798), named after the board's own BMC: the
  voltage rails get the BMC's names and dividers (+VCORE_0, +VCORE_1, +VSOC, +VDDIO, +5V, +12V, +3.3V,
  +CHIPSET_1.05V, +VDD_11_S3 …), so +12V reads 12 V instead of 1 V at the pin; the seven fan inputs are the BMC's
  headers (CHA_FAN1–5, CPU_FAN, W_PUMP+); open temperature pins and the Intel-only PCH registers are hidden. Matched
  on Linux alone by reading the Super I/O and the BMC side by side across an idle → per-CCD → all-core sweep
  (`tools/bmc-probe/match.py`; evidence in docs/board-profiles.md). Four inputs stay flagged as unconfirmed.
- Fixed: **GPU Lending grayed everything out when libvirt was down** ("openhw-helper isn't answering: virsh: error:
  failed to connect to the hypervisor"). Only the VM list depends on libvirt now; the page says libvirt isn't
  answering and the card and filter stay usable.
- Fixed: **a card that's no longer in the machine couldn't be replaced** while its filter was on (the card list was
  disabled). Choosing a card now turns off a filter that guards a missing card; tick "Keep this card away" again
  afterwards.

## 0.21.0 — 2026-11-14

- **Board management controller (BMC) sensors.** On boards with a BMC that Linux exposes as `/dev/ipmi0` (here the
  Pro WS WRX90E-SAGE SE's ASPEED AST2600), a new group shows every sensor the BMC reads, under the board's own
  names: the voltage rails (+VCORE_0/1, +VSOC, +VDDIO, +VDD_11_S3, chipset, +12V/+5V/+3.3V and standby, VBAT),
  CPU package, LAN and per-slot PCIe temperatures, a temperature for **every** DIMM slot, every fan header (CPU_FAN,
  CHA_FAN1–5, W_PUMP+, USB4, M.2, VRM heatsinks) and PSU readings where the PSU reports them. Tooltips list the
  BMC's own warning and critical limits. Sensors the BMC can't read right now (no PSU telemetry, empty slot,
  disabled header) stay out of the list until they read. openhw-helper reads the BMC (protocol 12, new `bmc`
  command) with read-only IPMI commands only — sensor repository, readings, thresholds — and keeps the readings
  fresh on its own thread, since a full sweep over the KCS interface is slow.
- **File → Hardware Check…**: what changed since OpenHW last saw this machine (motherboard, BIOS, CPU, memory, BMC,
  sensor chips, GPUs and drives — added, gone or moved to another slot), saved settings that point at sensors that
  aren't here any more (tray icons, alerts, logging, names, hidden, custom values) with the sensor each most likely
  became — **Move** or **Remove** — and what OpenHW can't fully read on this machine, with the fix where there is
  one (a kernel driver to load, an old helper, a stale GPU Lending card, DIMMs only the BMC can see, a board
  without a profile). OpenHW keeps a snapshot of the hardware in `~/.config/openhw/hardware.json`; about 30 s
  after it starts it compares, and opens Hardware Check by itself when something needs a look (not while a card
  is lent to the VM). `openhw --hardware-check` prints the same report without a display. It also flags boot GPU
  power-limit lines that name a PCI slot which is now empty or holds another card.
- **GPUs are no longer tied to PCI slots.** GPU Lending remembers its card by GPU UUID and follows it to its current
  slot whenever openhw-helper starts (older settings adopt the UUID of the card in their slot; a different card in
  the old slot is never filtered). Boot GPU power limits name the card (`nvidia-smi -i GPU-<uuid>`) instead of its
  slot, come after nvidia-power-limit.service's own per-slot lines so they win, and every line may fail on its own,
  so an empty slot no longer stops the remaining cards' limits. OpenHW's per-slot lines from before are dropped at the
  next change (which card they meant can't be known after a move); Hardware Check lists them.
- Explanations for the BMC's sensors and for the raw Super I/O and EC names of a board OpenHW has no profile for.
- Fixed: a live-machine test assumed the TRX50 workstation's CPU.

## 0.20.2 — 2026-10-28

- **No limit on tray icons** (was 8): File → Tray Icons… and Sensor Settings → Tray Icon → Show In Tray add as
  many as you like.
- Sensor Settings → Tray Icon → **Add All Items To Tray** is grayed out: with no limit it would put every sensor
  (several hundred) in the top bar.

## 0.20.1 — 2026-10-27

- Fixed: after a boot where Linux numbered the hwmon chips differently (e.g. `asusec` registering first and taking
  `hwmon9`), sensors saved under the old number showed as "(not present now)" — a tray icon for
  `amd_hsmp_hwmon` socket power, an alert, a logged sensor — and renamed, hidden, reordered or collapsed hwmon groups
  lost their settings. OpenHW now moves every saved choice to the same sensor's new number when the sensor list
  loads or changes. It only does so when exactly one chip of that name has that channel, so drives
  (several `nvme` chips) are never guessed at.

## 0.20.0 — 2026-10-26

- New switches in **Sensor Settings → General**, for things OpenHW always did (all on by default, so nothing changes
  until you turn one off): **Row Shading**, **Highlight changing values**, **Show ToolTips**, and **Show columns**
  Current / Minimum / Maximum / Average. Also **Always On Top** (off by default) for the sensor window.
- **Averaging Interval → Average last N samples**: the Average column can cover just the last N samples; 0 (the
  default) is still every sample since the last reset. Minimum and Maximum still cover the whole run.
- **Alerts → Change Item Color**: whether a firing alert turns the sensor's Current value red (on by default, as before).
- **Main Settings → Validate Window Positions** (on by default): a window saved on a monitor that's gone opens on
  the primary monitor; off, windows go exactly where they were saved.
- **Main Settings → Enable NVML, SMBus Support, S.M.A.R.T. Support**: turn off OpenHW's GPU, DIMM / board VR or
  NVMe S.M.A.R.T. readers. Their sensors leave the list until turned back on; drive temperatures stay, and GPU
  Lending works either way.
- **File → Sensor Settings…** opens the same dialog as the footer gear.
- Fixed: Driver Management's **Current Status** box cut off "Running (protocol 11)"; it now grows to fit.

## 0.19.0 — 2026-10-26

- **Sensor Settings and Settings now change real options**, the ones OpenHW already had. Each is the same setting
  as in OpenHW's other windows, saved the moment you change it, and a change made in either place shows in the
  other right away (OK and Cancel just close):
  - General → **Polling Period → Global** = File → Settings → Poll every (any ms; OpenHW uses the nearest of 1, 2, 3,
    5 or 10 s).
  - **Layout** = View → Layout Settings…: Monitoring, Show / Hidden items, the order arrows, Restore Original Order.
  - **Customize** = the row's Rename… and Customize values… and Enable Logging: Label, Unit, Multiply / Add (+),
    Decimal Digits, Logged.
  - **Tray Icon** = File → Tray Icons… and the row's Add to tray: Show in Tray, the Background color, Add All Items
    To Tray (up to 8 icons), Remove All Items From Tray.
  - **Alerts** = File → Alerts…: Enable Alerting, If value < / If value >= with their thresholds (OpenHW's "below" and
    "above"; above means more than), Number of samples to trigger alarm (× the poll interval). Display Warning Window
    is always on: OpenHW always shows a desktop notification.
  - Main Settings → **Auto Start** and **Minimize Main Window on Startup** = File → Settings → start at login and
    "with only the tray icons". **Driver Management → Current Status** shows whether openhw-helper is running.
- Everything else in the two dialogs is still layout only and changes nothing.

## 0.18.0 — 2026-10-26

- The footer's **settings gear** now opens **Sensor Settings**, laid out like HWiNFO's: General, Layout, Customize,
  Tray Icon and Alerts, with OSD, OSD (RTSS), Logitech LCD and HWiNFO Gadget grayed ("To be implemented"). Its
  **Main Settings** button opens **Settings**: General / User Interface, Safety, SMBus / I2C and Driver Management,
  with License Management grayed.
- **Scaffolding only so far**: the controls work but change nothing yet, and OK and Cancel just close. The sensor
  lists show your real sensors and whether each one is monitored, logged, in the tray or has an alert. Options will
  be connected one at a time. File → Settings and the tray's Settings… still open OpenHW's own Settings window.

## 0.17.2 — 2026-10-26

- Fixed: **OpenHW's windows opened in the top-left corner of the secondary monitor**, and forgot where you had put
  them when OpenHW restarted. Every window — Settings, Tray Icons, Alerts and each sensor's alert settings, sensor
  graphs, Layout Settings, CPU Frequency, Power Limits, GPU Lending — now opens where it was last time
  (`~/.config/openhw/window-places.json`; size too for windows you resize freely, such as graphs). With nothing saved,
  or saved on a monitor that's no longer there, a window opens centred on the primary monitor; so does the main window
  the first time. Dialogs still open centred on the window that opened them.

## 0.17.1 — 2026-10-26

- Fixed: **all tray icons disappeared after the screen was locked and unlocked** (and whenever GNOME's AppIndicator
  extension was turned off and on). The code that registers the icons again when the tray comes back read the wrong
  callback argument and crashed, which closed every icon's bus connection; OpenHW kept running with no icons. The
  icons now come back, one at a time in their saved order, and a lost tray connection is written to the log.
  `tools/tray-relock-test/run.sh` checks it in a private headless GNOME Shell.

## 0.17.0 — 2026-10-26

- GPU Lending: **Install / Uninstall** at the top of Power → GPU Lending…. Uninstall takes lending out of the system:
  the card filter leaves the kernel, openhw-helper no longer starts before the login screen, GNOME is no longer kept
  off the card, and libvirt's handoff hook is put away (in `/etc/openhw/lending-off/`). The page then shows only
  Install, and no new-app notifications come. Your card, VM, list and filter setting are kept: Install puts
  everything back as it was. Reboot once after either to finish. Uninstall waits until the card is returned.
- The helper's start before the login screen moved out of the packaged unit into a drop-in that Install writes
  (`/etc/systemd/system/openhw-helper.service.d/lending.conf`); an upgrade with lending set up writes it at once.
  OpenHW's own udev rule (`61-openhw-lending.rules`) replaces RTXinVM's `61-windows-gpu.rules` (kept in
  `/etc/openhw/lending-off/`). Helper protocol 11 (`lendinstall`, `lenduninstall`).

## 0.16.3 — 2026-10-26

- Fixed: **an app running in two scopes got two notifications, and lending treated it as two apps.** Chrome (and
  Electron apps such as Claude) moves its main process into a scope of its own (`app-com.google.Chrome-<pid>.scope`),
  while the processes it already started, including its GPU process, stay in the scope GNOME launched it in
  (`app-gnome-google\x2dchrome-<pid>.scope`). OpenHW now treats scopes from one launch as one app: one question, and
  the answer covers all of them. Ticking either scope is enough to lend; lending closes them all and opens the app
  once, with its own command (before, the second scope blocked the lend, or would have "reopened" Chrome's crash
  handler). Overlapping checks can no longer ask twice.
- **GPU lending replaces RTXinVM** (step 5 done): verified on the owner's machine after a reboot — the desktop never
  opens the lent card, a terminal sees all three GPUs, and a full Lend → Return closed and reopened the ticked apps
  and put back the card's power limit.

## 0.16.2 — 2026-10-26

- GPU Lending: **a notification when a new app opens the lent card.** With the filter on, any app that isn't on the
  list can open the card just by drawing with the GPU — and then the card can't be lent. The first time an app
  nobody has decided on holds it, OpenHW asks once: **Hide it** (now and from boot), **Close & reopen when
  lending**, or **Leave it** (it may use the card; no more questions). A click on the notification opens GPU Lending.
- Fixed: Hide didn't work for apps whose unit name systemd escapes (e.g. a "weather-widget" app runs as
  `app-gnome-weather\x2dwidget-….scope`): the helper refused the backslash.

## 0.16.1 — 2026-10-26

- Fixed: the card filter couldn't be turned on by the installed helper — its sandbox (`ProtectKernelTunables`) left
  the kernel's BPF filesystem read-only, where the filter keeps its program. The unit now lists the kernel tunables
  it protects as read-only paths instead (`/proc/sys`, `/sys/kernel`, …). Checked with `tools/sandbox-bpffs-test.sh`.
- Fixed: a failed "turn the filter on" was still remembered as on (and retried at every boot); it now stays off.

## 0.16.0 — 2026-10-26

**Ready to replace RTXinVM (GPU lending, step 5 — code)**
- openhw-helper now starts before the login screen and tells systemd when it's ready (`Type=notify`). With the card
  filter on, it first waits (up to 60 s) for the lent card's device files, so the filter is in force before GNOME
  starts — "hide from boot" is airtight from the first login. With the filter off, it's ready at once; it always
  reports ready, even after an error, so it can never hold up the login screen.
- GPU Lending: **Close and reopen when lending**, per app. An app holding the card no longer has to be closed by
  hand: tick it (e.g. OpenFan's window) and lending closes it gracefully (by stopping its app scope), reopens it
  with the same command once the card is handed over, and again when the card comes back. The new scope is named
  like the original, so the list entry covering it still does. Apps that aren't ticked still block the lend.

## 0.15.1 — 2026-10-25

First real lend (Windows-10-LTSC on the RTX 6000) worked; two fixes from it:
- Fixed: a user service started with `systemd-run` (e.g. OpenFan's window under RTXinVM) wasn't started again after
  the lend. systemd reports a FragmentPath even for transient units, so it was taken for a unit file and only
  `systemctl start`ed — but a stopped transient unit is gone. Transient units (`Transient=yes`) are now recreated
  with the same command, slice and description, after the lend and after the return.
- Fixed: while the card was lent, the page showed the libvirt hook's handoff record as a problem; the hook keeps it
  on purpose until the return.

## 0.15.0 — 2026-10-25

**Lend the card to a VM (GPU lending, step 4)**
- Power → GPU Lending… now has a **Lend** section: choose the VM (only libvirt VMs with the card assigned are
  offered), see what stands in the way, then **Lend to <VM>** and later **Return**.
- Lend refuses while anything would stop the handoff — the VM not off, a monitor plugged into the card (or a
  framebuffer from one at boot), a leftover handoff record, or an app holding the card (close it, or hide it and
  restart it) — and names it. What can be paused is paused for you and started again afterwards: the desktop user's
  services holding the card (by OpenHW's window; transient `systemd-run` units are recreated with the same command
  and slice) and system services (by openhw-helper, which also lets go of its own NVML handles and turns
  persistence off so nvidia-persistenced lets go). Then it puts back the card's power limit and persistence for the
  libvirt hook to record and starts the VM; any failure puts everything back.
- Return asks the VM for a normal shutdown (guest agent, else ACPI; never forced). However the VM is shut down, the
  helper notices the card is back, reopens NVML and restarts what it paused; OpenHW restarts the user's services
  so they list the card again (even with the GPU Lending window closed).
- Helper protocol 10: `lendstatus`, `cardvms`, `cardvm`, `lend`, `vmreturn`.

## 0.14.1 — 2026-10-25

- Fixed: the GPU Lending page still couldn't see the desktop user's programs holding the card — listing another
  user's `/proc/<pid>/fd` (mode 0500) needs `CAP_DAC_READ_SEARCH` as well as `CAP_SYS_PTRACE`; the unit grants both.
- Programs outside the desktop session are listed one per program, so OpenHW's own helper and other services each
  get their own line.

## 0.14.0 — 2026-10-25

**GPU Lending page (GPU lending, step 3)**
- Power → **GPU Lending…** (and the tray menu): choose the card to lend, turn the card filter on or off, see what
  has the card open right now — grouped by the list entry that would cover it, with **Hide now** / **Hide from
  boot** — and edit the list (Hide now, Hide from boot, Remove). Programs that are hidden but still hold the card
  are flagged ("restart it", or for the desktop "log out once"); system services that hold it are named; OpenHW's
  own helper is explained (it lets go of the card for the handoff).
- While RTXinVM's guard is running, the page says it is protecting the card and the filter should stay off.
- Fixed: the helper couldn't see which of the desktop user's programs had the card open (reading another user's
  `/proc/<pid>/fd` needs `CAP_SYS_PTRACE`, which the unit now grants for that), so the list showed only its own use.

## 0.13.0 — 2026-10-25

**Card filter (GPU lending, step 2) — built and tested, off until the switch-over**
- openhw-helper can now keep one NVIDIA card away from listed programs, as RTXinVM's guard does today, but with an
  editable list instead of "everyone except rtxgpu": a small eBPF cgroup device program (assembled in C#, ported
  from RTXinVM) refuses opens of that card's device nodes to listed programs; everything else — terminals, vLLM,
  llama.cpp, nvidia-smi — keeps seeing it. A program that starts before the helper has looked at it is refused
  until it has, so hiding is airtight; new apps are classified within about 60 ms (inotify on the cgroup tree).
- The list starts with the GNOME desktop, the login screen and OpenHW's own window, each "hide from boot"; entries
  can also be hidden or shown just until reboot ("now"). The filter stays in force across helper restarts.
- Helper protocol 9: `cardfilter [on|off]`, `cardselect <pci|none>`, `cardhide|cardshow <pattern> <now|boot|both>
  [name]`, `cardforget <pattern>`; the status lists every process holding the card and the entry that covers it.
- `openhw-helper --selftest-card-filter <pci>` (root) checks it all in a throwaway cgroup beside the desktop: 11 of 11
  pass on this machine. It is **not enabled** here: RTXinVM's guard keeps protecting the card until OpenHW can also
  lend it (step 4). The setup page (step 3) comes next.

## 0.12.1 — 2026-10-25

- Fixed: **Exit (tray menu) crashed OpenHW** on the way out — an unhandled exception, sometimes an abort (exit code
  134) — which could also stop Settings → Restart OpenHW now from starting the new instance. Two causes: Avalonia's
  own session-bus connection (its settings watcher) reported its closing to a UI thread that had already stopped,
  and a queued power-mode update tried to refresh tray menus that had just been removed. OpenHW now closes that
  connection while the UI thread still runs, and late tray updates are dropped. Checked with a new test,
  `tools/tray-exit-test/run.sh` (private display and session bus, Exit clicked over D-Bus as GNOME does): 14 of 14
  runs failed before, 12 of 12 exit cleanly now.
- Avalonia's global-menu export over D-Bus is off (GNOME doesn't show it; the menu bar is in the window).

## 0.12.0 — 2026-10-25

**The window no longer opens any GPU** (helper protocol 8 — install the .deb)
- Every NVIDIA reading (the GPU groups, and the Power Limits window's list) now comes from openhw-helper, which runs
  NVML as a system service; the window asks it over the socket (`gpus`, `gpuread`, `gpulimits`). The window doesn't
  load NVML at all.
- Why: a system service is never hidden from a card by a device filter on the desktop session, so OpenHW sees every
  GPU however it's started — RTXinVM no longer needs to launch it through `rtxgpu`, and the window can't hold a card
  that's about to be lent to a VM. While the helper restarts (e.g. around a GPU handoff) the GPU rows stay and go
  gray instead of disappearing.
- First step of folding RTXinVM's GPU lending into OpenHW (ROADMAP "GPU lending to a VM").

## 0.11.1 — 2026-10-25

- Fixed: hovering a footer button dropped its frame and flattened it (Avalonia's default). It now keeps the frame
  and lightens the face, as HWiNFO's do; pressed darkens it.

## 0.11.0 — 2026-10-25

**Footer like HWiNFO's**
- The bottom bar now matches HWiNFO's: expand all / collapse all on the left; the elapsed time, reset (clock),
  **settings (gear, new)** and close on the right. The icons are redrawn after HWiNFO's — light-blue outward arrows,
  steel-blue inward arrows, a white clock face, a silver gear, a glossy blue X — on larger buttons.
- The gear opens File → Settings for now; it will lead to a HWiNFO-style settings menu next.
- Removed from the footer: the power-mode list, CPU frequency and power limit buttons and the "Capped at …" button.
  They're all in the Power menu (and the tray menu); a failed power action still shows a short message in the footer.

## 0.10.1 — 2026-10-25

**Works with RTXinVM's launcher**
- Fixed: saving any setting rewrote OpenHW's login entry, replacing a launcher another tool had put there — RTXinVM's
  `rtxgpu --app openhw`, which starts OpenHW in the slice that can see the VM's GPU and lets RTXinVM stop it before
  switching the card. OpenHW now keeps such an entry and never rewrites it; "Start OpenHW when I log in" turns it
  off and on in place, and Settings shows the command it runs.
- Fixed: Settings → Restart OpenHW now, under a systemd user service such as `rtxgpu-openhw.service`, would have
  closed OpenHW for good (systemd stops the unit and kills what's left in it when the main process exits). A small
  waiter outside the unit now starts the same unit again — same name, slice and description — once it has stopped.

## 0.10.0 — 2026-10-25

**DIMM and VR sensors** (helper protocol 7 — install the .deb)
- Each DDR5 DIMM is now its own group, as in HWiNFO ("DDR5 DIMM [#0]: Kingston KF556R28-32 (AMDI0010:01 / 0x50)"):
  SPD hub temperature, TS0 and TS1 temperatures, the PMIC's VDD (SWA), VDDQ (SWB) and VPP (SWC) rails, 1.8 V and
  1.0 V LDOs, VIN, total power (or per-rail current/power, as the PMIC is set), and its high-temperature,
  over-voltage and under-voltage alarms.
- The board's memory-controller regulator, **Infineon XDPE19283B** (TRX50-SAGE): loop temperatures, VOUT, VIN, IOUT,
  IIN, output and input power, efficiency.
- Both are read by openhw-helper over the SMBus (new `ddr5` and `vr` commands; the unit may now open `/dev/i2c-*`).
  Reads only, except the DIMM PMIC's ADC select register, which is restored after each reading and only touched on
  a PMIC matching the module's SPD. `openhw-helper --once <command>` prints one reply, for checking with sudo.
- Not done: SVI3 telemetry (needs the SMU PM table; the kernel's HSMP metric table doesn't cover this CPU) and the
  ASUS EC's VRM voltage/current (needs the EC register map) — see ROADMAP.

## 0.9.0 — 2026-10-25

**Window scale**
- Fixed: when OpenHW starts at login with only its tray icons, the window opened from the tray ("Show sensors") was
  drawn at 100 % instead of the desktop's scale. OpenHW starts before GNOME publishes the scale to X11 apps
  (`Xft.dpi`, about a second later), and Avalonia 11.2 noticed the change but kept its cached screens. Avalonia is
  now 11.3.22, which re-reads them, so the window follows the scale whenever it arrives (reproduced and checked on a
  private X server: 1× before, 2× after).
- File → Settings → **Window scale**: Automatic (the desktop's scale, shown as "now N %") or a fixed 100–400 % for
  every OpenHW window. Applies at the next start; a **Restart OpenHW now** button next to it does that at once.

## 0.8.0 — 2026-10-24

**Tray units**
- Tray tiles show a short unit after the digits: a degree sign for temperatures (32°, also °F set in Customize
  values), and a small W, V, A or % for power, voltage, current and load (55W, 87%). Other units (MHz, RPM, MB,
  MB/s…) stay digits only: they're long, and their values are already shortened ("4.5k"). The tile stays square —
  GNOME's tray squeezes wider icons — so the digits get a little smaller to make room.
- File → Tray Icons… → "Show units on the tiles" (on by default) turns it off again for every icon.

## 0.7.0 — 2026-10-24

**Tray hover**
- Hovering over a tray icon on GNOME shows its sensor's details — reading, min/max/average, device and the full
  explanation — updated live while the pointer stays; the menu still opens on a click and hides it. GNOME's tray
  shows no tooltips, so the package now installs a small GNOME Shell extension,
  `openhw-tray-hover@russellmm.github.io`, which shows the tooltip OpenHW already publishes.
- File → Tray Icons… → Hover: on/off (GNOME's own extension switch; off until you tick it). After the first install,
  log out and back in once — Wayland only finds new extensions at login; a box ticked before then is on at that login.
- The tooltip's explanation is one paragraph (the menu keeps its short lines), so long explanations aren't cut off.

## 0.6.0 — 2026-10-24

**Layout and values**
- View → Layout Settings… (and a sensor's "Layout settings…"): HWiNFO's Layout tab — every group and sensor with
  its monitoring state, Monitoring / Show for the selected sensor, move sensors within a group and groups among
  groups (top, up, down, bottom), the hidden sensors listed below, Restore Original Order. New sensors keep
  OpenHW's place.
- Sensor menu → Customize values…: label, unit, multiplier, offset and decimal places, with a live preview. Used
  everywhere the value appears (window, graphs, tray, logs, alert thresholds). "Multiply by" multiplies (HWiNFO's
  takes the inverse) and accepts "." or ",".

**Settings**
- Pause sensors while the window is hidden (off by default): only tray, alerted, logged and graphed sensors are
  read while the window is hidden or minimized.
- Draw windows in software (no OpenGL), from the next start: about 65 MB less resident memory, a little more CPU
  while a window is open.

**Memory**
- Runtime options: TieredPGO off, GC conserve-memory, and a 4 MB gen0 budget (the launcher re-executes itself to
  set `DOTNET_GCgen0size`). With the window open: 322 → 293 MB resident, 219 → 187 MB private.

**Packaging**
- Debian package: `packaging/build-deb.sh` builds `dist/openhw_<version>_amd64.deb` with the app and openhw-helper
  installed system-wide (`/usr/lib/openhw`, `/usr/bin/openhw`, the helper service, the menu entry). Each release
  ships one from now on.
- Start at login falls back to `/usr/bin/openhw` when there's no per-user launcher.

## 0.5.0 — 2026-10-23

**Alerts**
- Per-sensor rules: above / below a value (in the sensor's unit) for a chosen time, or "becomes Yes/No" for Yes/No
  sensors. Firing shows a desktop notification kept until dismissed and turns the sensor's Current cell red; a "back
  to normal" notification follows. Sensor menu → Alert settings…; File → Alerts… lists every rule with its state.

**Sensors**
- Core effective clocks and their average (kernel APERF/MPERF average, no root).
- CPU Package Power (RAPL) and CPU Cores Power (RAPL), through openhw-helper v6 (`rapl`).
- GPU: 1 s average power and the PCIe error counters (NVML field values). Hotspot and memory-junction temperatures
  aren't available through NVML on these cards.
- The Super I/O shows as Nuvoton NCT6701D, as HWiNFO names it.

## 0.4.0 — 2026-10-23

- Logging is opt-in: no sensor is logged until you choose Enable Logging on it, so logs only hold what you want.
  Starting a log with none chosen explains how to pick sensors instead of writing an empty file.
- Tray icons have their own window, File → Tray Icons… (also in the tray menu and a sensor's "Tray icon
  settings…"): icons, order, colors, custom colors and the sensor search. Settings keeps the OpenHW-wide options
  (poll interval, start at login).

## 0.3.1 — 2026-10-23

- Fix: column header edges couldn't be dragged. The handles had no visual template, so they never received the
  pointer; they're now hit areas that show a faint line on hover. Tested with a simulated mouse drag.

## 0.3.0 — 2026-10-23

**Sensor right-click menu, filled in**
- Rename (F2): your own name for a sensor, shown in the window, tray, Settings, graphs and logs; sorting and
  explanations keep OpenHW's name, and the tooltip notes the original.
- Hide / Unhide (Shift+Del); View → Show Hidden Sensors and Unhide All Sensors.
- Disable / Enable monitoring (Del / Ins): the sensor isn't read and its values are blank.
- Show Graph: a live 30-minute graph per sensor (history kept for every sensor read).
- Enable / Disable Logging, with File → Start Logging… / Stop Logging writing a CSV (Date, Time, every monitored and
  logged sensor in window order), one row per poll.
- Choices are saved in `~/.config/openhw/sensors.json`.

**Window**
- Column resizing: drag a header edge (double-click for the default); long names trim with "…".
- The window's size, position and maximized state are remembered.

**Other**
- `install-helper.sh` asks for a reboot only on a first install.
- The version stamp shows the commit hash even on a tagged commit.

## 0.2.0 — 2026-10-23

**Controls (through openhw-helper)**
- Power modes: switch power-profiles-daemon's profile from the footer, Power menu and tray; when boost comes back
  on, cores return to their OpenHW cap or the hardware maximum (fixes the 4.0 GHz clamp after power-saver).
- CPU frequency caps, all-core or per core (both SMT threads), with readback and rollback; optional re-apply when
  OpenHW starts.
- Power limits: CPU socket PPT (HSMP, 50 W to the SMU's maximum) and each NVIDIA card's limit (NVML), live and at
  boot (hsmp-control's `ppt_mw`; an OpenHW drop-in for `nvidia-power-limit.service`). Shows what limits the CPU now.
- Helper v5: `cpufreqmax`, `cpufreqrestore`, `gpupower`, `cpuppt`, `cpupptboot`, `gpupowerboot`.

**Sensors**
- HSMP engine: FCLK, MCLK, core clock limit, active frequency limit and its 8 limit sources, C0 residency, DRAM
  bandwidth, PROCHOT.
- Nuvoton temperatures and fans named like HWiNFO (matched by behaviour); open pins hidden.
- in6 confirmed as CPU Core1 (Vcore1) by a per-CCD load sweep.
- Every sensor's tooltip explains what the reading means.

**Window and tray**
- Menu bar (File, View, Power, Help) with shortcuts; footer icon shortcuts; About window.
- HWiNFO's sensor right-click menu (Add/Remove from tray working, the rest marked "To be implemented").
- Settings: poll interval, start at login (optionally tray-only), single instance, tray icons with a searchable
  sensor list (no duplicates), order and colors, saved custom colors by hex code or color picker.
- Tray icons: up to 8, in Settings' order, bigger digits; each icon's menu shows its sensor's reading, min/max/avg
  and explanation, plus Move left/right, Remove and Color. Menus no longer close by themselves.

## 0.1.0 — 2026-10-22

- First release: HWiNFO-style Sensors Status window (device groups, Current/Min/Max/Average, collapsible groups,
  changed-value highlight, gray stale rows), one sensor engine per suite (hwmon, NVIDIA, NVMe, EDAC, system,
  network), CPU-temperature tray icon, openhw-helper for NVMe SMART, Super I/O voltages named and scaled for the
  TRX50-SAGE, user install script and app-grid entry.
