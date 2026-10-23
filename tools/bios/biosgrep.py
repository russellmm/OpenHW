import re, os, sys
root = sys.argv[1]
pat = re.compile(sys.argv[2])
for dp, dn, fn in os.walk(root):
    for f in fn:
        if not f.endswith('.bin'): continue
        p = os.path.join(dp, f)
        d = open(p, 'rb').read()
        hits = []
        for m in re.finditer(rb'(?:[\x20-\x7e]\x00){3,80}', d):
            s = m.group().decode('utf-16le')
            if pat.search(s): hits.append(s)
        if hits:
            print('##', p.replace(root, ''), len(hits))
            for h in hits[:200]: print('   ', h)
