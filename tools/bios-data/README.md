# BIOS data tools

Decode the ASUS BIOS capsule into the menu database the BIOS module (File → BIOS…) uses. Python 3 standard library
only (plus `pdftotext` from poppler-utils for the manual). Ported unchanged from the standalone BIOS emulator.

| Tool | Does |
|---|---|
| `uefi_extract.py` | capsule → firmware volumes → HII strings, IFR forms, AMI NVAR factory defaults |
| `build_bios_data.py <file.CAP>` | writes `src/OpenHW.App/Bios/Data/bios-<version>.json` (the newest one is used) |
| `parse_manual.py <manual.pdf>` | writes `src/OpenHW.App/Bios/Data/manual_notes.json` |

For a new BIOS: put the `.CAP` here (it's git-ignored, ~32 MB), run `python3 tools/bios-data/build_bios_data.py
Pro-WS-WRX90E-SAGE-SE-ASUS-XXXX.CAP`, then `dotnet test` (the BIOS module tests load the newest data file) and check
File → BIOS… shows "read from this machine" for the main variables (a different BIOS version changes their sizes).
