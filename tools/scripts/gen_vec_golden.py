#!/usr/bin/env python3
"""Writes testdata/vec/scalar_golden.jsonl: what a released sqlite-vec loadable extension, run in the pinned sqlite3 shell, answers for every
statement of testdata/vec/scalar_cases.sql (one statement per line). The tests check OneLite against it.

usage: gen_vec_golden.py <sqlite3 shell> <sqlite-vec loadable extension without suffix> <cases.sql> <out.jsonl>
"""
import json
import subprocess
import sys


def tokens(line):
    """Splits one row printed in the shell's quote mode into its cells."""
    cells, cur, quoted, i = [], '', False, 0
    while i < len(line):
        c = line[i]
        if quoted:
            cur += c
            if c == "'":
                if i + 1 < len(line) and line[i + 1] == "'":
                    cur += "'"
                    i += 1
                else:
                    quoted = False
        elif c == "'":
            quoted = True
            cur += c
        elif c == ',':
            cells.append(cur)
            cur = ''
        else:
            cur += c
        i += 1
    cells.append(cur)
    return cells


def main():
    shell, ext, cases, out = sys.argv[1:5]
    version = subprocess.run([shell, '-cmd', '.load ' + ext, ':memory:', 'select sqlite_version(), vec_version()'], capture_output=True, text=True).stdout.strip()
    with open(out, 'w') as o:
        o.write(json.dumps({'oracle': version}) + '\n')
        for sql in open(cases):
            sql = sql.strip()
            if not sql:
                continue
            p = subprocess.run([shell, '-cmd', '.load ' + ext, '-cmd', '.mode quote', ':memory:', sql], capture_output=True, text=True)
            err = p.stderr.strip()
            if err:
                msg = err.split(':', 1)[1].strip() if ':' in err else err
                msg = msg.split(' command line argument: ', 1)[-1]
                o.write(json.dumps({'sql': sql, 'error': msg.split('\n')[0]}) + '\n')
            else:
                rows = [tokens(r) for r in p.stdout.splitlines()]
                o.write(json.dumps({'sql': sql, 'rows': rows}) + '\n')


main()
