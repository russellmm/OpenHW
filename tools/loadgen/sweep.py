"""Load sweep: pin busy loops (sh while-loops, via taskset) to chosen CPUs and record every Nuvoton input plus
package power, clock and Tctl per step. Needs no root. Results: reference/stress-2026-10-23/linux-sweep-*.csv.

    python3 tools/loadgen/sweep.py out.csv
"""
import glob, os, statistics, subprocess, sys, time

def hw(name):
    for d in glob.glob("/sys/class/hwmon/hwmon*"):
        if open(f"{d}/name").read().strip().startswith(name):
            return d

NCT, HSMP, K10 = hw("nct67"), hw("amd_hsmp"), hw("k10temp")
INS = sorted(glob.glob(f"{NCT}/in*_input"), key=lambda p: int(os.path.basename(p)[2:-6]))

def rd(p):
    return int(open(p).read())

def sample(cpus):
    s = {os.path.basename(p)[:-6]: rd(p) / 1000 for p in INS}
    s["W"] = rd(f"{HSMP}/power1_input") / 1e6
    s["Tctl"] = rd(f"{K10}/temp1_input") / 1000
    f = [rd(f"/sys/devices/system/cpu/cpu{c}/cpufreq/scaling_cur_freq") / 1000 for c in (cpus or [0])]
    s["MHz"] = statistics.mean(f)
    return s

R = lambda a, b: list(range(a, b))
STEPS = [
    ("idle", []),
    ("1t cpu0 (CCD0)", [0]), ("1t cpu8 (CCD1)", [8]), ("1t cpu16 (CCD2)", [16]), ("1t cpu24 (CCD3)", [24]),
    ("8t CCD0", R(0, 8)), ("8t CCD1", R(8, 16)), ("8t CCD2", R(16, 24)), ("8t CCD3", R(24, 32)),
    ("16t CCD0+3", R(0, 8) + R(24, 32)), ("16t CCD1+2", R(8, 24)),
    ("32t all cores", R(0, 32)), ("64t all threads", R(0, 64)),
    ("idle after", []),
]

out = open(sys.argv[1], "w")
hdr = None
for name, cpus in STEPS:
    procs = [subprocess.Popen(["taskset", "-c", str(c), "sh", "-c", "while :; do :; done"]) for c in cpus]
    time.sleep(4)
    rows = []
    for _ in range(16):
        rows.append(sample(cpus))
        time.sleep(0.5)
    for p in procs:
        p.kill()
    for p in procs:
        p.wait()
    med = {k: statistics.median(r[k] for r in rows) for k in rows[0]}
    lo = {k: min(r[k] for r in rows) for k in rows[0]}
    hi = {k: max(r[k] for r in rows) for k in rows[0]}
    if hdr is None:
        hdr = list(med)
        out.write("step," + ",".join(f"{k},{k}_min,{k}_max" for k in hdr) + "\n")
    out.write(name + "," + ",".join(f"{med[k]:.4f},{lo[k]:.4f},{hi[k]:.4f}" for k in hdr) + "\n")
    out.flush()
    print(f"{name:18} W={med['W']:6.1f} MHz={med['MHz']:5.0f} in0={med['in0']:.3f} in6={med['in6']:.3f}", flush=True)
    time.sleep(3)
