#!/usr/bin/env python3
"""Summarises a .trx test result file: outcome counts, then failed tests grouped by the first line of their message.

  trx_failures.py results.trx [--names] [--limit N]

Digits in messages are folded to N so that repeats of one failure group together.
"""
import collections
import re
import sys


def main() -> None:
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    names = '--names' in sys.argv
    limit = 40
    if '--limit' in sys.argv:
        limit = int(sys.argv[sys.argv.index('--limit') + 1])
        args = [a for a in args if a != str(limit)]
    text = open(args[0], encoding='utf-8-sig').read()
    results = re.findall(r'<UnitTestResult [^>]*?testName="([^"]*)"[^>]*?outcome="(\w+)"[^>]*?(?:/>|>(.*?)</UnitTestResult>)', text, re.S)
    print(len(results), dict(collections.Counter(o for _, o, _ in results)))
    groups = collections.defaultdict(list)
    for name, outcome, body in results:
        if outcome != 'Failed':
            continue
        m = re.search(r'<Message>(.*?)</Message>', body or '', re.S)
        msg = (m.group(1) if m else '').strip().split('\n')[0][:140]
        groups[re.sub(r'\d+', 'N', msg)].append(name)
    for msg, tests in sorted(groups.items(), key=lambda kv: -len(kv[1]))[:limit]:
        print(f'{len(tests):4d}  {msg}')
        if names:
            for t in tests[:6]:
                print('        ' + t.replace('Microsoft.Data.Sqlite.', '')[:120])


if __name__ == '__main__':
    main()
