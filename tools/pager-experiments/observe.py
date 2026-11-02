#!/usr/bin/env python3
"""Black-box observation of the reference shell's commit sequence and journal contents.

Runs the pinned `sqlite3` under strace for one small write transaction per
journal_mode x synchronous x workload and prints the file-changing calls that
happen between two marker files (so connection setup is left out). Also dumps
the journal a PERSIST-mode commit leaves behind, to see which pages are journaled
(fileformat2, "The Rollback Journal").

usage: observe.py SQLITE3 WORKDIR [sequences|journal|spill]
"""
import os, re, struct, subprocess, sys, shutil

sqlite3, work = sys.argv[1], sys.argv[2]
what = sys.argv[3] if len(sys.argv) > 3 else "sequences"
os.makedirs(work, exist_ok=True)
os.chdir(work)


def run(db, script, trace=None, extra=()):
    cmd = [sqlite3, db]
    if trace:
        cmd = ["strace", "-f", "-o", trace, "-e",
               "trace=openat,pwrite64,write,fsync,fdatasync,ftruncate,unlink,rename"] + cmd
    return subprocess.run(cmd, input=script, text=True, capture_output=True)


def condense(trace):
    lines = open(trace).read().splitlines()
    start = next((i for i, l in enumerate(lines) if "marker_begin" in l and "openat" in l), 0)
    end = next((i for i, l in enumerate(lines) if "marker_end" in l and "openat" in l), len(lines))
    fds, out = {}, []
    for l in lines[:start]:
        m = re.search(r'openat\(.*?"([^"]+)".*= (\d+)', l)
        if m:
            fds[m.group(2)] = m.group(1)
    for l in lines[start + 1:end]:
        l = re.sub(r'^\d+\s+', '', l)
        m = re.match(r'openat\(.*?"([^"]+)", ([A-Z_|]+).*= (-?\d+)', l)
        if m:
            fds[m.group(3)] = m.group(1)
            out.append(f"open {m.group(1)} {m.group(2).replace('O_CLOEXEC','').replace('O_NOFOLLOW','').strip('|')}")
            continue
        m = re.match(r'(pwrite64|write)\((\d+), .*?, (\d+)(?:, (\d+))?\) = (\d+)', l)
        if m:
            name = fds.get(m.group(2), "fd" + m.group(2))
            if name.startswith("/dev"):
                continue
            out.append(f"{m.group(1)} {name} off={m.group(4)} len={m.group(3)}")
            continue
        m = re.match(r'(fsync|fdatasync|ftruncate)\((\d+)(?:, (\d+))?\)', l)
        if m:
            out.append(f"{m.group(1)} {fds.get(m.group(2), 'fd' + m.group(2))}" + (f" ->{m.group(3)}" if m.group(3) else ""))
            continue
        m = re.match(r'unlink\("([^"]+)"', l)
        if m:
            out.append(f"unlink {m.group(1)}")
    # merge runs of identical shape
    res = []
    for o in out:
        if res and res[-1][0] == o:
            res[-1][1] += 1
        else:
            res.append([o, 1])
    return [f"{o} x{n}" if n > 1 else o for o, n in res]


def fresh(db, page_size=4096, rows=300):
    for s in ("", "-journal", "-wal", "-shm"):
        if os.path.exists(db + s):
            os.remove(db + s)
    script = f"pragma page_size={page_size};create table t(a integer primary key, b text);create index tb on t(b);\n"
    script += f"with recursive c(x) as (select 1 union all select x+1 from c where x<{rows}) insert into t select x, hex(randomblob(60)) from c;\n"
    r = run(db, script)
    assert r.returncode == 0, r.stderr


def sequences():
    for mode in ("delete", "truncate", "persist", "memory", "off"):
        for sync in ("off", "normal", "full", "extra"):
            fresh("exp1.db")
            script = f"pragma journal_mode={mode};pragma synchronous={sync};select count(*) from t;\n.output marker_begin\n.output stdout\nbegin;insert into t values(100000,'x');commit;\n.output marker_end\n.output stdout\n"
            r = run("exp1.db", script, "exp1.trace")
            print(f"== journal_mode={mode} synchronous={sync}")
            for l in condense("exp1.trace"):
                if "marker" in l:
                    continue
                print("  ", l)


def parse_journal(path, ps):
    d = open(path, "rb").read()
    magic = d[:8]
    nrec, nonce, init, sector, pgsz = struct.unpack(">IIIII", d[8:28])
    print(f"   journal {len(d)} bytes magic={magic.hex()} nRec={nrec} nonce={nonce:#x} initialPages={init} sector={sector} pageSize={pgsz}")
    off = 512
    pages = []
    while off + 8 + ps <= len(d):
        pg = struct.unpack(">I", d[off:off + 4])[0]
        pages.append(pg)
        off += 4 + ps + 4
    print(f"   records ({len(pages)}): {pages[:40]}{'...' if len(pages) > 40 else ''}; trailing bytes {len(d) - off}")
    return nrec, pages


def journal():
    # which pages are journaled? (fileformat2: the journal holds the original content of pages that existed at start)
    cases = {
        "update-in-place": "update t set b='y' where a=5;",
        "append-extends-file": "insert into t select 200000+x, hex(randomblob(60)) from (with recursive c(x) as (select 1 union all select x+1 from c where x<300) select x from c);",
        "delete-frees-pages": "delete from t where a>100;",
        "insert-after-delete-reuses-free": "delete from t where a>100;",
    }
    for name, sql in cases.items():
        fresh("exp2.db")
        if name.startswith("insert-after"):
            run("exp2.db", sql)
            sql = "insert into t select 300000+x, hex(randomblob(60)) from (with recursive c(x) as (select 1 union all select x+1 from c where x<100) select x from c);"
        before = os.path.getsize("exp2.db")
        script = f"pragma journal_mode=persist;begin;{sql}commit;\n"
        r = run("exp2.db", script)
        print(f"== {name}: db before {before // 4096} pages, after {os.path.getsize('exp2.db') // 4096}")
        if not os.path.exists("exp2.db-journal"):
            print("   no journal"); continue
        nrec, pages = parse_journal("exp2.db-journal", 4096)
        # PERSIST zeroes the header at commit, so rerun with a kill-free copy: use journal_mode=persist + trace of header? header is zero now.
    print("(PERSIST zeroes the header after commit; record bodies stay readable.)")


def spill():
    # Spill: a transaction larger than the cache, with and without a reader holding SHARED on the file (rollback mode)
    import time
    fresh("exp7.db", 1024, 2000)
    for reader in (False, True):
        r = None
        p = None
        if reader:
            p = subprocess.Popen([sqlite3, "exp7.db"], stdin=subprocess.PIPE, text=True, stdout=subprocess.PIPE)
            p.stdin.write("begin;select count(*) from t;\n.shell sleep 5\n"); p.stdin.flush()
            time.sleep(0.5)
        script = "pragma cache_size=10;begin;update t set b=hex(randomblob(60));commit;select 'done';\n"
        r = run("exp7.db", script, "exp7.trace")
        print(f"== reader={reader}: rc={r.returncode} out={r.stdout.strip()!r} err={r.stderr.strip()!r}")
        if p:
            p.kill(); p.wait()
        fresh("exp7.db", 1024, 2000)


{"sequences": sequences, "journal": journal, "spill": spill}[what]()
