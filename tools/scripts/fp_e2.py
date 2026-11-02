#!/usr/bin/env python3
"""Looks at the shell's 17-digit rounding for values in [100,1000) (power of ten exact): usage fp_e2.py SQLITE3 N"""
import random, subprocess, sys, os, math, collections
from fractions import Fraction

exe, n = sys.argv[1], int(sys.argv[2])
rnd = random.Random(5)
vals = []
while len(vals) < n:
    v = rnd.uniform(100, 1000)
    if float('%.14e' % v) != v:
        vals.append(v)
path = os.path.join(os.environ.get('TMPDIR', '.'), 'e2.sql')
with open(path, 'w') as f:
    for v in vals:
        f.write('select %r;\n' % v)
out = subprocess.run([exe, ':memory:', '.read ' + path], capture_output=True, text=True).stdout.split('\n')
bins = collections.defaultdict(lambda: [0, 0])
rows = []
for v, o in zip(vals, out):
    scaled = Fraction(v) * 10 ** 14
    fl = int(scaled)
    frac = float(scaled - fl)
    digs = o.replace('.', '').ljust(17, '0')
    wi = int(digs)
    if wi not in (fl, fl + 1):
        continue
    up = wi == fl + 1
    bins[int(frac * 200)][up] += 1
    rows.append((frac, up, v))
bb = collections.defaultdict(lambda: [0, 0])
for fr, up, v in rows:
    if 0.45 <= fr < 0.5:
        bb[(math.frexp(v)[1], int(fr * 20))][up] += 1
for k in sorted(bb):
    print('binade 2^%d frac %.2f' % (k[0] - 1, k[1] / 20), 'down/up', bb[k])
