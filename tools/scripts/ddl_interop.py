#!/usr/bin/env python3
"""Schema changes in files written by either side: build a small database with the pinned sqlite3 shell (or with OneLite), run the same schema-change
script on a copy with OneLite and on a copy with the shell, then require that the OneLite-changed file passes PRAGMA integrity_check in the shell and
that its .dump equals the dump of the shell-changed file. Varies page size, text encoding, auto-vacuum mode and who wrote the base file.

usage: ddl_interop.py WORKDIR        env: ONELITE_HOME (default ~/onelite), ONELITE_SQLRUN (path of OneLite.SqlRun.dll, required)
"""
import itertools, os, subprocess, sys

home = os.environ.get("ONELITE_HOME", os.path.expanduser("~/onelite"))
sqlite = os.path.join(home, "oracles", "sqlite-3.53.4", "sqlite3")
sqlrun = os.environ.get("ONELITE_SQLRUN") or sys.exit("set ONELITE_SQLRUN to OneLite.SqlRun.dll")
work = sys.argv[1]
os.makedirs(work, exist_ok=True)

BASE = """
create table t(a integer primary key, b text, c real, d blob);
create table w(k text primary key, v) without rowid;
create table lg(id integer primary key, m);
with recursive n(i) as (select 1 union all select i+1 from n where i < %d)
  insert into t select i, 'row'||i||substr('abcdefghijklmnopqrstuvwxyz', 1, i%%26), i*1.5, zeroblob(i%%7*%d) from n;
with recursive n(i) as (select 1 union all select i+1 from n where i < %d) insert into w select 'k'||(i*7919%%10007), i from n;
create index tb on t(b);
create view v as select a, b from t where a > 3;
"""
CHANGES = [
    "alter table t rename column b to bb;",
    "alter table t add column e integer default 5;",
    "create trigger tr after insert on t begin insert into lg(m) values(new.a || ':' || new.bb); end;",
    "create trigger tu before update on t begin insert into lg(m) values(typeof(new.c)); end;",
    "insert into t(a, bb, c) values(100000, 'x', 2); update t set c = 3 where a < 20;",
    "create view v2 as select a, bb, e from t where e = 5;",
    "alter table t drop column d;",
    "alter table t rename to t1;",
    "create index te on t1(e, bb) where e is not null;",
    "analyze;",
    "drop view v2; drop trigger tu;",
    "delete from t1 where a % 3 = 0; delete from w where v % 2 = 0;",
    "vacuum;",
    "pragma auto_vacuum = %d; vacuum;",
    "alter table w rename to w2;",
    "analyze;",
]


def sh(db, sql):
    return subprocess.run([sqlite, db], input=sql, capture_output=True, text=True, errors="replace")


def ours(db, sql, opts):
    return subprocess.run(["dotnet", sqlrun, db, *opts], input=sql, capture_output=True, text=True, errors="replace")


fails = 0
runs = 0
for ps, enc, av, writer, rows in itertools.product((512, 1024, 4096, 65536), ("utf8", "utf16le", "utf16be"), (0, 1, 2), ("shell", "onelite"), (40, 900)):
    name = f"{writer}-p{ps}-{enc}-av{av}-r{rows}"
    d = os.path.join(work, name)
    os.makedirs(d, exist_ok=True)
    base, a, b = (os.path.join(d, x) for x in ("base.db", "shell.db", "ours.db"))
    for f in os.listdir(d):
        os.remove(os.path.join(d, f))
    e = {"utf8": "UTF-8", "utf16le": "UTF-16le", "utf16be": "UTF-16be"}[enc]
    opts = ["--page-size", str(ps)] + ({"utf8": [], "utf16le": ["--utf16le"], "utf16be": ["--utf16be"]}[enc]) + (["--auto-vacuum"] if av == 1 else ["--incremental"] if av == 2 else [])
    base_sql = BASE % (rows, 50 if ps < 4096 else 400, rows)
    if writer == "shell":
        sh(base, f"pragma page_size={ps}; pragma encoding='{e}'; pragma auto_vacuum={av};\n" + base_sql)
    else:
        ours(base, base_sql, opts)
    ic = sh(base, "pragma integrity_check").stdout.strip()
    if ic != "ok":
        print(f"SKIP {name}: base file is not ok in the shell ({ic[:60]})")
        fails += 1
        continue
    for dst in (a, b):
        with open(base, "rb") as s, open(dst, "wb") as o:
            o.write(s.read())
    script = "\n".join(c % ((av + 1) % 3,) if "%d" in c else c for c in CHANGES)
    ra = sh(a, script)
    rb = ours(b, script, [])
    runs += 1
    problems = []
    if sh(b, "pragma integrity_check").stdout.strip() != "ok":
        problems.append("integrity_check: " + sh(b, "pragma integrity_check").stdout.strip()[:100])
    if sh(a, ".dump").stdout != sh(b, ".dump").stdout:
        problems.append("dump differs")
    if sh(a, "pragma auto_vacuum").stdout != sh(b, "pragma auto_vacuum").stdout:
        problems.append("auto_vacuum differs")
    if ra.stderr.strip() != "" and "Error" in rb.stdout + rb.stderr and False:
        problems.append("errors differ")
    if "Error" in rb.stdout + rb.stderr:
        problems.append("OneLite reported: " + (rb.stdout + rb.stderr).strip().splitlines()[0][:100])
    if problems:
        fails += 1
        print(f"FAIL {name}: {'; '.join(problems)}")
print(f"ddl interop: {runs} runs, {fails} problems")
sys.exit(1 if fails else 0)
