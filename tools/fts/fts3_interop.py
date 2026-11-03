#!/usr/bin/env python3
"""Two-way FTS3/FTS4 file interop at scale.

For each option combination, page size and text encoding it builds a table of N documents twice, once with OneLite and once with the reference
shell, and then checks that
  * the shell accepts the file OneLite wrote (PRAGMA integrity_check and the FTS 'integrity-check' command),
  * OneLite accepts the file the shell wrote (the same two checks),
  * Q seeded queries (MATCH forms, offsets, snippet, matchinfo) give identical rows on all four pairings of engine and file,
  * after each engine has changed the other's file (inserts, deletes, 'optimize', 'merge=2,3') both files still pass the checks and the
    queries agree again.

usage: fts3_interop.py --sqlrun <OneLite.SqlRun.dll> --shell <sqlite3> [--docs 3000] [--queries 300] [--seed 1] [--work DIR] [--only substring] [--matrix default|full]
"""
import argparse, os, random, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fts_diff as f
import fts_interop as base

# (label, module, column options, contentless/external, can modify rows)
COMBOS = [
    ("fts3", "fts3", "", True),
    ("fts3-porter", "fts3", "tokenize=porter", True),
    ("fts4", "fts4", "", True),
    ("fts4-prefix-matchinfo", "fts4", "prefix='2,3', matchinfo=fts3", True),
    ("fts4-unicode61-desc", "fts4", "tokenize=unicode61, order=desc", True),
    ("fts4-notindexed", "fts4", "notindexed=b", True),
    ("fts4-langid", "fts4", "languageid='lid'", True),
    ("fts4-contentless", "fts4", "content=''", False),
]
CONFIGS = [(4096, "utf8"), (1024, "utf16le"), (512, "utf8"), (8192, "utf16be")]
# --matrix full: every page size against every text encoding
FULL_CONFIGS = [(ps, enc) for ps in (512, 1024, 2048, 4096, 8192, 16384, 32768, 65536) for enc in ("utf8", "utf16le", "utf16be")]


def build_script(page_size, module, opts, rows, modify):
    enc = {"utf16le": "PRAGMA encoding='UTF-16le';", "utf16be": "PRAGMA encoding='UTF-16be';"}.get(base.ENCODING, "")
    s = [enc + f"PRAGMA page_size={page_size};", f"CREATE VIRTUAL TABLE t USING {module}(a, b{', ' + opts if opts else ''});", "BEGIN;"]
    for k in range(0, len(rows), 200):
        vals = ",".join(f"({i},'{a}','{b}')" for i, a, b in rows[k:k + 200])
        s.append(f"INSERT INTO t(docid,a,b) VALUES {vals};")
    s.append("COMMIT;")
    if modify:
        s.append("DELETE FROM t WHERE docid % 17 = 0;")
        s.append("UPDATE t SET b = b || ' extra' WHERE docid % 23 = 0;")
    return "\n".join(s) + "\n"


def make_queries(r, words, notindexed, q):
    out = []
    w = lambda: base.zipf_word(r, words)
    for _ in range(q):
        k = r.randint(0, 9)
        m = [lambda: w(), lambda: f"{w()} {w()}", lambda: f"{w()} OR {w()}", lambda: f"{w()} -{w()}", lambda: w()[:2] + "*",
             lambda: f'"{w()} {w()}"', lambda: f"{w()} NEAR/{r.randint(1, 8)} {w()}", lambda: f"a:{w()}", lambda: f"{w()} {w()} OR {w()}",
             lambda: f"{w()} NEAR {w()}"][k]().replace("'", "''")
        form = r.randint(0, 4)
        if form == 0:
            out.append(f"SELECT docid FROM t WHERE t MATCH '{m}' ORDER BY docid LIMIT 200;")
        elif form == 1:
            out.append(f"SELECT docid, offsets(t) FROM t WHERE t MATCH '{m}' ORDER BY docid LIMIT 5;")
        elif form == 2:
            out.append(f"SELECT docid, snippet(t, '[', ']', '..', -1, 8) FROM t WHERE t MATCH '{m}' ORDER BY docid LIMIT 5;")
        elif form == 3:
            # the hit totals ('x') of a query with several phrases depend on how much of the later doclists the reference reads, which
            # depends on the segments of the file it reads (docs/fts3.md), so they are compared for one-phrase queries only
            fmt = "pcnal" if " " in m else "pcnalx"
            out.append(f"SELECT docid, hex(matchinfo(t, '{fmt}')) FROM t WHERE t MATCH '{m}' ORDER BY docid LIMIT 5;")
        else:
            out.append(f"SELECT count(*) FROM t WHERE t MATCH '{m}';")
    out.append("SELECT count(*) FROM t;")
    return out


CHECK = "INSERT INTO t(t) VALUES('integrity-check');\nPRAGMA integrity_check;\n"


def run_case(a, label, module, opts, modify, page_size, rows, queries, work):
    tag = f"{label}-p{page_size}-{base.ENCODING}"
    od, sd = os.path.join(work, tag + ".ours.db"), os.path.join(work, tag + ".shell.db")
    for p in (od, sd):
        for suffix in ("", "-wal", "-shm", "-journal"):
            if os.path.exists(p + suffix):
                os.remove(p + suffix)
    problems = []
    script = build_script(page_size, module, opts, rows, modify)
    o = base.ours(a.sqlrun, od, script)
    s = base.shell(a.shell, sd, script)
    if o.strip() or s.strip():
        problems.append(f"build output: ours={o.strip()[:150]!r} shell={s.strip()[:150]!r}")
    enc_have = base.shell(a.shell, od, "PRAGMA encoding;").strip()
    if enc_have != {"utf8": "UTF-8", "utf16le": "UTF-16le", "utf16be": "UTF-16be"}[base.ENCODING]:
        problems.append(f"encoding of our file is {enc_have}")
    if base.shell(a.shell, od, "PRAGMA page_size;").strip() != str(page_size):
        problems.append("page size of our file differs")

    # a contentless table has no content to validate the index against: the reference fails both its own integrity-check command and
    # PRAGMA integrity_check on such a table, even on files it wrote itself, so only the query results are compared there
    check = CHECK if modify else None

    def checks(stage):
        if check is None:
            return
        for who, db in (("ours file", od), ("shell file", sd)):
            r1 = base.shell(a.shell, db, check)
            r2 = base.ours(a.sqlrun, db, check)
            if not base.check_ok(r1):
                problems.append(f"{stage}: shell on {who}: {r1.strip()[:200]!r}")
            if not base.check_ok(r2):
                problems.append(f"{stage}: OneLite on {who}: {r2.strip()[:200]!r}")

    def compare(stage):
        qs = base.with_markers(queries)
        res = {"shell/shell": base.shell(a.shell, sd, qs), "shell/ours": base.shell(a.shell, od, qs),
               "ours/ours": base.ours(a.sqlrun, od, qs), "ours/shell": base.ours(a.sqlrun, sd, qs)}
        ref = f.segs(f.norm(res["shell/shell"]))
        for name, text in res.items():
            if name == "shell/shell":
                continue
            got = f.segs(f.norm(text))
            bad = [k for k in sorted(ref) if ref[k] != got.get(k)]
            if bad:
                k = bad[0]
                ra, rb = ' / '.join(ref[k]), ' / '.join(got.get(k, ['-']))
                at = next((i for i in range(min(len(ra), len(rb))) if ra[i] != rb[i]), min(len(ra), len(rb)))
                lo = max(0, at - 40)
                problems.append(f"{stage}: {name}: {len(bad)} of {len(queries)} queries differ, first #{k}: {queries[k][:120]} "
                                f"first difference at character {at}: ref=...{ra[lo:at + 60]!r} got=...{rb[lo:at + 60]!r}")

    checks("fresh")
    compare("fresh")
    n = len(rows)
    ins = [f"INSERT INTO t(docid,a,b) VALUES ({n + i},'{rows[i][1]}','{rows[i][2]} later');" for i in range(1, 200)]
    tail = ["DELETE FROM t WHERE docid % 5 = 1;"] if modify else []
    extra = "\n".join(ins + tail + ["INSERT INTO t(t) VALUES('optimize');", "INSERT INTO t(t) VALUES('merge=2,3');"]) + "\n"
    base.ours(a.sqlrun, sd, extra)
    base.shell(a.shell, od, extra)
    checks("after cross-writes")
    compare("after cross-writes")
    return problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sqlrun", required=True)
    ap.add_argument("--shell", required=True)
    ap.add_argument("--docs", type=int, default=3000)
    ap.add_argument("--queries", type=int, default=300)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--work", default="fts3_interop_work")
    ap.add_argument("--only", default="")
    ap.add_argument("--matrix", choices=("default", "full"), default="default")
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)
    r = random.Random(a.seed)
    words = base.vocab(r)
    total_docs = cases = failed = 0
    for page_size, enc in (FULL_CONFIGS if a.matrix == "full" else CONFIGS):
        base.ENCODING = enc
        for label, module, opts, modify in COMBOS:
            if a.only not in f"{label}-p{page_size}-{enc}":
                continue
            rows = base.make_docs(r, words, a.docs)
            queries = make_queries(r, words, "notindexed" in opts, a.queries)
            t0 = time.time()
            problems = run_case(a, label, module, opts, modify, page_size, rows, queries, a.work)
            cases += 1
            total_docs += a.docs
            print(f"{'FAIL' if problems else 'ok  '} {label} page={page_size} {enc} docs={a.docs} queries={a.queries} {time.time() - t0:.0f}s", flush=True)
            for p in problems[:4]:
                print("   ", p[:400], flush=True)
            failed += bool(problems)
    print(f"{cases} cases, {total_docs} documents, {failed} failing")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
