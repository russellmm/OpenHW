import re, sys
p, word, n = sys.argv[1], sys.argv[2], int(sys.argv[3])
d = open(p, 'rb').read()
strs = [(m.start(), m.group().decode('utf-16le')) for m in re.finditer(rb'(?:[\x20-\x7e\xb0]\x00){2,200}', d)]
for i, (o, s) in enumerate(strs):
    if s == word:
        print(f'===== {word} @ {o:#x} (idx {i})')
        for o2, s2 in strs[max(0, i - n): i + n]:
            print(f'  {o2:#9x} {s2[:100]}')
