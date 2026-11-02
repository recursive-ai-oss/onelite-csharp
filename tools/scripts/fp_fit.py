#!/usr/bin/env python3
"""Fits candidate rules for the shell's 17-digit real rendering. usage: fp_fit.py SQLITE3 N SEED"""
import random, struct, subprocess, sys, os, math
from fractions import Fraction

exe, n, seed = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
rnd = random.Random(seed)


def gen():
    k = rnd.randrange(4)
    if k == 0:
        m = rnd.getrandbits(53) | (1 << 52)
        sh = rnd.randint(-1000, 960)
    elif k == 1:
        m = rnd.getrandbits(53) | (1 << 52)
        sh = rnd.randint(-60, -40)
    elif k == 2:
        m = rnd.getrandbits(rnd.randint(20, 53)) | 1
        sh = rnd.randint(-70, 10)
    else:
        m = rnd.randint(1, 10 ** 6)
        sh = -rnd.randint(0, 30)
    return m, sh


def expr(m, sh):
    # exact: every step multiplies or divides by a power of two that a double holds exactly
    big = '1099511627776.0'  # 2**40
    e = 'CAST(%d AS REAL)' % m
    n = abs(sh)
    a, r = divmod(n, 40)
    op = '*' if sh > 0 else '/'
    for _ in range(a):
        e += ' %s %s' % (op, big)
    if r:
        e += ' %s %d.0' % (op, 1 << r)
    return e


vals = []
sqls = []
while len(vals) < n:
    m, sh = gen()
    try:
        v = math.ldexp(m, sh)
    except OverflowError:
        continue
    if v == 0 or math.isinf(v) or v < 2.3e-308:
        continue
    vals.append(v)
    sqls.append(expr(m, sh))

path = os.path.join(os.environ.get('TMPDIR', '.'), f'fit_{seed}.sql')
with open(path, 'w') as f:
    for q in sqls:
        f.write('select %s;\n' % q)
out = subprocess.run([exe, ':memory:', '.read ' + path], capture_output=True, text=True).stdout.split('\n')


def parse(o):
    o = o.lower()
    if 'e' in o:
        m, e = o.split('e')
        e = int(e)
    else:
        m, e = o, 0
    ip, _, fp = m.partition('.')
    digs = ip + fp
    # exponent of first digit
    lead = len(ip) - 1 + e
    stripped = digs.lstrip('0')
    lead -= len(digs) - len(stripped)
    return stripped.rstrip('0') or '0', lead


def exact_digits(v, nd):
    fr = Fraction(v)
    e = math.floor(math.log10(v))
    while Fraction(10) ** e > fr:
        e -= 1
    while Fraction(10) ** (e + 1) <= fr:
        e += 1
    scaled = fr / Fraction(10) ** (e - nd + 1)
    q = int(scaled + Fraction(1, 2))  # round half up (ties rare)
    s = str(q)
    if len(s) > nd:
        e += 1
        s = s[:nd]
    return s.rstrip('0'), e


def cand_a(v):
    e = math.floor(math.log10(v))
    p = 16 - e
    x = v * 10.0 ** p if p >= 0 else v / 10.0 ** (-p)
    q = int(round(x))
    s = str(q)
    if len(s) > 17:
        e += 1
        s = s[:17]
    return s.rstrip('0'), e


def dr(nd, final=17):
    def f(v):
        fr = Fraction(v)
        e = math.floor(math.log10(v))
        while Fraction(10) ** e > fr:
            e -= 1
        while Fraction(10) ** (e + 1) <= fr:
            e += 1
        q = int(fr / Fraction(10) ** (e - nd + 1) + Fraction(1, 2))
        if q >= 10 ** nd:
            e += 1
            q //= 10
        q2 = (q + 5 * 10 ** (nd - final - 1)) // 10 ** (nd - final)
        s = str(q2)
        if len(s) > final:
            e += 1
            s = s[:final]
        return s.rstrip('0'), e
    return f


def approx(pm, nm, nd=18, pbits=64, keep=0):
    def f(v):
        fr = Fraction(v)
        e = math.floor(math.log10(v))
        while Fraction(10) ** e > fr:
            e -= 1
        while Fraction(10) ** (e + 1) <= fr:
            e += 1
        k = nd - 1 - e
        m, ex = math.frexp(v)
        mi = int(m * 2 ** 53)
        q = ex - 53
        if k >= 0:
            X = 10 ** k
            b = X.bit_length()
            if b <= pbits:
                P = X << (pbits - b)
            else:
                sh = b - pbits
                P = X >> sh
                if pm == 'n' and (X >> (sh - 1)) & 1:
                    P += 1
            t = b - pbits
        else:
            X = 10 ** (-k)
            b = X.bit_length()
            num = 1 << (pbits - 1 + b)
            P = num // X
            if pm == 'n' and (num % X) * 2 >= X:
                P += 1
            t = -(pbits - 1 + b)
        M = mi * P
        if keep:
            drop = M.bit_length() - keep
            if drop > 0:
                M = (M >> drop) << drop
        sh = -(q + t)
        if sh > 0:
            N = M >> sh
            if nm == 'n' and (M >> (sh - 1)) & 1:
                N += 1
        else:
            N = M << (-sh)
        if N >= 10 ** nd:
            N //= 10
            e += 1
        if N < 10 ** (nd - 1):
            N *= 10
            e -= 1
        q2 = (N + 5 * 10 ** (nd - 18)) // 10 ** (nd - 17)
        s = str(q2)
        if len(s) > 17:
            e += 1
            s = s[:17]
        return s.rstrip('0'), e
    return f


cands = {'exact': lambda v: exact_digits(v, 17), 'dr18': dr(18), 'fn64': approx('f', 'n', 18, 64)}
res = {k: 0 for k in cands}
samples = {k: [] for k in cands}
tot = 0
for v, o in zip(vals, out):
    if float('%.14e' % v) == v:
        continue  # 15-digit path
    tot += 1
    want = parse(o)
    for k, f in cands.items():
        try:
            got = f(v)
        except OverflowError:
            got = None
        if got == want:
            res[k] += 1
        elif len(samples[k]) < 5:
            samples[k].append((repr(v), o, got))
print('17-digit cases', tot, res)
for k in samples:
    print(k, samples[k])

# --- mismatch anatomy: where does the exact scaled value sit relative to the shell's integer?
if len(sys.argv) > 4:
    cnt = 0
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        w = parse(o)
        ex = exact_digits(v, 17)
        if w != ex:
            fr = Fraction(v)
            e = ex[1]
            scaled = fr / Fraction(10) ** (e - 16)
            wi = int(w[0].ljust(17, '0'))
            print(repr(v), 'shell', wi, 'exact', float(scaled - wi), 'ulp17', float(Fraction(math.ulp(v)) / Fraction(10) ** (e - 16)))
            cnt += 1
            if cnt > 30:
                break

# --- decision anatomy: fraction of the exact 17-digit scaled value against the shell's rounding
if len(sys.argv) > 5:
    import collections
    up_min, down_max = 1.0, 0.0
    bad = []
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        w = parse(o)
        fr = Fraction(v)
        e = exact_digits(v, 17)[1]
        scaled = fr / Fraction(10) ** (e - 16)
        fl = int(scaled)
        wi = int(w[0].ljust(17, '0'))
        frac = float(scaled - fl)
        if wi == fl + 1:
            up = True
        elif wi == fl:
            up = False
        else:
            bad.append((repr(v), o)); continue
        if up and frac < 0.5 or (not up and frac >= 0.5):
            bad.append((repr(v), o, frac, up, e, float(scaled)))
    ups=[b for b in bad if len(b)>2 and b[3]]
    downs=[b for b in bad if len(b)>2 and not b[3]]
    print('misdecided', len(bad), 'ups', len(ups), 'min frac', min(b[2] for b in ups), 'downs', len(downs), 'max frac', max([b[2] for b in downs] or [0]), 'other', len([b for b in bad if len(b)<=2]))
    for b in downs[:15]:
        print(b)

# --- per-exponent threshold
if len(sys.argv) > 6:
    per = {}
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        w = parse(o)
        fr = Fraction(v)
        e = exact_digits(v, 17)[1]
        scaled = fr / Fraction(10) ** (e - 16)
        fl = int(scaled)
        wi = int(w[0].ljust(17, '0'))
        frac = float(scaled - fl)
        up = wi == fl + 1
        d = per.setdefault(e, [1.0, 0.0, 0])
        if up:
            d[0] = min(d[0], frac)
        elif frac > d[1]:
            d[1] = frac
        d[2] += 1
    for e in sorted(per):
        a = per[e]
        print(e, 'minUp %.4f maxDown %.4f n %d' % (a[0], a[1], a[2]), 'OVERLAP' if a[0] < a[1] else '')

# --- residual of dr18: frac and direction
if len(sys.argv) > 7:
    f18 = dr(18)
    rows = []
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        w = parse(o)
        g = f18(v)
        if g != w:
            fr = Fraction(v)
            e = exact_digits(v, 17)[1]
            scaled = fr / Fraction(10) ** (e - 16)
            fl = int(scaled)
            wi = int(w[0].ljust(17, '0'))
            rows.append((float(scaled - fl), 'up' if wi == fl + 1 else ('down' if wi == fl else 'far'), e, repr(v), o))
    rows.sort()
    print('residual', len(rows))
    import collections
    print(collections.Counter(r[1] for r in rows))
    for r in rows[:12] + rows[-12:]:
        print(r)

# --- residual of the chosen model
if len(sys.argv) > 8:
    fm = approx('f', 'n', 18, 64)
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        w = parse(o)
        g = fm(v)
        if g != w:
            fr = Fraction(v)
            e = exact_digits(v, 17)[1]
            scaled = fr / Fraction(10) ** (e - 16)
            print('e', e, 'frac %.5f' % float(scaled - int(scaled)), 'shell', o, 'model', g, repr(v))

# --- ambiguous region features
if len(sys.argv) > 9:
    import collections
    st = collections.defaultdict(lambda: [0, 0])
    rows = []
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        w = parse(o)
        fr = Fraction(v)
        e = exact_digits(v, 17)[1]
        scaled = fr / Fraction(10) ** (e - 16)
        fl = int(scaled)
        frac = float(scaled - fl)
        if not (0.45 <= frac < 0.5):
            continue
        wi = int(w[0].ljust(17, '0'))
        up = wi == fl + 1
        m, ex = math.frexp(v)
        mi = int(m * 2 ** 53)
        st[('low3', mi & 7)][up] += 1
        st[('e', e)][up] += 1
        st[('fl%2', fl % 2)][up] += 1
        st[('fl%10', fl % 10)][up] += 1
        st[('frac<.475', frac < 0.475)][up] += 1
        st[('bin', ex)][up] += 1
    for k in sorted(st, key=str):
        if k[0] in ('low3', 'fl%2', 'fl%10', 'frac<.475'):
            print(k, 'down/up', st[k])

# --- relation to the shortest round-trip form
if len(sys.argv) > 10:
    import collections
    c = collections.Counter()
    ex_ = []
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        w = parse(o)
        rp = parse(repr(v))
        d = dr(18)(v)
        ex = exact_digits(v, 17)
        key = ('shell=repr' if w == rp else 'shell!=repr', 'dr18=repr' if d == rp else 'dr18!=repr', 'shell=dr18' if w == d else 'shell!=dr18', 'repr17' if len(rp[0]) == 17 else 'repr<17')
        c[key] += 1
        if key[0] == 'shell!=repr' and len(ex_) < 8:
            ex_.append((repr(v), w, rp, d))
    for k, n in sorted(c.items()):
        print(k, n)
    for x in ex_:
        print(x)

# --- does the up candidate round-trip?
if len(sys.argv) > 11:
    import collections
    c = collections.Counter()
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        fr = Fraction(v)
        e = exact_digits(v, 17)[1]
        scaled = fr / Fraction(10) ** (e - 16)
        fl = int(scaled)
        frac = float(scaled - fl)
        if not (0.45 <= frac < 0.5):
            continue
        w = parse(o)
        wi = int(w[0].ljust(17, '0'))
        up = wi == fl + 1
        def rt(n):
            return float('%de%d' % (n, e - 16)) == v
        c[('up' if up else 'down', 'upRT' if rt(fl + 1) else 'upNoRT', 'dnRT' if rt(fl) else 'dnNoRT')] += 1
    for k, n in sorted(c.items()):
        print(k, n)

# --- by binade
if len(sys.argv) > 12:
    import collections
    bb = collections.defaultdict(lambda: [0, 0])
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        fr = Fraction(v)
        e = exact_digits(v, 17)[1]
        scaled = fr / Fraction(10) ** (e - 16)
        fl = int(scaled)
        frac = float(scaled - fl)
        if not (0.45 <= frac < 0.5):
            continue
        w = parse(o)
        wi = int(w[0].ljust(17, '0'))
        bb[math.frexp(v)[1]][wi == fl + 1] += 1
    for k in sorted(bb):
        print('binade', k - 1, 'down/up', bb[k])

# --- the 15-digit path
if len(sys.argv) > 13:
    import collections
    c = collections.Counter()
    shown = 0
    for v, o in zip(vals, out):
        if float('%.14e' % v) != v:
            continue
        w = parse(o)
        ex = exact_digits(v, 15)
        c['same' if w == ex else 'diff'] += 1
        if w != ex and shown < 6:
            shown += 1
            print('15-digit diff', repr(v), o, ex)
    print('15-digit path', dict(c))

# --- 15-digit path: signed tail in 15th-digit units vs shell's choice
if len(sys.argv) > 14:
    import collections
    rows = []
    for v, o in zip(vals, out):
        if float('%.14e' % v) != v:
            continue
        w = parse(o)
        ex = exact_digits(v, 15)
        fr = Fraction(v)
        e = ex[1]
        unit = Fraction(10) ** (e - 14)
        dec = int(ex[0].ljust(15, '0'))
        tail = float((fr - dec * unit) / unit)
        short = (w == ex)
        rows.append((tail, short, len(ex[0])))
    for lo in [-0.5, -0.1, -0.05, -0.02, -0.01, -0.005, 0, 0.005, 0.01, 0.02, 0.05, 0.1, 0.5]:
        pass
    bins = collections.defaultdict(lambda: [0, 0])
    for t, sh, nd in rows:
        if nd >= 15:
            b = round(t * 100) / 100
            bins[b][sh] += 1
    for b in sorted(bins):
        if abs(b) <= 0.5:
            print('tail', b, 'long/short', bins[b])

# --- candidate full model
def model(v, hi=0.005, lo15=0.005):
    d15, e = exact_digits(v, 15)
    fr = Fraction(v)
    unit = Fraction(10) ** (e - 14)
    dec = int(d15.ljust(15, '0'))
    t = float((fr - dec * unit) / unit)
    k = len(d15)
    if float('%de%d' % (dec, e - 14)) != v:
        return dr(18)(v)
    if k <= 13 or (k == 14 and t < hi) or (k >= 15 and abs(t) < lo15):
        return (d15, e)
    return dr(18)(v)


if len(sys.argv) > 15:
    bad = []
    for v, o in zip(vals, out):
        w = parse(o)
        g = model(v)
        if g != w:
            bad.append((repr(v), o, g))
    print('model mismatches', len(bad), 'of', len(vals))
    for b in bad[:12]:
        print('mm', b)

# --- by k and signed tail (D15 round-trips)
if len(sys.argv) > 16:
    import collections
    tb = collections.defaultdict(lambda: [0, 0])
    for v, o in zip(vals, out):
        if float('%.14e' % v) != v:
            continue
        d15, e = exact_digits(v, 15)
        unit = Fraction(10) ** (e - 14)
        t = float((Fraction(v) - int(d15.ljust(15, '0')) * unit) / unit)
        w = parse(o)
        tb[(len(d15), round(t * 50) / 50)][w == (d15, e)] += 1
    for kk in (10, 12, 13, 14):
        print('k', kk, ' '.join('%.2f:%d/%d' % (b[1], tb[b][0], tb[b][1]) for b in sorted(tb) if b[0] == kk))

# --- ambiguous region by r = ulp / 17th-digit unit
if len(sys.argv) > 17:
    import collections
    rb = collections.defaultdict(lambda: [0, 0])
    for v, o in zip(vals, out):
        if float('%.14e' % v) == v:
            continue
        fr = Fraction(v)
        e = exact_digits(v, 17)[1]
        unit = Fraction(10) ** (e - 16)
        scaled = fr / unit
        fl = int(scaled)
        frac = float(scaled - fl)
        if not (0.45 <= frac < 0.5):
            continue
        w = parse(o)
        wi = int(w[0].ljust(17, '0'))
        r = float(Fraction(math.ulp(v)) / unit)
        rb[math.floor(math.log2(r) * 2) / 2][wi == fl + 1] += 1
    for b in sorted(rb):
        print('r~2^%.1f' % b, 'down/up', rb[b])

# --- model 2: ulp-dependent rounding threshold
def dig17(v, r_cut, th_lo, th_hi=0.45):
    fr = Fraction(v)
    e = exact_digits(v, 17)[1]
    unit = Fraction(10) ** (e - 16)
    scaled = fr / unit
    fl = int(scaled)
    frac = float(scaled - fl)
    r = float(Fraction(math.ulp(v)) / unit)
    th = th_lo if r < r_cut else th_hi
    q = fl + (1 if frac >= th else 0)
    s = str(q)
    if len(s) > 17:
        e += 1
        s = s[:17]
    return s.rstrip('0'), e


def model2(v, r_cut, th_lo):
    d15, e = exact_digits(v, 15)
    dec = int(d15.ljust(15, '0'))
    unit = Fraction(10) ** (e - 14)
    t = float((Fraction(v) - dec * unit) / unit)
    k = len(d15)
    if float('%de%d' % (dec, e - 14)) == v and (k <= 13 or (k == 14 and t < 0.005) or (k >= 15 and abs(t) < 0.005)):
        return (d15, e)
    return dig17(v, r_cut, th_lo)


if len(sys.argv) > 18:
    for r_cut in (2.0, 2.4, 2.83):
        for th_lo in (0.5, 0.49, 0.48):
            bad = sum(1 for v, o in zip(vals, out) if model2(v, r_cut, th_lo) != parse(o))
            print('model2 r_cut', r_cut, 'th_lo', th_lo, 'mismatch', bad, 'of', len(vals))
