#!/usr/bin/env python3
"""Groups the differences in querydiff fuzz logs by the functions in the query, so a run reads as a short list.
usage: sumdiff.py LOG... [--n 3] [--w 220]"""
import re, sys, collections

args = sys.argv[1:]
n_ex, width = 2, 200
logs = []
i = 0
while i < len(args):
    if args[i] == '--n':
        n_ex = int(args[i + 1]); i += 2
    elif args[i] == '--w':
        width = int(args[i + 1]); i += 2
    else:
        logs.append(args[i]); i += 1

FUNCS = set('''length lower upper typeof coalesce ifnull substr substring abs hex quote char concat concat_ws format printf glob iif if instr changes
last_insert_rowid total_changes likelihood likely unlikely like load_extension ltrim rtrim trim max min nullif octet_length random randomblob replace round
sign soundex sqlite_compileoption_get sqlite_compileoption_used sqlite_source_id sqlite_version unhex unicode unistr unistr_quote zeroblob acos acosh asin asinh
atan atan2 atanh ceil ceiling cos cosh degrees exp floor ln log log10 log2 mod pi pow power radians sin sinh sqrt tan tanh trunc date time datetime julianday
unixepoch strftime timediff cast collate between in case glob escape'''.split())

groups = collections.OrderedDict()
for path in logs:
    txt = open(path, errors='replace').read()
    for rec in re.split(r'(?m)^DIFF ', txt)[1:]:
        lines = rec.split('\n')
        sql = lines[1].strip() if len(lines) > 1 else ''
        detail = ' '.join(l.strip() for l in lines[2:] if l.strip() and not l.startswith(('fuzz seed', 'unsupported')))
        found = sorted({w for w in re.findall(r'[a-z_0-9]+', sql.lower()) if w in FUNCS})
        kind = re.sub(r'\d+', 'N', detail[:18])
        key = (kind, tuple(found[:6]))
        g = groups.setdefault(key, [0, []])
        g[0] += 1
        if len(g[1]) < n_ex:
            g[1].append((sql, detail))
total = sum(g[0] for g in groups.values())
print('differences', total, 'groups', len(groups))
for key, (cnt, ex) in sorted(groups.items(), key=lambda kv: -kv[1][0])[:40]:
    print('--', cnt, key[0], ','.join(key[1]))
    for sql, detail in ex:
        print('   Q', sql[:width])
        print('   D', detail[:width])
