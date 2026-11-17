# BIOS Setup (File → BIOS Setup…, 0.37.0)

The ASUS UEFI BIOS Utility (Advanced Mode) screen for the Pro WS WRX90E-SAGE SE, inside OpenHW, showing **this
machine's current settings** without a reboot. Ported from the standalone BIOS emulator (`/mnt/SOLIDIGM/Claude/BIOS`,
Python 3 + PySide6, 2026-11-10), which the owner retired in favour of this module (2026-11-17).

## Owner's decisions (2026-11-17)

- **Port to C#/Avalonia**, not bundle the Python app (one app, one .deb, no 650 MB Qt environment, comes along to the
  Windows port).
- **Keep the Redfish list too** (File → BIOS Settings from the BMC…).
- **Saving: read-only now; a guarded write-back "later, carefully"** — see "Write-back" below.
- **Retire the Python project, keep its tools**: the capsule decoder lives in `tools/bios-data/`.

## Data flow

```
 .CAP (tools/bios-data, git-ignored) ──build_bios_data.py──► src/OpenHW.App/Bios/Data/bios-1405.json   menus, rules,
 manual PDF ───────────────────────────parse_manual.py────► manual_notes.json                            defaults
 hand-written ───────────────────────────────────────────► explanations.json                            plain English
 shipped profile + BiosPlatform.Probe ───────────────────► platform (CPU, cores, CCDs, boot entries, MACs, text)
 /sys/firmware/efi/efivars ──LinuxFirmwareVariables──────► BiosModel "Saved" (this machine)
                                                            BiosModel "Work" = Saved + edits ──F10──► plan.json
```

- **`bios-<version>.json`**: formsets → forms → items (kind, prompt, help, question id, varstore/offset/size,
  show/hide/lock rules as RPN, options with their own rules, numeric limits, links) + varstores (name, GUID, size,
  factory default bytes from AMI's NVAR StdDefaults) + the 9000WX (SHP) / 7000WX (STP) CBS/Overclocking variants.
  The decoder (`uefi_extract.py`, standard library only) reproduces `bios-1405.json` byte for byte.
- **`BiosModel`** (`src/OpenHW.App/Bios/BiosModel.cs`): values as byte buffers per varstore, read at offsets exactly as
  the firmware stores them. Sources: `Default` (factory bytes), `Saved` (this machine), `Work` (on screen).
  RPN evaluator, `StateOf` (ok / gray / hide), breadth-first page index with breadcrumbs (visible links first),
  internal "twin" ids of ASUS typed fields, platform `hide_items`, `VisibilityNotes`, search, profiles, report.
  **Parity**: equal to the Python engine on all 5,905 reachable items (state, value, path).
- **`BiosHelp`**: explanation (hand-written → manual → BIOS help), choices with meanings, allowed values (value rules →
  "Min: | Max:" in help → manual ranges → menu limits), input checking with step rounding, glossary terms.
- **`BiosWindow` / `BiosDialogs`**: the ASUS look (blue, tabs, breadcrumb, value boxes, amber dot = planned change,
  ★ = favorite, `[hidden]`), details panel, Hardware Monitor (OpenHW's BMC readings), hot keys as the real BIOS.

## Real values

`/sys/firmware/efi/efivars/<Name>-<guid>` is world-readable: 4 attribute bytes, then the variable. A variable counts
as this machine's only when its size equals the BIOS data's (a different BIOS version changes sizes); otherwise the
factory default is used and the variable is listed in "This machine…". On the WRX90E with BIOS 1405, **34 variables**
match, including Setup (533 bytes), VARSTORE_OCMR_SETTINGS_NAME (Ai Tweaker, 13,271), AmdSetupSHP (CBS, 2,438),
AodSetupShp (838), AMD_PBS_SETUP (256), ServerSetup (991) and the hardware-detection variables the emulator had to
guess — **AutoDetectData, SetupAiFeatures, AsusHwmDeviceStatus**. The emulator's guesses ("overlay") now apply only
to variables the firmware doesn't expose (AmiAmdSbSuppressIfSetupData, SystemAccess, DynamicPageCount, IDESecDev,
PcieSataSetupVar …). Network-driver and RAID pages aren't exposed (factory defaults).

Quirk: the running firmware stores the word **"Auto"** in ASUS typed fields whose factory default is empty — the
model treats them as equal (otherwise 46 settings showed as changed).

This machine (2026-11-17): 21 settings off default — Ai Overclock Tuner EXPO, EXPO Profile #1 (DDR5-5200 36-40-40
1.25 V), BCLK1/PCIE 100.0000, Memory Frequency 5200, DRAM VDD/VDDQ and Memory VDD/VDDQ 1.25000, PMIC "Sync All
PMICs", Tcl 36 / Trcd 40 / Trp 40 / Tras 80, Publish HII Resources on, SATA off, Restore AC Power Loss = Power On,
Power On By PCI-E on, PCIEX16(G5)_7 in RAID mode, Armoury Crate download off.

## The plan (instead of an emulated NVRAM)

Edits on screen are a **plan**. F10 saves it to `~/.config/openhw/bios/plan.json` — per item id, with favorites,
boot order and password flags — and appends the changes to `history.jsonl`. On open, Saved = the machine's
variables and the plan is applied on top; a planned value the machine already has is no longer a change, so it
drops off after the owner enters it in the real BIOS and reboots. Profiles (ASUS User Profile 1–8) keep whole Work
buffers under `profiles/`. "Changed from defaults" lists Saved vs factory and exports a text report.

## Write-back (not built; "later, carefully")

`IFirmwareVariables` has only `Read` and `BootEntries`. A writer would need, at least: the helper (root; efivarfs
files are immutable until `chattr -i`), a full backup of every variable it touches first, refusal on any size or BIOS
version mismatch, one variable at a time with a read-back, writing only the planned bytes (the plan holds item
values, so the writer recomputes the buffer from the *current* variable), the firmware's "Auto" convention for typed
fields, and the owner's explicit go-ahead per write. A bad Setup variable can stop the board booting until CMOS is
cleared.

## Platform probe

`BiosPlatform.Probe` starts from `Bios/Data/platform/wrx90e-9975wx.json` and fills in: CPU brand (`/proc/cpuinfo`),
cores (unique core ids), CCDs (unique L3 `shared_cpu_list`), base clock (`amd_pstate_nominal_freq`), variant (PRO 9xxx
→ SHP, 7xxx → STP), boot entries (BootOrder → Boot#### descriptions), X710 MACs (i40e interfaces). A saved
`~/.config/openhw/bios/platform.json` (written by "This machine…" → Apply) overrides it. Still from the profile: DIMM
population, SPD/EXPO profiles (Kingston datasheet), BMC device text.

## Updating to a new BIOS

Put the `.CAP` in `tools/bios-data/`, `python3 tools/bios-data/build_bios_data.py <file.CAP>` (writes
`bios-<version>.json`; the newest is used), `dotnet test`, then check "This machine…" lists the main variables as read
(size mismatches mean the data and the firmware disagree).

## Tests

`tests/OpenHW.App.Tests/BiosSetupTests.cs` ports the emulator's smoke test: every one of the 371 pages renders,
Advanced → AMD CBS is the SHP variant, SMT plan survives a reopen and drops off when the machine has it, favorites
persist without saving edits, Manual reveals BCLK1 (with a visibility note), profiles, search, help validation,
7000WX switch, boot entries from the (fake) firmware; captures `openhw-bios-*.png`. A fake `IFirmwareVariables`
supplies factory bytes with chosen changes (incl. the "Auto" quirk).

## Not done

EZ Mode (F7); driver-built pages (SMART, SPD, NVMe lists, RAID) show their static items only; only ~135 settings
have hand-written explanations (`explanations.json`); write-back (above).
