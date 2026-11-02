#!/usr/bin/env python3
"""Two-way FTS5 file interop at scale.

For each detail mode (and page size) it builds an index of N documents twice, once with OneLite and once with the reference shell, and then
checks that
  * the shell accepts the file OneLite wrote (PRAGMA integrity_check, the FTS5 'integrity-check' command),
  * OneLite accepts the file the shell wrote (the same two checks),
  * Q seeded queries (MATCH forms, bm25 ranking, highlight) give identical rows on all four pairings of engine and file,
  * after OneLite changes the shell's file (inserts, deletes, 'optimize'), and the shell changes OneLite's file, both files still pass the
    checks and the queries agree again.

usage: fts_interop.py --sqlrun <OneLite.SqlRun.dll> --shell <sqlite3> [--docs 20000] [--queries 600] [--page-size 4096] [--details full,column,none]
                      [--encoding utf8|utf16le|utf16be] [--seed 1] [--work DIR]
"""
import argparse, os, random, shutil, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fts_diff as f

WORDS = None


def vocab(r, n=3000):
    syl = ["ka", "to", "mi", "ra", "ne", "so", "lu", "vi", "pa", "de", "go", "shu", "ba", "fe", "zo", "chi"]
    out = set()
    while len(out) < n:
        out.add("".join(r.choice(syl) for _ in range(r.randint(1, 4))))
    return sorted(out)


def zipf_word(r, words):
    return words[min(int(len(words) * r.random() ** 3), len(words) - 1)]


def make_docs(r, words, n, lo=0, hi=None):
    rows = []
    for i in range(1, n + 1):
        a = " ".join(zipf_word(r, words) for _ in range(r.randint(8, 40)))
        b = " ".join(zipf_word(r, words) for _ in range(r.randint(2, 12)))
        rows.append((i, a, b))
    return rows


def build_script(page_size, detail, rows):
    enc = {"utf16le": "PRAGMA encoding='UTF-16le';", "utf16be": "PRAGMA encoding='UTF-16be';"}.get(ENCODING, "")
    s = [enc + f"PRAGMA page_size={page_size};",
         f"CREATE VIRTUAL TABLE t USING fts5(a, b, detail={detail}, prefix='2 3');"]
    s.append("BEGIN;")
    for k in range(0, len(rows), 200):
        chunk = rows[k:k + 200]
        vals = ",".join(f"({i},'{a}','{b}')" for i, a, b in chunk)
        s.append(f"INSERT INTO t(rowid,a,b) VALUES {vals};")
    s.append("COMMIT;")
    s.append("DELETE FROM t WHERE rowid % 17 = 0;")
    s.append("UPDATE t SET b = b || ' extra' WHERE rowid % 23 = 0;")
    return "\n".join(s) + "\n"


def make_queries(r, words, detail, q):
    out = []
    w = lambda: zipf_word(r, words)
    for _ in range(q):
        k = r.randint(0, 9)
        if k == 0:
            m = w()
        elif k == 1:
            m = f"{w()} AND {w()}"
        elif k == 2:
            m = f"{w()} OR {w()}"
        elif k == 3:
            m = f"{w()} NOT {w()}"
        elif k == 4:
            m = w()[:2] + "*"
        elif k == 5:
            m = f'"{w()} {w()}"'
        elif k == 6:
            m = f"NEAR({w()} {w()}, {r.randint(1, 8)})"
        elif k == 7:
            m = f"a : {w()}" if detail != "none" else w()
        elif k == 8:
            m = f"({w()} OR {w()}) AND {w()}"
        else:
            m = f"{w()} {w()}"
        m = m.replace("'", "''")
        form = r.randint(0, 3)
        if form == 0:
            out.append(f"SELECT rowid FROM t WHERE t MATCH '{m}' ORDER BY rowid LIMIT 200;")
        elif form == 1:
            out.append(f"SELECT rowid, round(bm25(t), 6) FROM t WHERE t MATCH '{m}' ORDER BY rank, rowid LIMIT 10;")
        elif form == 2:
            out.append(f"SELECT rowid, highlight(t, 1, '[', ']') FROM t WHERE t MATCH '{m}' ORDER BY rowid LIMIT 5;")
        else:
            out.append(f"SELECT count(*) FROM t WHERE t MATCH '{m}';")
    out.append("SELECT count(*) FROM t;")
    return out


ENCODING = "utf8"   # utf8, utf16le or utf16be


def ours(sqlrun, db, script, timeout=1500):
    extra = {"utf16le": ["--utf16le"], "utf16be": ["--utf16be"]}.get(ENCODING, [])
    return f.run(["dotnet", sqlrun, db] + extra, script, timeout=timeout)


def shell(sh, db, script, timeout=1500):
    return f.run([sh, db], script, timeout=timeout)


def with_markers(queries):
    return "".join(f"{q}\nSELECT '#{i}';\n" for i, q in enumerate(queries))


CHECK = "INSERT INTO t(t) VALUES('integrity-check');\nPRAGMA integrity_check;\n"


def check_ok(out):
    lines = [l for l in out.strip().splitlines() if l.strip()]
    return lines == ["ok"]


def run_case(a, detail, page_size, rows, queries, work):
    tag = f"{detail}-p{page_size}-{ENCODING}"
    od, sd = os.path.join(work, tag + ".ours.db"), os.path.join(work, tag + ".shell.db")
    for p in (od, sd):
        for suffix in ("", "-wal", "-shm", "-journal"):
            if os.path.exists(p + suffix):
                os.remove(p + suffix)
    problems = []
    script = build_script(page_size, detail, rows)
    t0 = time.time()
    o = ours(a.sqlrun, od, script)
    s = shell(a.shell, sd, script)
    if o.strip() or s.strip():
        problems.append(f"build output: ours={o.strip()[:150]!r} shell={s.strip()[:150]!r}")
    build_s = time.time() - t0
    enc_have = shell(a.shell, od, "PRAGMA encoding;").strip()
    if enc_have != {"utf8": "UTF-8", "utf16le": "UTF-16le", "utf16be": "UTF-16be"}[ENCODING]:
        problems.append(f"encoding of our file is {enc_have}")
    ps = shell(a.shell, od, "PRAGMA page_size;").strip()
    if ps != str(page_size):
        problems.append(f"page size of our file is {ps}")

    def checks(stage):
        for who, db in (("ours file", od), ("shell file", sd)):
            r1 = shell(a.shell, db, CHECK)
            r2 = ours(a.sqlrun, db, CHECK)
            if not check_ok(r1):
                problems.append(f"{stage}: reference shell on {who}: {r1.strip()[:200]!r}")
            if not check_ok(r2):
                problems.append(f"{stage}: OneLite on {who}: {r2.strip()[:200]!r}")

    def compare(stage):
        qs = with_markers(queries)
        res = {
            "shell/shell": shell(a.shell, sd, qs),
            "shell/ours": shell(a.shell, od, qs),
            "ours/ours": ours(a.sqlrun, od, qs),
            "ours/shell": ours(a.sqlrun, sd, qs),
        }
        ref = f.segs(f.norm(res["shell/shell"]))
        for name, text in res.items():
            if name == "shell/shell":
                continue
            got = f.segs(f.norm(text))
            bad = [k for k in sorted(ref) if ref[k] != got.get(k)]
            if bad:
                k = bad[0]
                problems.append(f"{stage}: {name}: {len(bad)} of {len(queries)} queries differ, first #{k}: {queries[k][:120]} "
                                f"ref={' / '.join(ref[k])[:100]!r} got={' / '.join(got.get(k, ['-']))[:100]!r}")

    checks("fresh")
    compare("fresh")

    # each engine changes the other's file, then everything is compared again
    n = len(rows)
    extra = "\n".join(
        [f"INSERT INTO t(rowid,a,b) VALUES ({n + i},'{rows[i][1]}','{rows[i][2]} later');" for i in range(1, 300)]
        + ["DELETE FROM t WHERE rowid % 5 = 1;", "INSERT INTO t(t) VALUES('optimize');", "INSERT INTO t(t, rank) VALUES('merge', 4);"]) + "\n"
    ours(a.sqlrun, sd, extra)   # OneLite writes into the shell's file
    shell(a.shell, od, extra)   # the shell writes into OneLite's file
    checks("after cross-writes")
    compare("after cross-writes")
    return build_s, problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sqlrun", required=True)
    ap.add_argument("--shell", required=True)
    ap.add_argument("--docs", type=int, default=20000)
    ap.add_argument("--queries", type=int, default=600)
    ap.add_argument("--page-size", type=int, default=4096)
    ap.add_argument("--details", default="full,column,none")
    ap.add_argument("--encoding", default="utf8", choices=["utf8", "utf16le", "utf16be"])
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--work", default="fts_interop_work")
    a = ap.parse_args()
    global ENCODING
    ENCODING = a.encoding
    os.makedirs(a.work, exist_ok=True)
    r = random.Random(a.seed)
    words = vocab(r)
    rows = make_docs(r, words, a.docs)
    failed = 0
    for detail in a.details.split(","):
        qs = make_queries(random.Random(a.seed * 31 + len(detail)), words, detail, a.queries)
        t = time.time()
        build_s, problems = run_case(a, detail, a.page_size, rows, qs, a.work)
        status = "ok" if not problems else "FAIL"
        print(f"{status} detail={detail} page_size={a.page_size} encoding={ENCODING} docs={a.docs} queries={len(qs)} build={build_s:.0f}s total={time.time() - t:.0f}s")
        for p in problems:
            print("   " + p)
        failed += bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
