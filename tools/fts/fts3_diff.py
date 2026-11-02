#!/usr/bin/env python3
"""Seeded differential run of FTS3 and FTS4 scripts: OneLite against the pinned sqlite3 shell.

Each case is a script: a table with random options (tokenizer, prefix indexes, order=desc, notindexed, matchinfo=fts3, languageid,
external or no content), rows from a small seeded corpus, updates and deletes, special commands, and queries of every standard-syntax
form with docid, offsets, snippet and matchinfo. Both engines run it; rows and error text are compared per statement (floats to
9 significant digits). The file OneLite wrote must also pass the shell's integrity-check and PRAGMA integrity_check, and the shell's own
file must pass OneLite's integrity-check.

usage: fts3_diff.py --sqlrun <OneLite.SqlRun.dll> --shell <sqlite3> [--seed N] [--cases K] [--minutes M] [--out DIR]
"""
import argparse, os, random, re, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fts_diff import q, norm, run, segs  # noqa: E402

WORDS = ["alpha", "beta", "gamma", "delta", "epsilon", "run", "running", "runs", "ran", "jump", "jumped", "jumping", "quick", "brown",
         "fox", "lazy", "dog", "the", "a", "of", "x", "caf\u00e9", "na\u00efve", "\u00dcber", "stra\u00dfe", "\u00e9clair", "\u4e2d\u6587",
         "Zoe", "ZOE", "foo-bar", "foo_bar", "o'neil", "3.14", "42", "caresses", "ponies", "relational", "conditional", "happiness",
         "generalization", "agreed", "plastered", "motoring", "sing", "hopping", "filing", "sky", "cats", "feed", "bled", "tanned",
         "falling", "hissing", "fizzed", "failing", "happy", "I", "II", "dolor", "sit", "amet", "lorem", "ipsum", "e\u0301clair", "or", "near"]
SEP = [" ", " ", " ", ", ", ". ", " - ", "\n", "  "]


def doc(r, nwords):
    out = []
    for _ in range(nwords):
        w = r.choice(WORDS)
        if r.random() < 0.15:
            w = w.upper() if r.random() < 0.5 else w.capitalize()
        out.append(w)
        out.append(r.choice(SEP))
    return "".join(out).rstrip()


def term(r, prefix_ok=True):
    w = r.choice(WORDS)
    w = re.sub(r"[^\w\u0080-\uffff]", "", w) or "a"
    if prefix_ok and r.random() < 0.3 and len(w) > 2:
        w = w[: r.randint(1, len(w) - 1)] + "*"
    return w


def phrase(r, cols, fts4):
    k = r.random()
    if k < 0.65:
        t = term(r)
    else:
        t = '"' + " ".join(term(r) for _ in range(r.randint(2, 3))) + '"'
    if fts4 and r.random() < 0.1:
        t = "^" + t
    if r.random() < 0.15:
        # a column filter written directly against a quote is not matched (docs/fts3.md), so a space goes between them
        t = r.choice(cols) + (": " if t.startswith('"') else ":") + t
    return t


def query(r, cols, fts4):
    parts = []
    for _ in range(r.randint(1, 3)):
        k = r.random()
        if k < 0.55:
            parts.append(phrase(r, cols, fts4))
        elif k < 0.75:
            parts.append(phrase(r, cols, fts4) + " OR " + phrase(r, cols, fts4))
        elif k < 0.9:
            op = r.choice(["NEAR", "NEAR/%d" % r.randint(0, 5)])
            parts.append(phrase(r, cols, fts4) + " " + op + " " + phrase(r, cols, fts4))
        else:
            parts.append("-" + term(r, False))
    return " ".join(parts)


def bad_queries():
    return ["", "AND", "a AND", "(a", "a)", '"abc', "a OR OR b", "NEAR(a)", "NEAR a", "NEAR/2 a", "a NEAR", "a : ", "zzz : a", "^", "a +",
            "* a", "a^b", "NOT a", "a NOT", "-", "-a", "a -b", "-a -b", "a OR -b", "a:b:c", "'", "a b (c d)", "(a b) c", "a OR", "OR a",
            "a NEAR/x b", "\"a b\" NEAR \"c d\"", "a-b", "a- b", "a - b", "a*b", "*", "a**", "\"\"", "\"*\"", "\" \"", "NEAR NEAR", "a NEAR b NEAR c"]


def make_case(seed):
    r = random.Random(seed)
    fts4 = r.random() < 0.7
    ncol = r.randint(1, 3)
    cols = ["c%d" % i for i in range(ncol)]
    opts = []
    tok = r.choice(["", "", "simple", "porter", "unicode61", "unicode61 \"remove_diacritics=0\"", "unicode61 \"remove_diacritics=2\"",
                    "unicode61 \"tokenchars=-_\"", "unicode61 \"separators=e\""])
    if tok:
        opts.append("tokenize=" + tok)
    if fts4:
        if r.random() < 0.3:
            opts.append("prefix=" + q(r.choice(["1", "2", "2,3", "1,2,3,4", "3"])))
        if r.random() < 0.15:
            opts.append("order=desc")
        if r.random() < 0.15:
            opts.append("matchinfo=fts3")
        if r.random() < 0.15 and ncol > 1:
            opts.append("notindexed=" + cols[-1])
        lang = r.random() < 0.15
        if lang:
            opts.append("languageid=lid")
    else:
        lang = False
    mode = r.choice(["normal"] * 6 + ["external", "contentless"]) if fts4 else "normal"
    s = []
    if mode == "external":
        s.append("CREATE TABLE src(id INTEGER PRIMARY KEY, " + ", ".join(cols) + (", lid" if lang else "") + ");")
        opts.append("content=src")
    elif mode == "contentless":
        opts.append("content=''")
    s.append("CREATE VIRTUAL TABLE t USING %s(%s);" % ("fts4" if fts4 else "fts3", ", ".join(cols + opts)))
    rows = {}
    nrows = r.randint(0, 40)
    ids = r.sample(range(1, 200), nrows)
    batch = r.random() < 0.5
    if batch:
        s.append("BEGIN;")
    for i in ids:
        vals = [doc(r, r.randint(0, 12)) if r.random() > 0.08 else "NULL" for _ in cols]
        vs = [v if v == "NULL" else q(v) for v in vals]
        lid = r.choice([0, 0, 0, 1, 2]) if lang else 0
        rows[i] = (vals, lid)
        lcol = ", lid" if lang else ""
        lval = ", %d" % lid if lang else ""
        if mode == "external":
            s.append("INSERT INTO src(id, %s%s) VALUES(%d, %s%s);" % (", ".join(cols), lcol, i, ", ".join(vs), lval))
        s.append("INSERT INTO t(docid, %s%s) VALUES(%d, %s%s);" % (", ".join(cols), lcol, i, ", ".join(vs), lval))
    if batch:
        s.append("COMMIT;")
    for _ in range(r.randint(0, 8)):
        k = r.random()
        if not rows:
            break
        i = r.choice(list(rows))
        if mode == "contentless":
            s.append("DELETE FROM t WHERE docid=%d;" % i)
            continue
        if k < 0.35:
            vals = [doc(r, r.randint(0, 10)) for _ in cols]
            if mode == "external":
                s.append("DELETE FROM t WHERE docid=%d;" % i)
                s.append("UPDATE src SET %s WHERE id=%d;" % (", ".join("%s=%s" % (c, q(v)) for c, v in zip(cols, vals)), i))
                s.append("INSERT INTO t(docid, %s%s) VALUES(%d, %s%s);" % (", ".join(cols), ", lid" if lang else "", i, ", ".join(q(v) for v in vals), ", %d" % rows[i][1] if lang else ""))
            else:
                s.append("UPDATE t SET %s WHERE docid=%d;" % (", ".join("%s=%s" % (c, q(v)) for c, v in zip(cols, vals)), i))
            rows[i] = (vals, rows[i][1])
        elif k < 0.6:
            s.append("DELETE FROM t WHERE docid=%d;" % i)
            if mode == "external":
                s.append("DELETE FROM src WHERE id=%d;" % i)
            del rows[i]
        elif k < 0.7 and mode == "normal":
            s.append("UPDATE t SET docid=%d WHERE docid=%d;" % (i + 300, i))
            rows[i + 300] = rows.pop(i)
        else:
            s.append(r.choice(["INSERT INTO t(t) VALUES('optimize');", "INSERT INTO t(t) VALUES('merge=4,2');",
                               "INSERT INTO t(t) VALUES('automerge=%d');" % r.choice([0, 2, 4, 8]) if fts4 else "INSERT INTO t(t) VALUES('optimize');"]))
    if mode == "normal" and r.random() < 0.4:
        s.append("INSERT INTO t(t) VALUES('rebuild');")
    if mode != "contentless":
        s.append("INSERT INTO t(t) VALUES('integrity-check');")
    if mode != "contentless":
        s.append("SELECT count(*) FROM t;")
    for _ in range(r.randint(6, 16)):
        qs = query(r, cols, fts4)
        k = r.random()
        if k < 0.3:
            s.append("SELECT docid FROM t WHERE t MATCH %s;" % q(qs))
        elif k < 0.4 and mode != "contentless":
            s.append("SELECT docid, offsets(t) FROM t WHERE t MATCH %s;" % q(qs))
        elif k < 0.55 and mode != "contentless":
            s.append("SELECT docid, snippet(t) FROM t WHERE t MATCH %s;" % q(qs))
        elif k < 0.65 and mode != "contentless":
            s.append("SELECT docid, snippet(t, '[', ']', '..', %d, %d) FROM t WHERE t MATCH %s;" % (r.randint(-1, ncol - 1), r.choice([-5, -3, -1, 2, 3, 5, 8, 15, 64]), q(qs)))
        elif k < 0.8:
            fmt = r.choice(["pcx", "pcxn", "pcxnal", "s", "y", "b", "ns", "pcnalsyb", "x"])
            if "NEAR" in qs and "x" in fmt:
                # the hit totals ("x") of a NEAR query depend on how much of the later doclists the reference reads (docs/fts3.md)
                fmt = fmt.replace("x", "") or "pc"
            s.append("SELECT docid, hex(matchinfo(t, %s)) FROM t WHERE t MATCH %s;" % (q(fmt), q(qs)))
        elif k < 0.86:
            s.append("SELECT docid FROM t WHERE %s MATCH %s;" % (r.choice(cols), q(qs)))
        elif k < 0.92:
            s.append("SELECT docid FROM t WHERE t MATCH %s AND docid > %d ORDER BY docid DESC;" % (q(qs), r.randint(0, 100)))
        elif k < 0.96 and mode != "contentless":
            s.append("SELECT docid, %s FROM t WHERE docid BETWEEN %d AND %d;" % (", ".join(cols), r.randint(0, 50), r.randint(50, 200)))
        elif lang:
            s.append("SELECT docid FROM t WHERE t MATCH %s AND lid = %d;" % (q(qs), r.randint(0, 2)))
        else:
            s.append("SELECT count(*) FROM t WHERE t MATCH %s;" % q(qs))
    for bq in r.sample(bad_queries(), 6):
        s.append("SELECT docid FROM t WHERE t MATCH %s;" % q(bq))
    # fts4aux counts segment entries without resolving a document's old versions on an order=desc table, and counts the stale entries an
    # external-content table keeps when its content changed before the delete; both are documented differences (docs/fts3.md), so not compared
    if "order=desc" not in opts and mode != "external":
        s.append("CREATE VIRTUAL TABLE aux USING fts4aux(t);")
        s.append("SELECT * FROM aux;")
        if lang:
            s.append("SELECT * FROM aux WHERE languageid = %d;" % r.randint(1, 2))
        s.append("SELECT term, col, documents FROM aux WHERE term >= %s ORDER BY term DESC, col LIMIT 20;" % q(r.choice(WORDS)[:2]))
    # merge=X,Y and automerge=N merge a little at a time in the shell and fully here, so the segment counts differ after them
    # A transaction, or rows of several languages, are one segment per language and statement here and one per transaction in the shell.
    merged = any("merge" in x for x in s) or batch or lang
    s.append("SELECT optimize(t) FROM t LIMIT 1;" if mode == "normal" and not merged else "SELECT 1;")
    return "\n".join(x + "\nSELECT '#%d';" % i for i, x in enumerate(s)) + "\n"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sqlrun", required=True)
    ap.add_argument("--shell", required=True)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--cases", type=int, default=10**9)
    ap.add_argument("--from", dest="start", type=int, default=0)
    ap.add_argument("--minutes", type=float, default=1)
    ap.add_argument("--out", default="fts3_diff_out")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    end = time.time() + a.minutes * 60
    bad = n = checks = 0
    for i in range(a.start, a.start + a.cases):
        if time.time() > end:
            break
        script = make_case(a.seed * 1000003 + i)
        stmts = script.split("\nSELECT '#")
        od, sd = os.path.join(a.out, "o.db"), os.path.join(a.out, "s.db")
        for f in (od, sd, od + "-journal", sd + "-journal"):
            if os.path.exists(f):
                os.remove(f)
        so = run(["dotnet", a.sqlrun, od], script)
        ss = run([a.shell, sd], script)
        n += 1
        checks += script.count("\n")
        # OneLite adds "[code] detail" after the identical message text on an error line
        so = re.sub(r"^(Error: .*?) \[\d+\] .*$", r"\1", so, flags=re.M)
        no, ns = norm(so), norm(ss)
        problem = None
        if no != ns:
            problem = "output differs"
        elif "content=''" not in script:
            chk = run([a.shell, od], "INSERT INTO t(t) VALUES('integrity-check'); PRAGMA integrity_check;")
            if chk.strip() != "ok":
                problem = "shell rejects our file: " + chk.strip()[:150]
            else:
                chk2 = run(["dotnet", a.sqlrun, sd], "INSERT INTO t(t) VALUES('integrity-check');")
                if chk2.strip():
                    problem = "we reject the shell file: " + chk2.strip()[:150]
        if problem:
            bad += 1
            p = os.path.join(a.out, "case-%d-%d.sql" % (a.seed, i))
            open(p, "w").write(script)
            la, lb = ns.splitlines(), no.splitlines()
            sa, sb = segs(ns), segs(no)
            nd = 0
            for k in sorted(sa):
                if sa[k] != sb.get(k) and nd < 3:
                    nd += 1
                    st = (stmts[k].split("\n", 1)[1] if k > 0 else stmts[k]) if k < len(stmts) else ""
                    st = st.replace("\n", " ")
                    print("  SEG %d: %s\n     shell=%r\n     ours =%r" % (k, st[:200], " / ".join(sa[k])[:200], " / ".join(sb.get(k, ["-"]))[:200]))
            first = next((k for k in range(min(len(la), len(lb))) if la[k] != lb[k]), min(len(la), len(lb)))
            print("DIFF case %d: %s (line %d) -> %s" % (i, problem, first, p))
    print("seed %d: %d cases, %d statements, %d differing" % (a.seed, n, checks, bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
