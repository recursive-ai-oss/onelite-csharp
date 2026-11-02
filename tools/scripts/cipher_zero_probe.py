#!/usr/bin/env python3
"""Black-box probe: how does the SQLCipher shell treat an all-zero page 2..N of an encrypted database?

Environment: SQLCIPHER, WORK. For every page p >= 2 of a small database whose table was emptied (so most pages are free), copy the file, overwrite
page p with zeros (or with random bytes as a control) and print what integrity_check, cipher_integrity_check and a reuse of the free pages say.
"""
import os, shutil, subprocess, random

SQLCIPHER = os.environ["SQLCIPHER"].split()
WORK = os.environ["WORK"]
PS = 1024
os.makedirs(WORK, exist_ok=True)


def run(db, sql):
    p = subprocess.run(SQLCIPHER + [db, "PRAGMA key='k'; PRAGMA cipher_page_size=%d; " % PS + sql], capture_output=True, text=True)
    return (p.stdout.strip() + " | " + p.stderr.strip()).replace("\n", ";")[:110]


base = os.path.join(WORK, "base.db")
for f in (base,):
    if os.path.exists(f):
        os.remove(f)
run(base, "CREATE TABLE t(a,b); WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM s WHERE i<40) INSERT INTO t SELECT i, randomblob(2500) FROM s; DELETE FROM t;")
size = os.path.getsize(base)
n = size // PS
print("pages", n, run(base, "PRAGMA freelist_count;"))
rnd = random.Random(1)
for kind in ("zero", "random"):
    for p in range(2, min(n, 9) + 1):
        w = os.path.join(WORK, "w.db")
        shutil.copy(base, w)
        with open(w, "r+b") as fh:
            fh.seek((p - 1) * PS)
            fh.write(bytes(PS) if kind == "zero" else bytes(rnd.getrandbits(8) for _ in range(PS)))
        a = run(w, "PRAGMA integrity_check;")
        b = run(w, "PRAGMA cipher_integrity_check;")
        c = run(w, "WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM s WHERE i<10) INSERT INTO t SELECT i, randomblob(2500) FROM s; SELECT count(*) FROM t;")
        d = run(w, "VACUUM;")
        print(f"{kind} p{p}: integrity={a} | cipher_ic={b} | reuse={c} | vacuum={d}")
