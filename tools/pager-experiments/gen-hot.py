#!/usr/bin/env python3
"""Leaves hot rollback journals behind by killing the pinned `sqlite3` partway through a commit.

For each case it builds a database, then runs a write transaction under
`strace -e inject=...:signal=KILL:when=N`, which delivers SIGKILL to the shell at its Nth
write/sync/truncate/unlink of the commit (a black-box way to stop it at a chosen point).
Cases that leave a non-empty `-journal` next to the database are kept as
OUT/<case>/{db,db-journal}; `expected.db` is what the pinned shell itself makes of a copy
(it recovers on open), for byte comparison with our pager's recovery.

usage: gen-hot.py SQLITE3 OUT COUNT [seed]
"""
import os, random, shutil, subprocess, sys

sqlite3, out, count = sys.argv[1], sys.argv[2], int(sys.argv[3])
seed = int(sys.argv[4]) if len(sys.argv) > 4 else 1
rnd = random.Random(seed)
os.makedirs(out, exist_ok=True)


def sh(db, sql, **kw):
    return subprocess.run([sqlite3, os.path.basename(db)], input=sql, text=True, capture_output=True, cwd=os.path.dirname(os.path.abspath(db)), **kw)


def build(db, page_size, reserved, rows):
    for s in ("", "-journal", "-wal", "-shm"):
        if os.path.exists(db + s):
            os.remove(db + s)
    sql = f"pragma page_size={page_size};\ncreate table t(a integer primary key, b text);create index tb on t(b);\n"
    sql += f"with recursive c(x) as (select 1 union all select x+1 from c where x<{rows}) insert into t select x, hex(randomblob(30)) from c;\n"
    sql += f".filectrl reserve_bytes {reserved}\nvacuum;\n" if reserved else ""
    r = sh(db, sql)
    assert r.returncode == 0, r.stderr


def attempt(case, mode, ps, reserved, sync, kind, nth, tx, sysc):
    # The case is built where it will stay: a journal that names a super-journal records its absolute path.
    work = os.path.join(out, case)
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    db = os.path.join(work, "db")
    build(db, ps, reserved, 400 if ps <= 4096 else 120)
    if kind == "multi":
        build(os.path.join(work, "db2"), ps, reserved, 100)
    shutil.copy(db, os.path.join(work, "pre.db"))
    script = f"pragma journal_mode={mode};pragma synchronous={sync};pragma cache_size={rnd.choice([5, 10, 2000])};\n"
    if kind == "multi":
        script += "attach 'db2' as d2;\n"
    script += tx
    cmd = ["strace", "-f", "-o", "/dev/null", "-e", f"inject={sysc}:signal=KILL:when={nth}", sqlite3, db]
    p = subprocess.run(cmd, input=script, text=True, capture_output=True, cwd=work)
    killed = p.returncode in (-9, 137)
    j = db + "-journal"
    if not killed or not os.path.exists(j) or os.path.getsize(j) == 0:
        shutil.rmtree(work, ignore_errors=True)
        return False
    dest = work
    for f in ("trace",):
        if os.path.exists(os.path.join(work, f)):
            os.remove(os.path.join(work, f))
    # what the pinned shell makes of a copy
    rec = os.path.join(out, "_r")
    shutil.rmtree(rec, ignore_errors=True)
    shutil.copytree(dest, rec)
    sh(os.path.join(rec, "db"), "select count(*) from sqlite_master;" if kind != "multi" else "attach 'db2' as d2; select count(*) from sqlite_master; select count(*) from d2.sqlite_master;")
    shutil.copy(os.path.join(rec, "db"), os.path.join(dest, "expected.db"))
    if os.path.exists(os.path.join(rec, "db2")):
        shutil.copy(os.path.join(rec, "db2"), os.path.join(dest, "expected2.db"))
    open(os.path.join(dest, "meta.txt"), "w").write(f"{mode} {ps} {reserved} {sync} {kind} {sysc}={nth}\n")
    return True


kept = 0
tries = 0
while kept < count and tries < count * 40:
    tries += 1
    mode = rnd.choice(["delete", "truncate", "persist"])
    ps = rnd.choice([512, 4096, 65536])
    reserved = rnd.choice([0, 0, 8, 32])
    sync = rnd.choice(["full", "normal", "off", "extra"])
    kind = "multi" if rnd.random() < float(os.environ.get("MULTI", "0.15")) and ps <= 4096 else "single"
    sysc = rnd.choice(["pwrite64", "pwrite64", "fdatasync", "unlink", "ftruncate"])
    nth = rnd.randint(1, 60 if sysc == "pwrite64" else 4)
    tx = "begin;update t set b=hex(randomblob(30)) where a%3=0;delete from t where a%7=1;insert into t select 100000+a, b from t where a%5=0;commit;\n"
    if kind == "multi":
        tx = "begin;update t set b=hex(randomblob(30)) where a%3=0;update d2.t set b='z' where a%2=0;commit;\n"
    elif mode == "persist" and rnd.random() < 0.5:
        # a second transaction reuses the journal the first one left behind, with stale segments after it
        tx = tx + "begin;update t set b=hex(randomblob(30)) where a%11=0;commit;\n"
        nth = rnd.randint(1, 140) if sysc == "pwrite64" else nth
    case = f"c{kept:04d}"
    if attempt(case, mode, ps, reserved, sync, kind, nth, tx, sysc):
        kept += 1
print(f"kept {kept} of {tries} attempts")
shutil.rmtree(os.path.join(out, "_r"), ignore_errors=True)
