"""Build src/OpenHW.App/Bios/Data/bios-<version>.json (the emulator's menu database) from an ASUS .CAP BIOS file.

Usage:  python3 tools/bios-data/build_bios_data.py Pro-WS-WRX90E-SAGE-SE-ASUS-1405.CAP
"""
import json
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import uefi_extract as ue  # noqa: E402

ROOT = os.path.dirname(os.path.abspath(__file__))   # tools/bios-data: put the .CAP here
DATA = os.path.join(os.path.dirname(os.path.dirname(ROOT)), 'src', 'OpenHW.App', 'Bios', 'Data')

# Known formsets -> short ids used by the emulator.
KNOWN = {
    '7b59104a-c00d-4158-87ff-f04d6396a915': 'setup',
    'a16ad209-58b0-4c6d-817e-6793b180fd75': 'cbs_shp',      # AMD CBS, TR PRO 9000WX (Shimada Peak)
    '475dde81-7e41-477e-89e9-7cdd94e8d0bb': 'cbs_stp',      # AMD CBS, TR PRO 7000WX (Storm Peak)
    '14351d88-b72e-431e-a126-36c2f5f13a08': 'aod_shp',      # AMD Overclocking, 9000WX
    'e39ac631-48ed-445e-9042-55b34f405843': 'aod_stp',      # AMD Overclocking, 7000WX
    'b863b959-0ec6-4033-99c1-8fd89f040222': 'pbs',
    '0d8c6f9c-e8d3-4180-8120-14e1f84328c5': 'memstatus',
    '01239999-fc0e-4b6e-9e79-d54d5db6cd20': 'servermgmt',
    '8beb8c19-3fec-4fab-a378-c903e890fcae': 'eventlogs',
    '5e39cf2e-6712-45ab-84c4-35d3c6a3686d': 'nvme',
    'aa5d54a2-59c8-458b-80d8-e41944ad1d99': 'raid',
    'cee81408-4764-4314-ab17-5d61691bda1f': 'realtek',
    '9b942747-154e-4d29-a436-bf7100c8b53b': 'ipv4',
    '02eea107-98db-400e-9830-460a1542d799': 'ipv6',
    '4d20583a-7765-4e7a-8a67-dcde74ee3ec5': 'httpboot',
    '80e1202e-2697-4264-9cc9-80762c3e5863': 'storagesel',
}


def collect_formsets(sections):
    """Parse every module that carries IFR + strings. Keep the best copy of each formset."""
    best = {}
    for fguid, typ, body in sections:
        if typ not in (0x10, 0x19):
            continue
        fpk = ue.find_form_packages(body)
        if not fpk:
            continue
        spk = ue.find_string_packages(body)
        if not spk:
            continue
        fss = []
        for o in fpk:
            try:
                fss += ue.parse_forms(body, o, spk[0])
            except Exception:
                pass
        real = [f for f in fss if f['forms'] and f['title']]
        for fs in real:
            n_items = sum(len(f['items']) for f in fs['forms'])
            score = (n_items, -len(real))
            cur = best.get(fs['guid'])
            if cur is None or score > cur[0]:
                best[fs['guid']] = (score, fs, fguid)
    return {g: (fs, mod) for g, (_, fs, mod) in best.items()}


def read_int(buf, off, size):
    if off + size > len(buf):
        return None
    return int.from_bytes(buf[off:off + size], 'little')


def write_int(buf, off, size, val):
    if val is None or off + size > len(buf):
        return
    buf[off:off + size] = int(val & ((1 << (8 * size)) - 1)).to_bytes(size, 'little')


def main(cap_path):
    data = open(cap_path, 'rb').read()
    m = re.search(r'-(\d{4})\.CAP$', os.path.basename(cap_path), re.I)
    version = m.group(1) if m else 'unknown'
    print('walking capsule ...')
    sections = ue.walk_capsule(data)
    print('  %d sections' % len(sections))
    found = collect_formsets(sections)
    std = ue.std_defaults(data)
    print('  %d formsets, %d default variables' % (len(found), len(std)))

    varstores = {}      # key -> {name, guid, size, defaults(bytearray), src}
    formsets = {}
    for g, (fs, mod) in found.items():
        fsid = KNOWN.get(g, 'fs_' + g[:8])
        vsmap = {}
        for vid, vs in fs['varstores'].items():
            key = '%s:%s' % (vs['name'], vs['guid'][:8])
            vsmap[vid] = key
            if key not in varstores and vs['size']:
                d = std.get(vs['name'])
                if d is not None and len(d) == vs['size']:
                    varstores[key] = {'name': vs['name'], 'guid': vs['guid'], 'size': vs['size'],
                                      'buf': bytearray(d), 'src': 'StdDefaults'}
                else:
                    varstores[key] = {'name': vs['name'], 'guid': vs['guid'], 'size': vs['size'],
                                      'buf': bytearray(vs['size']), 'src': 'IFR'}
        form_ids = {f['id'] for f in fs['forms']}
        qmap = {}
        forms = {}
        for f in fs['forms']:
            items = []
            for idx, it in enumerate(f['items']):
                it = dict(it)
                it['id'] = '%s:%d:%d' % (fsid, f['id'], idx)
                if 'q' in it:
                    key = vsmap.get(it.pop('vs'))
                    if key and key in varstores and it.get('sz') and it['o'] + it['sz'] <= varstores[key]['size']:
                        it['vs'] = key
                        qmap.setdefault(it['q'], [key, it['o'], it['sz']])
                    else:
                        it.pop('o', None)
                        if it['t'] in ('oneof', 'num', 'check', 'string'):
                            qmap.setdefault(it['q'], ['@', '%s:%d' % (fsid, it['q']), it.get('sz', 1)])
                    if it['t'] == 'ref':
                        tgt = it.get('f')
                        if it.get('fsguid') and it['fsguid'] in found and it['fsguid'] != g:
                            it['link'] = [KNOWN.get(it['fsguid'], 'fs_' + it['fsguid'][:8]), None]
                        elif tgt == f['id']:
                            it['t'] = 'act'          # AMI callback button (Save, Load defaults, ...)
                        elif tgt in form_ids:
                            it['link'] = [fsid, tgt]
                        else:
                            it['link'] = ['@ext', it['p'].strip()]
                    it.pop('fsguid', None)
                    if it.get('qf', 0) == 0:
                        it.pop('qf', None)
                items.append(it)
            forms[str(f['id'])] = {'title': f['title'], 'items': items}
        formsets[fsid] = {'guid': g, 'title': fs['title'], 'root': fs['forms'][0]['id'],
                          'forms': forms, 'qmap': {str(k): v for k, v in qmap.items()}}

    # Fill IFR defaults into stores that have no factory default image.
    n_mis = 0
    for fsid, fs in formsets.items():
        for f in fs['forms'].values():
            for it in f['items']:
                key = it.get('vs')
                if not key:
                    continue
                vs = varstores[key]
                d = it.get('ifrdef', it.get('def'))
                if d is None and it['t'] == 'oneof' and it['opts']:
                    d = it['opts'][0][1]
                if vs['src'] == 'IFR':
                    if d is not None and it['t'] != 'string':
                        write_int(vs['buf'], it['o'], it['sz'], d)
                elif d is not None and it['t'] != 'string':
                    if read_int(vs['buf'], it['o'], it['sz']) != d:
                        n_mis += 1
    print('  %d items whose IFR default differs from factory StdDefaults (factory value wins)' % n_mis)

    agesa = build_date = None
    for _, _, body in sections:
        if agesa is None:
            m = re.search(rb'AGESA!V9\x00([ -~]+)', body)
            if m:
                agesa = m.group(1).decode()
        if build_date is None:
            m = re.search(rb'((?:[01]\x00\d\x00/\x00[0-3]\x00\d\x00/\x002\x000\x00[12]\x00\d\x00))\x00\x00', body)
            if m and b'1\x009\x00' not in m.group(1)[12:16]:
                build_date = m.group(1).decode('utf-16le')
    out = {
        'board': 'Pro WS WRX90E-SAGE SE',
        'bios_version': version,
        'agesa': agesa,
        'build_date': build_date,
        'source_file': os.path.basename(cap_path),
        'varstores': {k: {'name': v['name'], 'guid': v['guid'], 'size': v['size'], 'src': v['src'],
                          'defaults': v['buf'].hex()} for k, v in varstores.items()},
        'formsets': formsets,
        'variants': {
            'SHP': {'label': 'AMD Ryzen Threadripper PRO 9000WX (Zen 5, "Shimada Peak")',
                    'cbs': 'cbs_shp', 'aod': 'aod_shp'},
            'STP': {'label': 'AMD Ryzen Threadripper PRO 7000WX (Zen 4, "Storm Peak")',
                    'cbs': 'cbs_stp', 'aod': 'aod_stp'},
        },
    }
    os.makedirs(DATA, exist_ok=True)
    path = os.path.join(DATA, 'bios-%s.json' % version)
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(out, fh, ensure_ascii=False, separators=(',', ':'))
    total = sum(len(f['items']) for fs in formsets.values() for f in fs['forms'].values())
    print('wrote %s  (%d formsets, %d items, %.1f MB)' % (path, len(formsets), total,
                                                           os.path.getsize(path) / 1e6))
    for fsid, fs in sorted(formsets.items()):
        print('   %-12s %-40s %4d forms' % (fsid, fs['title'][:40], len(fs['forms'])))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        caps = [f for f in os.listdir(ROOT) if f.lower().endswith('.cap')]
        if not caps:
            sys.exit(__doc__)
        sys.argv.append(os.path.join(ROOT, caps[0]))
    main(sys.argv[1])
