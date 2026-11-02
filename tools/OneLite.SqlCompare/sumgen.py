#!/usr/bin/env python3
"""Adds up the per-kind tables printed by `gen` over several logs: sumgen.py log...

Columns: generated-and-unique, native prepares, native syntax errors, native other errors (parsed, then failed), we accept, mismatches.
"""
import re
import sys

rows = {}
extra = {'round trips': 0, 'semantic checks': 0, 'masked': 0, 'mismatches': 0}
gen = uniq = 0
for path in sys.argv[1:]:
    for line in open(path, errors='replace'):
        m = re.match(r'gen seed (\d+): (\d+) generated, (\d+) unique', line)
        if m:
            gen += int(m.group(2))
            uniq += int(m.group(3))
            continue
        m = re.match(r'\s{2}(\S+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)(.*)$', line)
        if m and m.group(1) != 'TOTAL':
            r = rows.setdefault(m.group(1), [0] * 6)
            for i in range(6):
                r[i] += int(m.group(2 + i))
        m = re.match(r'\s{2}TOTAL.*round trips (\d+), semantic checks (\d+), masked (\d+), mismatches (\d+)', line)
        if m:
            for k, v in zip(extra, m.groups()):
                extra[k] += int(v)

print(f'generated {gen}, unique statements compared {uniq}')
print(f'{"kind":<24}{"compared":>10}{"prepares":>10}{"syntax":>9}{"other":>9}{"we accept":>10}{"mismatch":>9}')
tot = [0] * 6
for k, r in sorted(rows.items()):
    print(f'{k:<24}' + ''.join(f'{v:>{w}}' for v, w in zip(r, (10, 10, 9, 9, 10, 9))))
    for i in range(6):
        tot[i] += r[i]
print(f'{"TOTAL":<24}' + ''.join(f'{v:>{w}}' for v, w in zip(tot, (10, 10, 9, 9, 10, 9))))
print(extra)
