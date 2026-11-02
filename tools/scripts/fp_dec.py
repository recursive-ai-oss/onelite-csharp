#!/usr/bin/env python3
"""For decimal literals of k significant digits: does the shell print them short or with 17 digits, against the exact tail?
usage: fp_dec.py SQLITE3 N"""
import random, subprocess, sys, os, math
from fractions import Fraction

exe, n = sys.argv[1], int(sys.argv[2])
rnd = random.Random(11)
items = []
for _ in range(n):
    k = rnd.randint(1, 16)
    e = rnd.randint(-8, 8)
    digs = str(rnd.randint(10 ** (k - 1), 10 ** k - 1))
    items.append((k, digs, e))
path = os.path.join(os.environ.get('TMPDIR', '.'), 'dec.sql')
with open(path, 'w') as f:
    for k, d, e in items:
        f.write('select %se%d;\n' % (d, e - k + 1))
out = subprocess.run([exe, ':memory:', '.read ' + path], capture_output=True, text=True).stdout.split('\n')
stat = {}
for (k, d, e), o in zip(items, out):
    lit = Fraction(int(d)) * Fraction(10) ** (e - k + 1)
    v = float('%se%d' % (d, e - k + 1))
    tail = abs(float((Fraction(v) - lit) / (Fraction(10) ** (e - 14))))  # in 15th-digit units
    digs = o.lower().split('e')[0].replace('.', '').replace('-', '').lstrip('0').rstrip('0')
    short = len(digs) <= 15
    s = stat.setdefault(k, [0, 0, 9.0, 0.0])
    s[0 if short else 1] += 1
    if short:
        s[3] = max(s[3], tail)
    else:
        s[2] = min(s[2], tail)
for k in sorted(stat):
    s = stat[k]
    print('k=%2d short %5d long %5d  min|tail| of long %.4f  max|tail| of short %.4f' % (k, s[0], s[1], s[2], s[3]))

if len(sys.argv) > 3:
    from decimal import Decimal, getcontext
    getcontext().prec = 30
    kk = int(sys.argv[3])
    longonly = len(sys.argv) > 4
    shown = {True: 0, False: 0}
    for (k, d, e), o in zip(items, out):
        if (k != kk and not longonly) or (longonly and k > kk):
            continue
        v = float('%se%d' % (d, e - k + 1))
        digs = o.lower().split('e')[0].replace('.', '').replace('-', '').lstrip('0').rstrip('0')
        short = len(digs) <= 15
        if shown[short] < 12 and not (longonly and short):
            shown[short] += 1
            print('short' if short else 'LONG ', 'lit', d, 'e', e - k + 1, 'exact', Decimal(v), 'shell', o)
