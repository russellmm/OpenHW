# OpenHW roadmap

Planned work, roughly in priority order within each section. Writes are validated `openhw-helper` commands,
keeping the GUI unprivileged. Behaviour is derived from the hardware's own interfaces (ranges, readback) — not
copied from openfan-linux, which OpenHW never references.

## Fan control — next (owner, 2026-11-14)

The machine is now the ASUS Pro WS WRX90E-SAGE SE (NCT6798 Super I/O, AST2600 BMC, ASUS EC) with a TR PRO 9975WX,
1× RTX 5060 Ti and 2× RTX PRO 6000. Fan readings come from three places: the Super I/O (`fan1–7` = CHA_FAN1, CPU_FAN,
CHA_FAN2, CHA_FAN3, CHA_FAN4, CHA_FAN5, W_PUMP+), the ASUS EC (CPU_OPT, USB4, M.2, VRM heatsink fans) and the BMC
(all headers, read-only so far). Same rules as every other write: fixed helper commands, ranges from the hardware,
readback, menu entry first.

0. [x] **Decided (owner, 2026-11-14): OpenHW takes over the fans; the aim is to run only OpenHW**, retiring
   OpenFan. Until it's gone, the two must never write the same fans: OpenHW's fan control stays off while OpenFan's
   helper (`openfan-helper.service`) is running, and the Fan Control page says so and offers to stop it (optional
   knowledge only — OpenHW never needs OpenFan). Everything OpenFan does for fans (Super I/O headers, GPU fans, BMC
   fans) has to exist in OpenHW before OpenFan is uninstalled; built from the hardware's interfaces, not OpenFan's code.
1. [x] **Find out who drives each header** — survey done 2026-11-14:
   - **NCT6798**: all seven `pwmN` in automatic mode 5 (SmartFan IV), every curve on **temp8 "PECI Agent 0
     Calibration", which is dead on this board (flat 11 °C)** — so the chip's curves sit at their lowest point: duty
     51/255 (20 %) on pwm1–6, CHA_FAN4's curve 153, pump pwm7 fixed at 255.
   - **Chassis fans are driven by something else**: under 60 s of all-core load (280 W) the NCT6798's duty stayed 51
     while CHA_FAN1 went 2106 → 3284 RPM, CHA_FAN2 722 → 1195, CHA_FAN3 612 → 1154, CHA_FAN5 1521 → 2323, and back at idle —
     almost certainly the BMC's own fan control.
   - **CPU_FAN stayed ~520 RPM and W_PUMP+ ~3900 RPM** through the load. **Guarded write test (owner-approved,
     2026-11-14, `tools/fan-probe/cpu-fan-test.sh`)**: with OpenFan's helper stopped, pwm2 set to 60 % then 100 % →
     CPU_FAN stayed 506–519 RPM; restored to mode 5 and OpenFan restarted. **The NCT6798's PWM outputs drive none of the
     visible headers — it only measures them. All fan control on this board is the BMC's** (CPU_FAN's flat ~510 RPM
     at 280 W is the BMC's choice for that header).
   - **ASUS EC / WMI**: readings only, no PWM controls.
   - **BMC** = the ASUS IPMI Expansion Card (AST2600 at PCI d6:00.0, AMI MegaRAC firmware). Its web UI's Fan Control
     has Generic / Full speed / Customized (duty per temperature, chosen temperature source; no temperature → CPU
     temperature → else 60 %). The ASUS manual documents **no** IPMI command for fan mode or duty (only `raw 0x30`
     FRU unlock 0x17, PSU address 0x96, hardware version 0x98). Routes to control it: Redfish over the BMC's USB host
     NIC (needs the owner's BMC credentials) or OEM IPMI commands verified with ASUS — not guessed.
   - **So fan control here = BMC + NVML.** The BMC is onboard (AST2600, AMI MegaRAC) and runs **Redfish over the
     BIOS's Redfish Host Interface** (BIOS: Advanced → Redfish Host Interface Settings, on by default): the PC's USB
     NIC `enx36dbecc2f9a6` (169.254.x) reaches it at **https://169.254.0.17**, inside the machine only. Reads work
     **without a login** (service root, Chassis/Self, Thermal, Managers/Self). `/redfish/v1/Chassis/Self/Thermal` →
     `Oem` holds the fan control: `"Mode": "Manual"` and `"PWM"`: 8 channels (`Index` 0–7), each `Source` (1 here,
     presumably CPU) and five points `Temperature1–5` / `FanDuty1–5` (%). Today: ch0 20 % to 65 °C then 100 % at 70 °C
     (CPU_FAN — why it sat at ~510 RPM through 65 °C), ch1/2/3/5 20→40→70→100 % (chassis), ch4 60→100 % (CHA_FAN4),
     ch6 100 % (pump), ch7 unused — the same curves the BIOS Q-Fan writes into the NCT6798. Channel → header mapping
     inferred, to verify. Writes (PATCH of that `Oem` block) presumably need a BMC account: not tried.
   - **BMC account done (owner, 2026-11-16)**: user `openhw` (Administrator; created in the BMC web UI — the BIOS
     page didn't offer it), password in the owner's `/mnt/SOLIDIGM/Claude/.secrets/BMC_openhw` (user on line 1,
     password on line 2). **Reads prove nothing here**: over the host interface every GET succeeds without a login,
     even with bogus credentials (so a first "200" test was meaningless). The real test is a session login (POST
     SessionService/Sessions: 201 accepted, 401 refused — a bogus login gets 401): `tools/bmc-probe/login-test.sh
     <file>`, run by the owner on 2026-11-16: **`openhw` accepted (201)**, session closed (204). The web console refuses `openhw` (an AMI web-access setting; maybe not needed). Account list is readable without a login: `/redfish/v1/AccountService/Accounts` (5 = openhw).
     OpenHW's helper reads the login from a root-only copy (to decide: `/etc/openhw/bmc-login` 0600, installed from
     the owner's file) — never in the user's config. Claude never signs in to the BMC itself; authenticated checks are
     run by the owner. The helper's sandbox
     gets network access to 169.254.0.17 only (`IPAddressAllow=169.254.0.17`, `IPAddressDeny=any`, AF_INET) and pins
     the BMC's self-signed certificate on first use. Then: read the mode and curves (no login), save the originals,
     verify the channel → header map one channel at a time, write curves/mode with readback, restore on request.
     Check whether the BIOS re-pushes Q-Fan curves to the BMC at boot (if so OpenHW re-applies at start).
2. [x] **Stage 1 — the fan driver in openhw-helper** (0.23.0, owner 2026-11-16: "build stage 1 in openhw-helper";
   OpenFan's code may be reused). `AsusBmcFans` = OpenFan 0.4.6's `AsusBmcController` ported onto OpenHW's IPMI
   transport (identity: board, ASUS 2623 / 0x1193, firmware 2.01, aux 22 00 00 00, 8 zones in order ff 00…07, each
   table's name and curve temperatures; `30 0f N` read, `30 0e 04 N` + 10 bytes write + readback; 0 % refused by the
   firmware). `FanDriver`: client tokens, one owner per zone, 15 s leases, journal `/var/lib/openhw/fan-restore.json`
   (machine identity = DMI product UUID), restore on release / lease loss / helper stop / next start after a crash,
   refuses takeovers while OpenFan's `/var/lib/openfan/bmc-restore.json` exists. **Flash limits**: faster at once;
   slower after 30 s since the last write and ≥ 3 % lower (else "pending"); writes counted per zone. Protocol 13:
   `fans [token]`, `fanset`, `fanrelease`, `fanbeat`. App: Power → Fan Control… (status), `openhw --fan-test`.
   Tests on OpenFan's captured firmware replies; read check on the real BMC passed (2026-11-16); bmc:1 (CHA_FAN1) write test passed (1,440 → 3,480 RPM at 70 %,
   curve restored identical); bmc:6 (W_PUMP+) passed (3,480 → 3,960 RPM at 100 %). Each test = 2 BMC writes.
   bmc:0 CPU_FAN, bmc:2/3/5 CHA_FAN2/3/5 passed at 70 % — **all six connected zones verified**, mapping confirmed
   (bmc:4 has no fan). **Flash check done 2026-11-16**: CHA_FAN1
   held at a flat 70 % through `ipmitool mc reset warm` (owner's sudo): the BMC was gone 18:11:53–18:12:23 and came
   back still reporting the 70 % curve — **curve writes are stored persistently (almost certainly flash)**, so every
   write counts; the driver's 30 s / 3 % limits stay, and stage 2 should keep writes few (and maybe show a daily
   count). The helper's lease and journal rode out the BMC restart; release restored the original curve.
   **Cold reset too (2026-11-16 18:26, `ipmitool mc reset cold`)**: BMC offline 18:26:29–18:27:21, CHA_FAN1 still flat
   70 % afterwards → **confirmed persistent (flash)**. BMC sensor readings took ~4–5 min to return (to do: re-check all
   sensors promptly after an outage instead of once a minute).
3. [x] **NVIDIA fans** through NVML (0.26.0): `GpuFanDriver` in the helper (protocol 15), leases, automatic again on
   release / lease loss / helper stop / lend; refused while OpenFan's helper runs. Cards on the one Fan Control screen.
3a. [x] **Flash-free alternatives studied (owner, 2026-11-16: "can we bypass the flash writes?")**. No: the headers are
   wired to the BMC's PWM outputs; replacing the BMC firmware (OpenBMC) or writing its registers over ASPEED's P2A
   PCIe bridge are ruled out. The BIOS has no "Onboard Fan Control Source" switch on this board (E26328 mentions it for
   add-in IPMI cards), so the Super I/O can't take the fans over. **ASUS E26328 (V3, July 2025) §3.7.17**: fan mode
   `30 0e` / `30 0f` (0x00 Generic, 0x03 Full speed, 0x04 Manual), config `30 13` (01 count, 03 order, 00 N table:
   32-byte name, PWM source, TCC max, current + generic curve; 02 N set), **temperature source `30 93`** (get: no data
   → one byte per PWM1–23; set: 23 bytes). Source codes aren't listed; the BIOS Q-Fan offers CPU, CPU Package,
   MotherBoard, VRM_W, VRM_E, Chipset, Chipset 2, T_Sensor, up to 3 per fan (Redfish shows `Source` 1 on every zone).
4. [x] **Stage 2 — hybrid (owner, 2026-11-16)** — part 2 built in 0.25.0 (see step 5). **Part 1 built (0.24.0)**: BMC-run curves — Fan Control → Edit…
   (sources via Redfish PATCH of Thermal Oem.PWM with the stored `openhw` login; curve via IPMI 30 0e; original kept in
   fan-original.json; floors CPU 20 %, pump 50 %); helper protocol 14; sandbox opened to 169.254.0.0/16 only. **To
   verify on the real BMC**: the Redfish PATCH format (whole PWM list with If-Match, falls back to one entry) — first
   real source change by the owner. **Part 2 (0.25.0)**: OpenHW-driven curves for GPU temperatures and the OpenFan
   profile import. Original plan: fans whose curve follows a temperature the BMC knows (CPU, CPU
   Package, VRM, MotherBoard, Chipset, T_Sensor) get a **BMC curve written once** — OpenHW sets the zone's source and
   5-point curve, the BMC runs it from memory; writes happen only when the user changes the curve. Fans that follow
   anything else (DIMM, GPU, NVMe temps; mixes) get **OpenHW-driven duties** under a write budget (coarse steps, wide
   hysteresis, slow-downs held, a daily write count). First: decode `30 93` read-only and map the source codes (BIOS
   Q-Fan change + read back, or Redfish `Source`), confirm BMC curves keep running without OpenHW.
   - `30 93` read refused with 0xC7 (request length) on this firmware, as OpenFan found; other lengths not tried (the
     same command sets sources).
   - **BIOS 1405 source codes per fan** (Q-Fan "… Q-Fan Source", BIOS emulator data, hide rules evaluated): 0 CPU,
     1 CPU Package, 2 MotherBoard, 3 VRM_W, 4 VRM_E, 7 T_Sensor, **17 DIMM Group 1 (Max TS), 19 DIMM Group 2 (Max TS)**,
     18 USB4 Thermistor, 99 Multiple Sources (up to three; hidden here: Chipset 5/6, T_Sensor 1/2, DIMM.2 9–12, water
     13–16). So **DIMM temperatures can be BMC-run** — only GPU-following curves need OpenHW-driven duties. Redfish
     shows `Source` 1 on all zones while the BIOS default is 0.
   - **The BIOS hides Chassis Fan(s) Configuration** on this machine (hide rule on internal Setup byte 215 = 1; factory
     default 0; already hidden before "Publish HII Resources" was enabled — owner): the BMC owns the chassis fans;
     their sources are set in the **BMC web console** (Fan Control → Temperature Source), not the BIOS.
   - **BMC temperature sources decoded (2026-11-16)**: the web console offers the BMC's own sensors — CPU Package Temp,
     TR1, LAN Temp, DIMMA1–H1_Temp, PCIE01–07 Temp (checkboxes). The owner set CHA_FAN1 → DIMMA1_Temp and Redfish
     `Source` went 1 → **8**: a **bitmask, bit = BMC sensor number − 1** (CPU Package 1→bit0, TR1 2→bit1, LAN 3→bit2,
     DIMMA1–H1 4–11→bits 3–10, PCIE01–07 12–18→bits 11–17); unused ch7 = 0xFFFFFF. **Confirmed**: DIMMA1 + DIMMB1
     → 24. So DIMM- and PCIe-slot-following fans can be BMC-run; the IPMI source command (30 93) is refused here, so
     sources would be set via Redfish PATCH of Thermal Oem (with the `openhw` login), curves via IPMI 30 0e.
   - The BMC's host-interface NIC **changes name every boot** (enx + a random MAC): find it by vendor/route, not name.
   - "Publish HII Resources" (owner enabled): 1,300 AMD CBS/PBS BIOS attributes readable over Redfish without a login
     (`/redfish/v1/Systems/Self/Bios`); ASUS Q-Fan items aren't among them.
5. [x] **Stage 2 — curves (0.25.0–0.32.0, 2026-11-17)**. Owner: "build the capability to create curves like we can in
   OpenFan and assign them to a given fan … BMC sensors cause no flash writes … other sensors, but get warned", then
   "a single screen like OpenFan has … including the GPUs". Done: OpenFan's curve engine (graph / flat / mix, hysteresis,
   max speed, graph editor), bulk sensors (hottest of several), one OpenFan-style Fan Control screen (BMC + NVIDIA fan
   cards, curve cards with live dots, mix BMC previews/warnings, friendly names, New/Duplicate, drag-and-drop order,
   configurations, tray menu). BMC-sensor curves are fitted and written once; others are OpenHW-driven with warnings and
   per-day counts. Point 4 is free (80 °C accepted on CHA_FAN1). Slot sensors = GPU cores (`tools/gpu-slot-test/`).
   **Left**: disable OpenFan's helper for good (still enabled); a slot sensor while its card is lent; the ~25 s at
   start before BMC-sensor GPU curves have readings.
5. [ ] **Safety tests**: fake sysfs for every write path; a header that doesn't follow its duty is reported, never
   pushed further; pump/CPU minimums can't be lowered from the UI without an explicit override.

## Windows 11 — eventually (owner, 2026-11-16)

The owner wants OpenHW on Windows 11 too, eventually — **scope (owner, 2026-11-16): the HWiNFO replacement (sensor
window, tray, logging, alerts) and fan control only; no GPU Lending** (and power/CPU-frequency features aren't asked
for). Keep platform code behind interfaces now:
- **BMC fan control** carries over: IPMI raw commands through Windows' built-in IPMI driver (WMI `Microsoft_IPMI`
  RequestResponse) instead of `/dev/ipmi0` — same OEM fan commands, no BMC login; Redfish (temperature sources) via the
  BMC's **LAN address (10.0.0.200 on the owner's network**, where the web console answers under Windows) with the
  `openhw` login, or the host-interface link if its Windows driver is installed. Keep `IIpmiTransport` and a Redfish
  client interface as the seams; fan curves/sources logic stays platform-free.
- Needed on Windows: sensor engines (hwmon/libsensors → WMI/PDH and LibreHardwareMonitor-style access; NVML and the
  BMC/IPMI engines port directly), and the root helper → a Windows service with a named pipe (BMC fan driver, NVMe
  SMART, GPU reads). Not needed there: GPU Lending (eBPF card filter, libvirt), cpufreq, HSMP power limits. The
  Avalonia window already runs on Windows.

## After the board swap (2026-11-14, 0.21.0–0.22.0)

- [x] **BMC sensors** over IPMI (`/dev/ipmi0`, helper `bmc`, read-only commands): rails, DIMM/LAN/PCIe-slot
      temperatures, all fan headers (0.21.0).
- [x] **Hardware Check** (File → Hardware Check…): what changed, saved settings pointing at missing sensors with
      suggested moves, what isn't read; opens itself at start when needed (0.21.0).
- [x] **GPUs by identity, not PCI slot**: GPU Lending follows its card by GPU UUID; boot power limits name the card
      (`nvidia-smi -i GPU-…`) (0.21.0).
- [x] **WRX90E-SAGE SE board profile** for the NCT6798, named after the BMC's sensors and matched against them
      (0.22.0, `tools/bmc-probe/match.py`, docs/board-profiles.md).
- [x] GPU Lending survives libvirt being down; a missing card can be replaced while its filter is on (0.22.0).
- [ ] Confirm the WRX90E's unconfirmed inputs: in11 vs in14 (+VDD_11_S3) with a BIOS memory-voltage change; in8
      VBAT and in9 VTT; fan5 (CHA_FAN4) with a fan on that header. A HWiNFO capture would name in12/in14.
- [ ] **The other 4 DIMMs' SPD sideband** (voltages, PMIC power): Linux bound SPD hubs on only two of the memory
      buses. Needs a root scan of the others, which the BMC shares — careful; the BMC already gives all 8 temperatures.
- [ ] **WRX90E board VR**: find out whether this board has a PMBus VR worth mapping (the TRX50's XDPE19283B entry
      doesn't apply).
- [x] BMC: discrete sensors (CPU_ECC, Memory_Train_ERR, PSU presence…) and the BMC's event log (SEL), read-only
      (0.34.0, File → BMC Status & Event Log…).
- [x] BMC fan alarm thresholds (lower non-critical / critical / non-recoverable), per fan, with Restore original
      (0.35.0, Fan Control → ⋮ → Alarm thresholds…).
- [x] BIOS Setup options via Redfish (Publish HII Resources): File → BIOS Settings…, changes in Hardware Check;
      warning when the BMC's host interface needs no login (0.36.0).
- [x] **BIOS Setup in OpenHW** (0.37.0): the standalone BIOS emulator ported to C#, with this machine's values from
      its UEFI variables; changes are a plan (F10), never written.
- [ ] BIOS Setup: write the plan to the firmware — only behind a strong guard (owner: "later, carefully"): back up every
      variable first, write one variable at a time, read back, refuse on size/version mismatch.
- [ ] BIOS Setup: EZ Mode (F7), driver-built pages (SMART, SPD, NVMe lists), more plain-English explanations.
- [ ] DIMM details from Redfish (part number, rated vs running speed, ranks) — BIOS labels slots only "DIMM 0–7".

## GPU lending to a VM (replaces RTXinVM) — agreed with the owner 2026-10-25

Goal: one root helper, no `rtxgpu` launcher. When the VM isn't using the card, every Linux program can use both RTX
6000s (vLLM, Unsloth Studio, llama.cpp, nvidia-smi…); the card is kept away only from programs the owner lists.

1. [x] **The window reads all GPUs through openhw-helper** (0.12.0; RTXinVM no longer launches OpenHW) (a system service, never blocked by a device filter).
   The app then never opens a card, runs as plain `/usr/bin/openhw --tray`, and keeps running through a handoff
   (the lent card's rows go stale, then return). Saves the app the NVIDIA driver's memory.
2. [x] **Card filter in openhw-helper** (0.13.0; built and self-tested, **off** until the switch-over in step 4/5 —
   RTXinVM's guard keeps doing the job until then): blocks *new* opens of the chosen
   card's nodes (`/dev/nvidiaN`, `/dev/dri/cardN`, `renderD*`) for listed programs. Unlike RTXinVM's allow-list, the
   program refuses any cgroup without a verdict and the helper marks every *leaf unit* not on the list allowed the
   moment it appears (inotify on cgroupfs + 2 s rescan) — so hiding is airtight and everything else still sees the
   card. Helper protocol 9: `cardfilter [on|off]`, `cardselect`, `cardhide|cardshow <pattern> <now|boot|both> [name]`,
   `cardforget`. Root self-test: `openhw-helper --selftest-card-filter <pci>` (throwaway cgroup beside user.slice).
   Still to do at switch-over: start the helper before the login screen (unit ordering + `Type=notify`). Granularity = how programs are
   launched: an app from the grid/dock (`app-<id>-*.scope`), the GNOME desktop (`org.gnome.Shell@*.service`, which
   includes Xwayland), a terminal window's scope. A program that already has the card keeps it until it exits.
3. [x] **Setup page** (0.14.0: Power → GPU Lending…, also in the tray menu) (from the gear's settings menu / Power menu): the GPU to lend and the VM; **what's using it
   now** (live, every process with its nodes open, from the helper); per entry **Hide now** (until reboot) and
   **Hide from boot** (applied by the helper before the login screen). GNOME desktop: Hide from boot ticked by
   default; unticked → GNOME sees the card from the next boot (and the VM can't start until a logout frees it).
   Unhide is immediate for programs started afterwards. Shows "still holding the card — restart it" where needed.
4. [x] **Lend to Windows / Return** (0.15.0; `CardLending` in the helper, `LendCoordinator` in the app) (owner's rule: the VM does not start while anything still has the card open; the
   page names what). Optional: pause OpenFan if it's installed (independence rule: optional, no dependency). The
   libvirt hook keeps doing the handoff; return refreshes the helper's GPU list.
5. [x] **Retire RTXinVM** — switched over 2026-10-26 (0.16.1: helper before the login screen with `Type=notify`
      and a wait for the card's nodes; "Close and reopen when lending" for apps such as OpenFan's window; filter on;
      `rtxinvm uninstall` run). Verified 2026-10-26: after a reboot the desktop never opens the card and a terminal
      lists three GPUs; a full Lend → Return from the page with 0.16.3 (10:17 → 10:20) closed and reopened OpenFan,
      Chrome and the weather widget, and the hook restored 300 W + persistence.
6. [x] **Install / Uninstall from the page** (0.17.0, owner's request 2026-10-26): uninstalled, nothing of lending
      is in the kernel or at boot (filter, boot-order drop-in, GNOME udev rule, libvirt hook put away); install puts
      it all back with the owner's choices. Verified 2026-10-26: uninstall → reboot (plain boot: no drop-in, rule, hook or
      filter; GNOME used E1) → install → reboot (helper ordered before gdm, filter attached, desktop off E1) → Lend → Return.

## Write capabilities (via openhw-helper)

Each write is a fixed helper command with strict argument validation, a readback check, and a clear error
when firmware clamps or rejects the value. Nothing a client sends may become a path or raw register.

- [x] **GPU power limit** — NVML, within each card's constraints, read back (helper `gpupower`). Live until reboot.
- [x] **CPU socket power limit (PPT)** — `amd_hsmp_hwmon` `power1_cap`, 50 W to the SMU's reported max, read back
      (helper `cpuppt`); shows the active frequency limit and its source. Live until reboot.
- [x] **Keep power limits after reboot** — in the existing boot services' config: hsmp-control's `ppt_mw` (100–300 W)
      and an OpenHW drop-in for `nvidia-power-limit.service` (helper `cpupptboot` / `gpupowerboot`).
- [x] **Settings window** — poll interval, start at login (`--tray`), tray icons (sensor + colour, no limit since 0.20.2),
      single instance.
- [x] **Power modes** — switch power-profiles-daemon's profile from the footer and tray, and lift the base-clock
      cap left after power-saver (helper `cpufreqrestore`). See README "Power modes and CPU frequency caps".
- [x] **CPU maximum frequency** — all-core and per-core `scaling_max_freq` caps (helper `cpufreqmax`), with
      readback and rollback; caps set here survive power-saver. Live until reboot.
- [x] **Keep CPU caps after reboot** — optional; OpenHW re-applies saved caps when it starts (with Start at login).
- [x] **Where the 4.0 GHz clamp comes from**: not the boot sequence (booted in power-saver, switched to Performance
      with both apps closed → full 5.49 GHz). It's a 4.0 GHz value stored while boost is off, e.g. OpenFan's
      Hardware maximum pressed in power-saver.
- [ ] **Fan control** — next; see [Fan control](#fan-control--next-owner-2026-11-14) at the top.

## Root-only readings (via openhw-helper)

- [x] **RAPL** package/core energy → power, through the helper (0.5.0).
- [x] **Effective clocks** from the kernel's APERF/MPERF average (`cpuinfo_avg_freq`, no root) (0.5.0).
- [ ] **Per-core power** (AMD core-energy MSR) — needs CAP_SYS_RAWIO in the helper; owner to decide.

## More sensors (HWiNFO parity)

Compared against the HWiNFO captures in `reference/`. Method for board mappings: [docs/board-profiles.md](docs/board-profiles.md).
Items marked *TRX50* apply to the Pro WS TRX50-SAGE WIFI A, which is no longer in the owner's machine (replaced by
the WRX90E-SAGE SE in November 2026); its profile stays for anyone with that board.

- [x] **Super I/O voltages named and scaled** on the TRX50-SAGE (in0–in16; in17 hidden) — `BoardProfiles`.
- [x] **in6 CPU Core1 confirmed** (×1.11) by a per-CCD load sweep — see docs/board-profiles.md.
- [ ] *TRX50* — **Confirm in13 CPU VSOC** (×1.11) with a BIOS SoC-voltage change (needs that board). in15 VIN9 follows temperature, not load.
- [x] **Super I/O temperatures and fans named** on this board, by behaviour (docs/board-profiles.md). Still inferred:
      CPU (PECI/TSI calibration) and CPU (Weighted Value) (Virtual_TEMP) — confirm with a longer cross-OS run.
- [x] Show the chip as HWiNFO names it (Nuvoton **NCT6701D**) (0.5.0).
- [x] **WRX90E-SAGE SE** (NCT6798) — from its BMC (0.22.0); see "After the board swap".
- [ ] Profiles for more boards (same method; a HWiNFO capture + raw Linux values, or a BMC to compare against).
- [ ] **CPU "Enhanced" telemetry** (HWiNFO reads AMD SMU/SVI3, not the Super I/O):
  - AMD HSMP metrics: the kernel already exposes many without root on the HSMP platform device
    (`/sys/devices/platform/AMDI0097:00/`): `fclk_input`, `mclk_input`, `cclk_freq_limit_input`, `clk_fmax`/`clk_fmin`,
    `c0_residency_input`, `ddr_max_bw`/`ddr_utilised_bw_input`, `prochot_status`, `pwr_current_active_freq_limit`
    and its `_source` — **done** (`HsmpEngine`, CPU Enhanced group). The full table (`metrics_bin`) is root-only.
  - SVI3 rail telemetry (VDDCR_VDD / VDDCR_SOC voltage, current, power, TDC/EDC, VRM temperatures): **deferred**
    (owner, 2026-10-25). Checked: `metrics_bin` answers "Operation not supported" here — the kernel only decodes
    the HSMP metric table for protocol 6 (MI300); this CPU is protocol 7. The SMU PM table needs the out-of-tree
    `ryzen_smu` module; research its Threadripper 9000 support before committing to it.
  - Per-core and L3 temperatures, IOD hotspot/average, per-core power, L3 clocks.
- [x] **VR controllers**: Infineon XDPE19283B (loop temperatures, VIN, VOUT, IIN, IOUT, PIN, POUT, efficiency) over
      PMBus through the helper's `vr`, PMBus reads only (0.10.0). No kernel driver covers the 19283B; its place
      (PIIX4 port 0, 0x20) is in `BoardVrs`, confirmed by IC_DEVICE_ID on every read.
- [x] **DDR5 PMIC rails** per DIMM (VDD/VDDQ/VPP, 1.8V/1.0V VOUT, VIN, total power, PMIC alarms) and the TS0/TS1
      sensors, through the helper's `ddr5` (0.10.0). Reads, plus the PMIC's ADC select (R30) — owner-approved, the
      one register HWiNFO also sets — only on a PMIC whose vendor id matches the SPD's, restored after each sweep.
- [ ] **ASUS EC VRM data**: VRM Vcore0/1 VOUT, IOUT, POUT (HWiNFO shows them on the TRX50); Linux's `asus_ec_sensors` exposes
      only temperatures and fans — needs the EC register map for this model, and a way to call the
      board's ACPI BREC method for registers the driver doesn't list (acpi_call DKMS or a driver patch). Deferred
      (owner, 2026-10-25).
- [x] **GPU extras** where NVML has them: 1 s average power, PCIe error counters (0.5.0). Hotspot / memory-junction
      temperatures aren't in NVML's public interface (memory temperature reports "not supported" on these cards).
- [x] **Thermal Grizzly WireView Pro II** (0.38.0): its own group (per-wire V/A/W, totals, current imbalance,
      connector temperatures, PSU sense-pin limit, faults), keyed by the device's USB serial — not tied to a GPU
      (owner). Read-only through the helper (protocol 19); see docs/power-devices.md.
- [x] **UPS through NUT** (0.38.0; whole numbers 0.38.2): input/output voltage, load, power, battery charge/voltage/
      runtime, on-battery / low / replace / overloaded flags — upsd on localhost, read-only, no root.
- [ ] Power devices: alert presets for WireView wire current, connector temperature and faults, and for "UPS on
      battery" (the alert system can already do these by hand).

## Resource use

Measured 2026-10-22 on the TRX50 workstation (before the board swap) (per 2 s poll, background thread): NVIDIA ~156 ms wall / ~37 ms CPU,
hwmon ~74 ms wall / ~4 ms CPU, everything else under 2 ms combined. Two reads dominate:
NVML PCIe throughput (~42 ms per GPU — NVML samples each counter for 20 ms) and NVMe hwmon temperatures (5–19 ms
per drive — each read is an admin command to the drive, which can also wake it from a low-power state).

- [x] **Collapsed groups stop reading.** Rows not on screen (collapsed groups and per-core lists, plus limits and
      alarm bits the window never shows) are not read; their Min/Max/Average freeze and show dimmed until
      expanded. Tray sensors stay live. Collapse state persists in `~/.config/openhw/view.json`.
      Measured per poll: all open 71 ms wall / 32 ms CPU; GPU + drive groups collapsed 3.7 ms / 2 ms.
- [x] PCIe throughput sampled every 10 s instead of every poll.
- [x] Pause reads while the window is hidden, optionally (Settings; off by default — HWiNFO keeps statistics running) (0.6.0).

### Memory

Measured 2026-10-22 (b68a473, everything collapsed, window open): ~0.7% of one core, ~310 MB resident
(~190 MB private). Breakdown: ~130 MB .NET runtime (GC heap, JIT code, 35 MB W^X double-mapping), ~70 MB
NVIDIA OpenGL driver mappings (Avalonia renders through GL), ~98 MB shared libraries.

- [x] **Runtime settings** (0.6.0; measured 2026-10-24, window open, 90 s median): TieredPGO off, GC
      conserve-memory 7 and a 4 MB gen0 budget (environment only — the launcher re-executes itself to set it):
      322 → 293 MB resident, 219 → 187 MB private, no extra CPU. Tried and dropped: W^X off (the 34 MB
      double-mapping just moves into anonymous memory), non-concurrent GC (no gain), heap hard limit and
      `MALLOC_ARENA_MAX=2` (no gain); `System.GC.Gen0Size` in runtimeconfig is ignored.
- [x] Optional software rendering (Settings, next start) (0.6.0): 293 → 228 MB resident, +0.8% of a core while a
      window is visible.
- [ ] **Native AOT later**: compile ahead of time to remove the JIT and most runtime overhead (large memory and
      startup win). Needs trim-safe code first: replace reflection `Binding("Name")` in the row templates with
      compiled bindings, check Tmds.DBus and System.Text.Json (source-generated context for `ViewState`) and
      the P/Invoke declarations for AOT warnings.

## Window and tray

- [x] Choose which sensors get tray icons, with per-icon colours.
- [x] Tray icon menus: Move left/right, Remove, Color submenu; saved custom colors (hex entry or color picker); panel order follows
      Settings; Settings sensor picker is searchable.
- [x] Sensor row right-click menu in HWiNFO's shape (Add/Remove from tray work; others grayed "To be implemented").
- [x] Row menu: Show Graph, Disable/Enable monitoring, Rename, Hide/Unhide, Enable/Disable Logging (0.3.0).
- [x] Alerts: per-sensor rules, desktop notifications, File → Alerts… (0.5.0).
- [x] Row menu: Layout settings (View → Layout Settings…: order, show/hide, monitoring) and Customize values
      (label, unit, multiply, offset, decimals) (0.6.0).
- [ ] OSD / RTSS / LG LCD are Windows-specific in HWiNFO — decide whether they have a Linux meaning.
- [x] Tray icon menus start with the sensor's reading, min/max/average and explanation; sensor tooltips explain
      every reading.
- [ ] Hover info for tray icons on GNOME (the AppIndicators extension shows no tooltips): a left-click info popup,
      or an OpenHW GNOME Shell extension with real hover tooltips. Deferred by the owner.
- [x] **HWiNFO settings menu, scaffolding** (0.18.0): footer gear → Sensor Settings (General, Layout, Customize,
      Tray Icon, Alerts) → Main Settings (General / User Interface, Safety, SMBus / I2C, Driver Management), laid out
      from `reference/settings_menu/`; tabs without a capture grayed. Nothing saved yet.
- [x] **Connect what OpenHW already has** (0.19.0): Polling Period, Layout, Customize, Tray Icon and Alerts items,
      Auto Start / Minimize on Startup, helper status — two-way with the older windows, which stay for now (owner).
- [x] **Small additions** (0.20.0): Row Shading, Highlight changing values, Show ToolTips, Show columns, Always On
      Top, Average last N samples, Change Item Color, Validate Window Positions, Enable NVML / SMBus / S.M.A.R.T.
- [ ] **Connect the rest of the settings menu** item by item (owner-led): each control to the matching OpenHW option, or grayed
      "To be implemented" where there is none yet; decide which Windows-only items (PresentMon, ADL, Intel ME, CSMI
      SAS, NVAPI, Driver Management) get a Linux meaning, a new name, or are grayed. Then decide what happens to
      File → Settings, Tray Icons… and Layout Settings… (they overlap with these tabs).
- [ ] More settings modelled on `reference/` (units, shown columns, row shading, highlight) — most are on the
      General tab now, waiting to be connected.
- [x] Column resizing; remember window size/position, expanded groups and hidden sensors.
- [x] CSV logging.
- [x] Single instance (a second launch shows the running window).
- [x] Optional start at login (autostart entry), optionally in the tray only.
