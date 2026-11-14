"""Board-profile capture for a board with a BMC: at each load step, read every Super I/O input (raw, at the pin) and
the BMC's named sensors (through openhw-helper's `bmc`, no root) side by side, so pins can be matched to the board's
own rail and header names by value and by behaviour. Loads are plain busy loops pinned per CCD (taskset).

    python3 tools/bmc-probe/match.py reference/wrx90e-2026-11-14/match.csv

Columns: step, sample, then nct:<input> (V, RPM or °C as the driver reports, scaled to units) and bmc:<name>.
"""
import base64, glob, json, os, socket, subprocess, sys, time

def hw(name):
    for d in glob.glob("/sys/class/hwmon/hwmon*"):
        if open(f"{d}/name").read().strip().startswith(name):
            return d

NCT, HSMP, K10 = hw("nct67"), hw("amd_hsmp"), hw("k10temp")

def nct():
    out = {}
    for p in glob.glob(f"{NCT}/*_input"):
        key = os.path.basename(p)[:-6]
        try:
            v = int(open(p).read())
        except OSError:
            continue
        out["nct:" + key] = v / 1000 if key.startswith(("in", "temp")) else v
    return out

def bmc():
    s = socket.socket(socket.AF_UNIX)
    s.settimeout(30)
    s.connect("/run/openhw/helper.sock")
    s.sendall(b"bmc\n")
    buf = b""
    while not buf.endswith(b"\n"):
        chunk = s.recv(1 << 16)
        if not chunk:
            break
        buf += chunk
    reply = buf.decode().strip()
    if not reply.startswith("ok "):
        raise SystemExit("helper: " + reply)
    return {"bmc:" + x["Name"]: x["Value"] for x in json.loads(base64.b64decode(reply[3:]))["Sensors"] if x["Value"] is not None}

def extra():
    return {"W": int(open(f"{HSMP}/power1_input").read()) / 1e6, "Tctl": int(open(f"{K10}/temp1_input").read()) / 1000}

R = lambda a, b: list(range(a, b))
# 9975WX: CCD n = cores 8n..8n+7 (SMT siblings +32), from cache/index3/id.
STEPS = [
    ("idle", []),
    ("8t CCD0", R(0, 8)), ("idle", []), ("8t CCD1", R(8, 16)), ("idle", []),
    ("8t CCD2", R(16, 24)), ("idle", []), ("8t CCD3", R(24, 32)), ("idle", []),
    ("16t CCD0+1", R(0, 16)), ("16t CCD2+3", R(16, 32)),
    ("64t all threads", R(0, 64)),
    ("idle after", []),
]
SETTLE, SAMPLES, EVERY = 8, 5, 2.5

rows, keys = [], set()
for name, cpus in STEPS:
    procs = [subprocess.Popen(["taskset", "-c", str(c), "sh", "-c", "while :; do :; done"]) for c in cpus]
    try:
        time.sleep(SETTLE)
        for i in range(SAMPLES):
            row = {"step": name, "sample": i, **extra(), **nct(), **bmc()}
            keys |= row.keys()
            rows.append(row)
            print(f"{name:18} {i} W={row['W']:.0f} Tctl={row['Tctl']:.0f} "
                  f"VCORE_0={row.get('bmc:+VCORE_0')} VCORE_1={row.get('bmc:+VCORE_1')} in0={row.get('nct:in0')}", flush=True)
            time.sleep(EVERY)
    finally:
        for p in procs:
            p.kill()
        for p in procs:
            p.wait()

order = ["step", "sample", "W", "Tctl"] + sorted(k for k in keys if k.startswith("nct:")) + sorted(k for k in keys if k.startswith("bmc:"))
os.makedirs(os.path.dirname(os.path.abspath(sys.argv[1])), exist_ok=True)
with open(sys.argv[1], "w") as f:
    f.write(",".join(order) + "\n")
    for r in rows:
        f.write(",".join("" if r.get(k) is None else str(r[k]) for k in order) + "\n")
print("wrote", sys.argv[1], len(rows), "rows")
