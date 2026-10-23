"""Read HWiNFO64 shared memory (Global\\HWiNFO_SENS_SM2) and dump all sensors.

Usage: python -I hwinfo_shm.py dump            -> print all readings once
       python -I hwinfo_shm.py log OUT.csv SECS INTERVAL [regex]  -> log matching readings to CSV
"""
import ctypes, ctypes.wintypes as wt, struct, sys, time, re, csv

FILE_MAP_READ = 4
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenFileMappingW.restype = wt.HANDLE
k32.OpenFileMappingW.argtypes = [wt.DWORD, wt.BOOL, wt.LPCWSTR]
k32.MapViewOfFile.restype = ctypes.c_void_p
k32.MapViewOfFile.argtypes = [wt.HANDLE, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]

TYPES = {0: "none", 1: "temp", 2: "volt", 3: "fan", 4: "current", 5: "power", 6: "clock", 7: "usage", 8: "other"}


def open_sm():
    h = k32.OpenFileMappingW(FILE_MAP_READ, False, "Global\\HWiNFO_SENS_SM2")
    if not h:
        raise SystemExit(f"OpenFileMapping failed: {ctypes.get_last_error()} (shared memory support off?)")
    p = k32.MapViewOfFile(h, FILE_MAP_READ, 0, 0, 0)
    return p


def snapshot(p):
    hdr = ctypes.string_at(p, 44)
    sig, ver, rev, poll, so, ss, sn, eo, es, en = struct.unpack_from("<IIIqIIIIII", hdr)
    total = eo + es * en
    buf = ctypes.string_at(p, total)
    sensors = []
    for i in range(sn):
        o = so + i * ss
        sid, sinst = struct.unpack_from("<II", buf, o)
        orig = buf[o + 8:o + 136].split(b"\0")[0].decode("latin-1")
        user = buf[o + 136:o + 264].split(b"\0")[0].decode("latin-1")
        sensors.append((sid, sinst, orig, user))
    readings = []
    for i in range(en):
        o = eo + i * es
        typ, sidx, rid = struct.unpack_from("<III", buf, o)
        lo = buf[o + 12:o + 140].split(b"\0")[0].decode("latin-1")
        lu = buf[o + 140:o + 268].split(b"\0")[0].decode("latin-1")
        unit = buf[o + 268:o + 284].split(b"\0")[0].decode("latin-1")
        val, vmin, vmax, vavg = struct.unpack_from("<dddd", buf, o + 284)
        s = sensors[sidx]
        readings.append(dict(type=TYPES.get(typ, typ), sensor=s[2], sensor_id=f"{s[0]:#x}/{s[1]}",
                             reading_id=f"{rid:#x}", label=lo, label_user=lu, unit=unit,
                             value=val, min=vmin, max=vmax, avg=vavg))
    return dict(sig=hex(sig), ver=ver, rev=rev, poll=poll, sensors=sensors, readings=readings)


def main():
    p = open_sm()
    if sys.argv[1] == "dump":
        s = snapshot(p)
        print(f"# sig={s['sig']} ver={s['ver']}.{s['rev']} sensors={len(s['sensors'])} readings={len(s['readings'])}")
        cur = None
        for r in s["readings"]:
            if r["sensor"] != cur:
                cur = r["sensor"]
                print(f"\n== {cur}  [{r['sensor_id']}]")
            lbl = r["label"] + (f"  (user: {r['label_user']})" if r["label_user"] != r["label"] else "")
            print(f"  {r['type']:7} {r['reading_id']:>10}  {lbl:48} {r['value']:12.4f} {r['unit']:6}"
                  f" min {r['min']:.4f} max {r['max']:.4f} avg {r['avg']:.4f}")
    elif sys.argv[1] == "log":
        out, secs, iv = sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
        rx = re.compile(sys.argv[5]) if len(sys.argv) > 5 else None
        s = snapshot(p)
        keys = [(r["sensor"], r["label"], r["unit"]) for r in s["readings"]
                if rx is None or rx.search(r["sensor"] + " | " + r["label"])]
        with open(out, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["t"] + [f"{a} | {b} [{c}]" for a, b, c in keys])
            t0 = time.time()
            while time.time() - t0 < secs:
                s = snapshot(p)
                m = {(r["sensor"], r["label"], r["unit"]): r["value"] for r in s["readings"]}
                w.writerow([f"{time.time() - t0:.2f}"] + [m.get(k, "") for k in keys])
                f.flush()
                time.sleep(iv)


main()
