#!/usr/bin/env python3
"""Shows each mismatch written by gen --out with the text around the earlier of the two error offsets: ctx.py <file> [max]."""
import re, sys

L = open(sys.argv[1]).read().split('\n')
max_n = int(sys.argv[2]) if len(sys.argv) > 2 else 15
i = n = 0
while i < len(L) and n < max_n:
    if L[i].startswith('['):
        m = re.search(r'off=(\d+)', L[i + 1])
        m2 = re.search(r'off=(\d+)', L[i + 2])
        has_full = i + 3 < len(L) and L[i + 3].startswith('    full:')
        full = L[i + 3][10:].replace('\\n', '\n') if has_full else L[i]
        b = full.encode()
        offs = [int(x.group(1)) for x in (m, m2) if x]
        o = min(offs) if offs else 0
        print(L[i].split(']')[0], '|', L[i + 1].strip()[:80], '|', L[i + 2].strip()[:50])
        print('   ...', b[max(0, o - 90):o + 30].decode(errors='replace').replace('\n', ' '))
        n += 1
        i += 4 if has_full else 3
    else:
        i += 1
