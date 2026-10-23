"""Compare a Linux hwmon capture (linux-capture.sh) with the Windows HWiNFO reference, phase by phase.

    python3 tools/loadgen/compare.py /tmp/openhw-stress-XXXX \
        reference/stress-2026-10-23/windows-hwinfo-phase-medians.csv

Prints, for each Nuvoton input, the Linux median × the board-profile scale next to HWiNFO's median for the same
load phase, plus a few CPU/EC readings. Values are in volts / °C / RPM / W.
"""
import csv
import re
import statistics
import sys

# Pin → (HWiNFO reading name, scale), as in BoardProfiles.cs for the Pro WS TRX50-SAGE WIFI A.
NCT = {
    "in0": ("Vcore", 1), "in1": ("+5V", 5), "in2": ("AVSB", 1), "in3": ("3VCC", 1), "in4": ("+12V", 12),
    "in5": ("CPU VDDIO", 1.11), "in6": ("CPU Core1", 1.11), "in7": ("3VSB", 1), "in8": ("VBAT", 1),
    "in9": ("VTT", 1), "in10": ("CHIPSET1 1.0V", 1), "in11": ("VDD_11_S3 / MC", 1.11), "in12": ("VIN2", 1),
    "in13": ("CPU VSOC", 1.11), "in14": ("CHIPSET1 1.8V", 1.11), "in15": ("VIN9", 1), "in16": ("VHIF", 1),
}
# Other pairs worth eyeballing: Linux column → HWiNFO column substring.
OTHER = {
    "k10temp/temp1_input": "Enhanced | CPU (Tctl/Tdie)",
    "amd_hsmp_hwmon/power1_input": "Enhanced | CPU Package Power",
}


def linux_phase_medians(outdir):
    t0 = int(open(f"{outdir}/t0.txt").read())
    phases = [(m[0], int(m[1])) for m in
              re.findall(r"PHASE (\S+)\s+threads=\d+\s+start_unix=(\d+)", open(f"{outdir}/loadgen-phases.txt").read())]
    rows = list(csv.reader(open(f"{outdir}/hwmon.csv")))
    hdr, data = rows[0], rows[1:]
    res = {}
    for i, col in enumerate(hdr[1:], 1):
        meds = []
        for k, (_, start) in enumerate(phases):
            a = start - t0 + 6
            b = (phases[k + 1][1] if k + 1 < len(phases) else start + 30) - t0
            v = [float(r[i]) for r in data if len(r) > i and r[i] and a <= float(r[0]) < b]
            meds.append(statistics.median(v) if v else None)
        res[col] = meds
    return [p for p, _ in phases], res


def windows_medians(path):
    rows = list(csv.reader(open(path, encoding="utf-8")))
    hdr = rows[0]
    phases = [h[:-len("_median")] for h in hdr if h.endswith("_median")]
    return phases, {r[0]: [float(x) if x else None for x in r[1:1 + len(phases)]] for r in rows[1:]}


def unit_scale(col):
    if "/in" in col or "/power" in col:
        return 1e-3 if "/in" in col else 1e-6   # mV → V, µW → W
    if "/temp" in col:
        return 1e-3
    return 1


def main():
    lphases, lin = linux_phase_medians(sys.argv[1])
    wphases, win = windows_medians(sys.argv[2])
    phases = [p for p in lphases if p in wphases]
    wi = [wphases.index(p) for p in phases]
    li = [lphases.index(p) for p in phases]

    def fmt(v):
        return f"{v:8.3f}" if v is not None else "       -"

    print(f"{'reading':34}" + "".join(f"{p:>19}" for p in phases))
    print(f"{'':34}" + "".join(f"{'linux / hwinfo':>19}" for _ in phases))
    rows = []
    for col in lin:
        m = re.match(r"(nct67\w+)/(in\d+)_input", col)
        if m and m[2] in NCT:
            name, scale = NCT[m[2]]
            wkey = next((k for k in win if "NCT6701D) | " + name + " [" in k), None)
            rows.append((f"{m[2]:5} {name}", [v * 1e-3 * scale if v is not None else None for v in lin[col]], wkey))
    for col, wsub in OTHER.items():
        if col in lin:
            wkey = next((k for k in win if wsub in k), None)
            rows.append((col, [v * unit_scale(col) if v is not None else None for v in lin[col]], wkey))
    for label, lvals, wkey in rows:
        wvals = win.get(wkey, [None] * len(wphases))
        print(f"{label[:34]:34}" + "".join(f"{fmt(lvals[a])} /{fmt(wvals[b])}  " for a, b in zip(li, wi)))


main()
