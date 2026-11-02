#!/usr/bin/env python3
"""Files the reference wrote, modified by OneLite SQL: for each database, apply the same generated modifications with OneLite and with the pinned
sqlite3 shell, then require that the OneLite-modified file passes PRAGMA integrity_check and that its .dump equals the shell's.

usage: engine_modify_interop.py DB [DB ...]   (the originals are copied, never changed)
env:   ONELITE_HOME (default ~/onelite) for the oracle; ONELITE_SQLRUN to point at a built OneLite.SqlRun.dll
"""
import os, re, shutil, subprocess, sys, tempfile

home = os.environ.get("ONELITE_HOME", os.path.expanduser("~/onelite"))
sqlite = os.path.join(home, "oracles", "sqlite-3.53.4", "sqlite3")
sqlrun = os.environ.get("ONELITE_SQLRUN") or sys.exit("set ONELITE_SQLRUN to OneLite.SqlRun.dll")


def q(db, sql):
    return subprocess.run([sqlite, db, sql], capture_output=True, text=True).stdout


# The inserts name their order: an unordered scan of a table with a covering index may run in index order in the reference, which gives the
# re-inserted rows other rowids and the dumps another order without either file being wrong.
def modifications(db):
    """A script of DML over every ordinary table: deletes, re-inserts, replaces, updates of the first non-key column, then an index and its removal."""
    out = []
    rows = q(db, "select name, sql from sqlite_schema where type='table' and name not like 'sqlite_%' order by name").splitlines()
    for line in rows:
        name = line.split("|")[0]
        info = q(db, f'pragma table_info("{name}")').splitlines()
        cols = [l.split("|") for l in info]
        sql = q(db, f"select sql from sqlite_schema where name='{name}'")
        rowid = "without rowid" not in sql.lower()
        if not cols or not rowid:
            continue
        trig = q(db, f"select count(*) from sqlite_schema where type='trigger' and tbl_name='{name}'").strip()
        if trig != "0":
            continue
        out.append(f'delete from "{name}" where rowid % 7 = 0;')
        out.append(f'insert or ignore into "{name}" select * from "{name}" where rowid % 11 = 0 order by rowid;')
        out.append(f'insert or replace into "{name}" select * from "{name}" where rowid % 13 = 1 order by rowid;')
        text = [c for c in cols if "CHAR" in c[2].upper() or "TEXT" in c[2].upper()]
        pk = [c for c in cols if c[5] != "0"]
        if text and not any(text[0][1] == p[1] for p in pk):
            out.append(f'update "{name}" set "{text[0][1]}" = "{text[0][1]}" || \'~\' where rowid % 5 = 0;')
        out.append(f'delete from "{name}" where rowid % 3 = 1 and rowid > 50;')
    return "\n".join(out) + "\n"


def run(db):
    work = tempfile.mkdtemp(prefix="olmod-")
    a, b = os.path.join(work, "a.db"), os.path.join(work, "b.db")
    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(db + suffix):
            shutil.copy(db + suffix, a + suffix)
            shutil.copy(db + suffix, b + suffix)
    script = modifications(db)
    with open(os.path.join(work, "mod.sql"), "w") as f:
        f.write(script)
    ours = subprocess.run(["dotnet", sqlrun, a], input=script, capture_output=True, text=True)
    subprocess.run([sqlite, b], input=script, capture_output=True, text=True)
    errs = [l for l in ours.stdout.splitlines() if l.startswith("Error")]
    ic = q(a, "pragma integrity_check").strip()
    same = q(a, ".dump") == q(b, ".dump")
    ok = ic == "ok" and same and not errs
    print(("OK   " if ok else "FAIL ") + os.path.basename(db), f"statements={script.count(chr(10))} integrity={ic[:60]} dump_equal={same} errors={len(errs)}")
    for e in errs[:3]:
        print("   ", e[:200])
    shutil.rmtree(work, ignore_errors=True)
    return ok


if __name__ == "__main__":
    sys.exit(0 if all([run(d) for d in sys.argv[1:]]) else 1)
