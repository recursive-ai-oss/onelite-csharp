#!/usr/bin/env python3
"""Runs each case of a case file through the pinned sqlite3 shell and through OneLite's SqlRun, on fresh database files, and compares the rows
and the error messages. Cases are separated by lines holding only %%. A first line starting with `-- opts:` passes options to SqlRun (the shell
gets none: page size and the like are set by SQL in the case). Rows are compared as printed in list mode; an error line is reduced to its message.

usage: pragma_diff.py CASES [--sqlite3 PATH] [--sqlrun 'dotnet X.dll'] [--work DIR] [-v]
Environment defaults: ONELITE_SQLITE3, ONELITE_SQLRUN, ONELITE_WORK.
"""
import argparse, os, re, shlex, subprocess, sys, shutil

SETUP_TEXT = "create table t(a integer primary key, b text not null default 'x', c real unique);\ncreate index ib on t(b, c desc);\ncreate table p(x, y references t(a));\ninsert into t values (1,'a',1.5),(2,'b',2.5);\n"

def norm_err(line):
    m = re.match(r'^(?:Parse error|Runtime error|Error)(?: near line \d+)?: (.*)$', line)
    if not m:
        return line
    msg = m.group(1)
    msg = re.sub(r' \(\d+\)$', '', msg)          # the shell appends "(code)"
    msg = re.sub(r' \[\d+\].*$', '', msg)        # SqlRun appends " [extended] detail"
    msg = re.sub(r'^in prepare, ', '', msg)
    return 'ERR ' + msg

def norm(out, err):
    out = out.replace('ours.db', 'ref.db')
    lines = [l for l in out.split('\n')]
    errs = [norm_err(l) for l in err.split('\n') if l.strip()]
    res = []
    for l in lines:
        if res and res[-1].startswith('ERR') and l.startswith(' ') and (not l.strip().startswith('^---') or True):
            continue
        if re.match(r'^(Error|Parse error|Runtime error)', l):
            res.append(norm_err(l))
        elif l != '':
            res.append(l)
    return res + [e for e in errs if e.startswith('ERR')]

def run(cmd, db, sql, cwd):
    p = subprocess.run(cmd + [db], input=sql, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, cwd=cwd, timeout=120)
    return norm(p.stdout, '')

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cases')
    ap.add_argument('--sqlite3', default=os.environ.get('ONELITE_SQLITE3', 'sqlite3'))
    ap.add_argument('--sqlrun', default=os.environ.get('ONELITE_SQLRUN'))
    ap.add_argument('--work', default=os.environ.get('ONELITE_WORK', '.'))
    ap.add_argument('-v', action='store_true')
    a = ap.parse_args()
    blocks = [b.strip('\n') for b in open(a.cases).read().split('\n%%\n')]
    os.makedirs(a.work, exist_ok=True)
    ok = bad = 0
    per = {}
    for i, b in enumerate(blocks):
        if not b.strip():
            continue
        opts = []
        if b.startswith('-- opts:'):
            first, _, b = b.partition('\n')
            opts = shlex.split(first[len('-- opts:'):])
        d = os.path.join(a.work, 'c%d' % i)
        shutil.rmtree(d, ignore_errors=True)
        os.makedirs(d)
        ref = run([a.sqlite3, '-header'], 'ref.db', b + '\n', d)
        shutil.rmtree(d, ignore_errors=True)
        os.makedirs(d)
        # SqlRun takes the database first, then options
        p = subprocess.run(shlex.split(a.sqlrun) + ['ours.db', '--header'] + opts, input=b + '\n', capture_output=True, text=True, cwd=d, timeout=120)
        got = norm(p.stdout, p.stderr)
        shutil.rmtree(d, ignore_errors=True)
        tag = re.match(r'-- pragma: (\w+)', b)
        tag = tag.group(1) if tag else '-'
        st = per.setdefault(tag, [0, 0])
        if ref == got:
            ok += 1
            st[0] += 1
        else:
            bad += 1
            st[1] += 1
            if a.v: print('--- case %d DIFFERS\n%s\n  sqlite3: %s\n  onelite: %s' % (i, re.sub(r'^-- pragma: \w+\n', '', b).replace(SETUP_TEXT, '[setup]\n'), ref, got))
    for k, (p, f) in sorted(per.items()):
        print('%-28s cases %3d pass %3d fail %3d' % (k, p + f, p, f))
    print('cases %d pass %d fail %d' % (ok + bad, ok, bad))
    return 1 if bad else 0

if __name__ == '__main__':
    sys.exit(main())
