#!/usr/bin/env python3
"""Collects the pinned shell's rendering of random doubles and compares it with candidate rules.

usage: fp_probe.py SQLITE3 N SEED [kind]
Writes the shell's output next to correctly rounded 15/17-digit renderings so that a rule can be fitted black-box.
"""
import random, struct, subprocess, sys, os, tempfile
from decimal import Decimal, getcontext

getcontext().prec = 80
exe, n, seed = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
rnd = random.Random(seed)


def gen():
    k = rnd.randrange(4)
    if k == 0:
        return struct.unpack('<d', struct.pack('<Q', rnd.getrandbits(64)))[0]
    if k == 1:
        return rnd.uniform(-1000, 1000)
    if k == 2:
        return rnd.random() * 10 ** rnd.randint(-30, 30)
    return float(rnd.randint(1, 10 ** 6)) / rnd.randint(1, 10 ** 4)


vals = []
while len(vals) < n:
    v = gen()
    if v != v or v in (float('inf'), float('-inf')):
        continue
    vals.append(v)

path = os.path.join(os.environ.get('TMPDIR', '.'), f'fp_{seed}.sql')
with open(path, 'w') as f:
    for v in vals:
        f.write('select %s;\n' % repr(v) if 'e' in repr(v) or '.' in repr(v) else 'select %s.0;\n' % repr(v))
out = subprocess.run([exe, ':memory:', '.read ' + path], capture_output=True, text=True).stdout.split('\n')
bad = 0
for v, o in zip(vals, out):
    if repr(v) == o:
        pass
    # compare digit strings only
    exact17 = '%.16e' % v
    d17 = exact17.replace('.', '').split('e')[0].lstrip('-')
    digs = o.lower().replace('-', '').replace('.', '')
    mant = digs.split('e')[0].rstrip('0')
    e17 = d17.rstrip('0')
    if mant != e17 and not (len(mant) <= 15 and float('%.14e' % v) == v):
        bad += 1
        if bad <= 25:
            print(repr(v), o, '%.16e' % v)
print('total', len(vals), 'differ from correctly rounded', bad)
