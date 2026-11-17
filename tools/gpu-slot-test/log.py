#!/usr/bin/env python3
"""log.py <out.csv> <seconds> — every 5 s: GPU temps/power/fan (nvidia-smi) and the BMC's PCIE slot temps (helper `bmc`)."""
import sys, time, socket, base64, json, subprocess, csv
def bmc():
    s = socket.socket(socket.AF_UNIX); s.settimeout(30); s.connect('/run/openhw/helper.sock'); s.sendall(b'bmc\n')
    d = b''
    while not d.endswith(b'\n'):
        c = s.recv(65536)
        if not c: break
        d += c
    j = json.loads(base64.b64decode(d.decode().split()[-1]))
    return {x['Name']: x['Value'] for x in j['Sensors'] if x['Name'].startswith('PCIE') or x['Name'] in ('CPU Package Temp',)}
out, secs = sys.argv[1], int(sys.argv[2]); t0 = time.time()
with open(out, 'w', newline='') as f:
    w = None
    while time.time() - t0 < secs:
        g = subprocess.run(['nvidia-smi', '--query-gpu=pci.bus_id,temperature.gpu,power.draw,fan.speed',
                            '--format=csv,noheader,nounits'], capture_output=True, text=True).stdout
        row = {'t': round(time.time() - t0)}
        for line in g.strip().splitlines():
            bus, temp, pw, fan = [x.strip() for x in line.split(',')]
            k = bus[-7:-5]; row[f'gpu{k}_temp'] = temp; row[f'gpu{k}_w'] = pw; row[f'gpu{k}_fan'] = fan
        try: row.update(bmc())
        except Exception as e: row['err'] = str(e)
        if w is None:
            w = csv.DictWriter(f, fieldnames=list(row) + ['err'], extrasaction='ignore'); w.writeheader()
        w.writerow(row); f.flush(); time.sleep(5)
