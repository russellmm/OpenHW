"""Turn the BIOS manual PDF into per-setting notes: description, allowed range, option meanings.

    python tools/parse_manual.py E26933_AMD_TR5_Series_BIOS_manual_EM_V2_WEB.pdf
Writes src/OpenHW.App/Bios/Data/manual_notes.json. Needs the `pdftotext` command (poppler-utils).
"""
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))   # tools/bios-data: put the manual PDF here
DATA = os.path.join(os.path.dirname(os.path.dirname(ROOT)), 'src', 'OpenHW.App', 'Bios', 'Data')

RANGE = re.compile(r'values?\s+range\s+from\s+(-?[\d.]+)\s*(?:\w+)?\s+to\s+(-?[\d.]+)\s*(?:\w+)?'
                   r'(?:\s+with\s+an?\s+(?:interval|increment|step)\s+of\s+(-?[\d.]+))?', re.I)
CONF = re.compile(r'Configuration options?:\s*(.*)', re.I)
OPTLINE = re.compile(r'^\[([^\]]+)\]\s*(.*)$')


def norm(s):
    return re.sub(r'[^a-z0-9]', '', s.lower())


def is_heading(line, nxt):
    s = line.strip()
    if not s or len(s) > 70 or s.endswith(('.', ',', ':', ';')) or s.startswith(('[', '•', '�', 'NOTE', 'CAUTION')):
        return False
    if re.match(r'^\d+(\.\d+)*\s', s) or s.isdigit():
        return False
    if s[0].islower() or s.lower().startswith(('configuration options', 'use the', 'allows you', 'the ')):
        return False
    words = s.split()
    if len(words) > 9:
        return False
    # next meaningful line should be a description / options line
    return bool(nxt) and (nxt.strip()[:1].isupper() or nxt.strip().startswith('['))


def parse(text):
    lines = [l.rstrip() for l in text.splitlines()]
    lines = [l for l in lines if l.strip() and not re.fullmatch(r'\s*\d{1,3}\s*', l)]
    notes = {}
    cur = None
    body = []

    def flush():
        if not cur:
            return
        txt = ' '.join(x.strip() for x in body)
        txt = re.sub(r'\s+', ' ', txt).replace('�', '').strip()
        entry = {'name': cur}
        c = CONF.search(txt)
        if c:
            entry['options'] = c.group(1).split('NOTE')[0].strip()
        m = RANGE.search(txt)
        b = re.search(r'\[(-?[\d.]+)\]\s*-\s*\[(-?[\d.]+)\]', entry.get('options', ''))
        if m or b:
            lo, hi = (b.group(1), b.group(2)) if b else (m.group(1), m.group(2))
            step = m.group(3) if m else None
            st = re.search(r'(?:value )?stepping is\s+(-?[\d.]+)', txt)
            if st:
                step = st.group(1)
            entry['range'] = {'min': lo, 'max': hi, 'step': step.rstrip('.') if step else None}
        opts = {}
        for b in body:
            om = OPTLINE.match(b.strip())
            if om and om.group(2):
                opts[om.group(1).strip()] = om.group(2).strip()
        if opts:
            entry['option_help'] = opts
        desc = CONF.split(txt)[0]
        desc = re.split(r'\bNOTE\b', desc)[0].strip()
        if desc:
            entry['desc'] = desc
        if len(entry) > 1:
            # "CPU Core0~1 Voltage Override" -> CPU Core0 Voltage Override, CPU Core1 Voltage Override
            rm = re.match(r'^(.*?)(\d+)~(\d+)(.*)$', cur)
            names = [cur]
            if rm and int(rm.group(3)) - int(rm.group(2)) < 300:
                names += ['%s%d%s' % (rm.group(1), n, rm.group(4))
                          for n in range(int(rm.group(2)), int(rm.group(3)) + 1)]
            for nm in names[1:]:
                k = norm(nm)
                if k and k not in notes:
                    notes[k] = dict(entry, name=nm)
                elif k:
                    for f, v in entry.items():
                        notes[k].setdefault(f, v)
            k = norm(cur)
            if k and k not in notes:
                notes[k] = entry
            elif k:
                for f, v in entry.items():
                    notes[k].setdefault(f, v)

    for i, line in enumerate(lines):
        nxt = lines[i + 1] if i + 1 < len(lines) else ''
        if is_heading(line, nxt):
            flush()
            cur = line.strip(); body = []
        elif cur is not None:
            body.append(line)
    flush()
    return notes


def main(pdf):
    text = subprocess.run(['pdftotext', '-layout', pdf, '-'], capture_output=True).stdout.decode('latin1')
    notes = parse(text)
    out = os.path.join(DATA, 'manual_notes.json')
    with open(out, 'w', encoding='utf-8') as fh:
        json.dump(notes, fh, indent=1, ensure_ascii=False)
    print('wrote %s (%d settings, %d with ranges)' % (out, len(notes), sum('range' in n for n in notes.values())))


if __name__ == '__main__':
    pdf = sys.argv[1] if len(sys.argv) > 1 else next(
        os.path.join(ROOT, f) for f in os.listdir(ROOT) if f.lower().endswith('.pdf'))
    main(pdf)
