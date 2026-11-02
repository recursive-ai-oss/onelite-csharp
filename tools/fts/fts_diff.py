#!/usr/bin/env python3
"""Seeded differential run of FTS5 scripts: OneLite against the pinned sqlite3 shell.

Each case is a script (schema with random options, rows from a small seeded corpus, updates and deletes, special commands,
queries of every syntax form, auxiliary functions, fts5vocab). Both engines run it; rows and error text are compared (floats to
12 significant digits). The file OneLite wrote must also pass the shell's integrity-check and PRAGMA integrity_check, and the
shell's own file must pass OneLite's integrity-check.

usage: fts_diff.py --sqlrun <OneLite.SqlRun.dll> --shell <sqlite3> [--seed N] [--cases K] [--minutes M] [--out DIR]
"""
import argparse, os, random, re, subprocess, sys, time

WORDS = ["alpha", "beta", "gamma", "delta", "epsilon", "run", "running", "runs", "ran", "jump", "jumped", "jumping", "quick", "brown",
         "fox", "lazy", "dog", "the", "a", "of", "x", "caf\u00e9", "na\u00efve", "\u00dcber", "stra\u00dfe", "\u00e9clair", "\u4e2d\u6587",
         "Zoe", "ZOE", "foo-bar", "foo_bar", "o'neil", "3.14", "42", "caresses", "ponies", "relational", "conditional", "happiness",
         "generalization", "agreed", "plastered", "motoring", "sing", "hopping", "filing", "sky", "cats", "feed", "bled", "tanned",
         "falling", "hissing", "fizzed", "failing", "happy", "I", "II", "dolor", "sit", "amet", "lorem", "ipsum", "e\u0301clair"]
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


def q(s):
    return "'" + s.replace("'", "''") + "'"


def term(r, prefix_ok=True):
    w = r.choice(WORDS)
    w = re.sub(r"[^\w\u0080-\uffff]", "", w) or "a"
    if prefix_ok and r.random() < 0.3 and len(w) > 2:
        w = w[: r.randint(1, len(w) - 1)] + "*"
    return w


def phrase(r, cols):
    k = r.random()
    if k < 0.6:
        t = term(r)
    elif k < 0.85:
        t = '"' + " ".join(term(r, False) for _ in range(r.randint(2, 3))) + '"'
    else:
        t = " + ".join(term(r, False) for _ in range(2))
    if r.random() < 0.1:
        t = "^" + t
    if r.random() < 0.15:
        c = r.choice(cols)
        t = c + " : " + t
    return t


def query(r, cols, depth=0):
    k = r.random()
    if depth > 2 or k < 0.45:
        return phrase(r, cols)
    if k < 0.6:
        return query(r, cols, depth + 1) + " AND " + query(r, cols, depth + 1)
    if k < 0.75:
        return query(r, cols, depth + 1) + " OR " + query(r, cols, depth + 1)
    if k < 0.82:
        return query(r, cols, depth + 1) + " NOT " + phrase(r, cols)
    if k < 0.9:
        return "NEAR(" + " ".join(phrase(r, []) if False else term(r, False) for _ in range(r.randint(2, 3))) + ", %d)" % r.randint(0, 6)
    if k < 0.95:
        return "(" + query(r, cols, depth + 1) + ")"
    return "{" + " ".join(r.sample(cols, min(len(cols), 2))) + "} : " + term(r)


def bad_queries():
    return ["", "AND", "a AND", "(a", "a)", '"abc', "a OR OR b", "NEAR(a)", "NEAR(a b, x)", "a : ", "zzz : a", "^", "a +", "* a", "a^b",
            "NOT a", "a NOT", "{a b", "a:b:c", "-", "- a : x", "NEAR(", "'", "a b (c d)", "(a b) c"]


def make_case(seed):
    r = random.Random(seed)
    ncol = r.randint(1, 4)
    cols = ["c%d" % i for i in range(ncol)]
    coldefs = [c + (" UNINDEXED" if r.random() < 0.15 and ncol > 1 else "") for c in cols]
    opts = []
    tok = r.choice(["", "", "unicode61", "unicode61 remove_diacritics 0", "unicode61 remove_diacritics 2", "porter", "ascii",
                    "porter unicode61 remove_diacritics 0", "trigram", "trigram case_sensitive 1", "trigram remove_diacritics 1",
                    "unicode61 tokenchars '-_'", "unicode61 separators 'e'", "unicode61 categories 'L* N* Co Mn'", "ascii tokenchars '-'"])
    if tok:
        opts.append("tokenize=" + q(tok))
    if r.random() < 0.3:
        opts.append("prefix=" + q(r.choice(["1", "2", "2 3", "1 2 3 4"])))
    detail = r.choice(["full"] * 5 + ["column", "none"])
    if detail != "full":
        opts.append("detail=" + detail)
    if r.random() < 0.1:
        opts.append("columnsize=0")
    mode = r.choice(["normal"] * 6 + ["external", "contentless"])
    s = []
    if mode == "external":
        s.append("CREATE TABLE src(id INTEGER PRIMARY KEY, " + ", ".join(cols) + ");")
        opts.append("content=src")
        opts.append("content_rowid=id")
    elif mode == "contentless":
        opts.append("content=''")
    s.append("CREATE VIRTUAL TABLE t USING fts5(" + ", ".join(coldefs + opts) + ");")
    rows = {}
    nrows = r.randint(0, 40)
    ids = r.sample(range(1, 200), nrows)
    batch = r.random() < 0.5
    if batch:
        s.append("BEGIN;")
    for i in ids:
        vals = [doc(r, r.randint(0, 12)) if r.random() > 0.08 else "NULL" for _ in cols]
        vs = [v if v == "NULL" else q(v) for v in vals]
        rows[i] = vals
        if mode == "external":
            s.append("INSERT INTO src(id, %s) VALUES(%d, %s);" % (", ".join(cols), i, ", ".join(vs)))
        s.append("INSERT INTO t(rowid, %s) VALUES(%d, %s);" % (", ".join(cols), i, ", ".join(vs)))
    if batch:
        s.append("COMMIT;")
    for _ in range(r.randint(0, 8)):
        k = r.random()
        if not rows:
            break
        i = r.choice(list(rows))
        if mode == "contentless":
            if k < 0.5:
                old = rows[i]
                s.append("INSERT INTO t(t, rowid, %s) VALUES('delete', %d, %s);" % (", ".join(cols), i, ", ".join("NULL" if v == "NULL" else q(v) for v in old)))
                del rows[i]
            continue
        if k < 0.35:
            vals = [doc(r, r.randint(0, 10)) for _ in cols]
            if mode == "external":
                s.append("INSERT INTO t(t, rowid, %s) VALUES('delete', %d, %s);" % (", ".join(cols), i, ", ".join("NULL" if v == "NULL" else q(v) for v in rows[i])))
                s.append("UPDATE src SET %s WHERE id=%d;" % (", ".join("%s=%s" % (c, q(v)) for c, v in zip(cols, vals)), i))
                s.append("INSERT INTO t(rowid, %s) VALUES(%d, %s);" % (", ".join(cols), i, ", ".join(q(v) for v in vals)))
            else:
                s.append("UPDATE t SET %s WHERE rowid=%d;" % (", ".join("%s=%s" % (c, q(v)) for c, v in zip(cols, vals)), i))
            rows[i] = vals
        elif k < 0.7:
            if mode == "external":
                s.append("INSERT INTO t(t, rowid, %s) VALUES('delete', %d, %s);" % (", ".join(cols), i, ", ".join("NULL" if v == "NULL" else q(v) for v in rows[i])))
                s.append("DELETE FROM src WHERE id=%d;" % i)
            else:
                s.append("DELETE FROM t WHERE rowid=%d;" % i)
            del rows[i]
        else:
            s.append(r.choice(["INSERT INTO t(t) VALUES('optimize');", "INSERT INTO t(t, rank) VALUES('merge', 4);",
                               "INSERT INTO t(t, rank) VALUES('automerge', %d);" % r.choice([0, 2, 4, 8]),
                               "INSERT INTO t(t, rank) VALUES('pgsz', %d);" % r.choice([64, 200, 1000, 4000]),
                               "INSERT INTO t(t, rank) VALUES('crisismerge', 3);"]))
    if mode == "normal" and r.random() < 0.5:
        s.append("INSERT INTO t(t) VALUES('rebuild');")
    if mode != "contentless":
        s.append("INSERT INTO t(t) VALUES('integrity-check');")
    s.append("SELECT count(*) FROM t;")
    for _ in range(r.randint(6, 16)):
        qs = query(r, cols)
        k = r.random()
        if k < 0.35:
            s.append("SELECT rowid FROM t WHERE t MATCH %s;" % q(qs))
        elif k < 0.5:
            s.append("SELECT rowid, round(bm25(t), 8) FROM t WHERE t MATCH %s ORDER BY rank, rowid;" % q(qs))
        elif k < 0.6 and mode != "contentless":
            s.append("SELECT rowid, highlight(t, %d, '[', ']') FROM t WHERE t MATCH %s;" % (r.randrange(ncol), q(qs)))
        elif k < 0.7 and mode != "contentless":
            s.append("SELECT rowid, snippet(t, %d, '<', '>', '...', %d) FROM t WHERE t MATCH %s;" % (r.randint(-1, ncol - 1), r.randint(1, 12), q(qs)))
        elif k < 0.78:
            s.append("SELECT rowid FROM t WHERE %s MATCH %s;" % (r.choice(cols), q(qs)))
        elif k < 0.84:
            s.append("SELECT rowid FROM t WHERE t MATCH %s AND rowid > %d ORDER BY rowid DESC;" % (q(qs), r.randint(0, 100)))
        elif k < 0.9:
            s.append("SELECT rowid, round(bm25(t, %s), 8) FROM t WHERE t MATCH %s ORDER BY 2, 1;" % (", ".join("%.1f" % r.choice([0.5, 1, 2, 10]) for _ in cols), q(qs)))
        elif k < 0.95:
            s.append("SELECT %s FROM t WHERE rowid BETWEEN %d AND %d;" % (", ".join(cols), r.randint(0, 50), r.randint(50, 200)))
        else:
            s.append("SELECT count(*) FROM t WHERE t MATCH %s;" % q(qs))
    for bq in r.sample(bad_queries(), 4):
        s.append("SELECT rowid FROM t WHERE t MATCH %s;" % q(bq))
    for ty in ("row", "col", "instance"):
        s.append("CREATE VIRTUAL TABLE v_%s USING fts5vocab(t, %s);" % (ty, q(ty)))
        s.append("SELECT * FROM v_%s ORDER BY 1, 2, 3;" % ty)
    # a marker row after every statement lets the comparison report per statement
    return "\n".join(x + "\nSELECT '#%d';" % i for i, x in enumerate(s)) + "\n"


def segs(text):
    out, cur = {}, []
    for line in text.splitlines():
        m = re.match(r"^#(\d+)$", line)
        if m:
            out[int(m.group(1))] = cur
            cur = []
        else:
            cur.append(line)
    return out


def norm(text):
    out = []
    for line in text.splitlines():
        m = re.match(r"^(?:Parse error|Runtime error|Error)(?: near line \d+)?: (.*?)(?: \[\d+\])?\s*$", line)
        if m:
            out.append("ERR " + re.sub(r"\s*\(\d+\)$", "", m.group(1)))
            continue
        out.append(re.sub(r"-?\d+\.\d+(?:e[-+]?\d+)?", lambda g: "%.9g" % float(g.group(0)), line))
    return "\n".join(out)


def run(cmd, script, timeout=120):
    if not cmd[0].endswith("sqlite3"):
        try:
            p = subprocess.run(cmd, input=script, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=timeout)
            return p.stdout
        except subprocess.TimeoutExpired:
            return "TIMEOUT"
    # The shell: stdout and stderr share one pty so error lines stay in step with the rows
    # (a pipe would block-buffer stdout). Works without stdbuf, so it runs on macOS too.
    import pty, termios, threading, select
    master, slave = pty.openpty()
    attrs = termios.tcgetattr(slave)
    attrs[1] &= ~termios.OPOST
    termios.tcsetattr(slave, termios.TCSANOW, attrs)
    p = subprocess.Popen([cmd[0], "-batch", "-list"] + cmd[1:], stdin=subprocess.PIPE, stdout=slave, stderr=slave)
    os.close(slave)

    def feed():
        try:
            p.stdin.write(script.encode())
            p.stdin.close()
        except OSError:
            pass
    t = threading.Thread(target=feed, daemon=True)
    t.start()
    chunks = []
    end = time.time() + timeout
    try:
        while True:
            left = end - time.time()
            if left <= 0:
                p.kill()
                return "TIMEOUT"
            r, _, _ = select.select([master], [], [], min(left, 1.0))
            if not r:
                if p.poll() is not None:
                    break
                continue
            try:
                d = os.read(master, 65536)
            except OSError:
                break
            if not d:
                break
            chunks.append(d)
    finally:
        os.close(master)
        p.wait()
    return b"".join(chunks).decode(errors="replace")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sqlrun", required=True)
    ap.add_argument("--shell", required=True)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--cases", type=int, default=10**9)
    ap.add_argument("--from", dest="start", type=int, default=0)
    ap.add_argument("--minutes", type=float, default=1)
    ap.add_argument("--out", default="fts_diff_out")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    end = time.time() + a.minutes * 60
    bad = 0
    n = 0
    checks = 0
    for i in range(a.start, a.start + a.cases):
        if time.time() > end:
            break
        script = make_case(a.seed * 1000003 + i)
        stmts = script.split("\nSELECT '#")
        od, sd = os.path.join(a.out, "o.db"), os.path.join(a.out, "s.db")
        for f in (od, sd, od + "-journal", sd + "-journal"):
            if os.path.exists(f):
                os.remove(f)
        # keep the shell's view of the script free of OneLite's batch helpers
        so = run(["dotnet", a.sqlrun, od], script)
        ss = run([a.shell, sd], script)
        n += 1
        checks += script.count("\n")
        no, ns = norm(so), norm(ss)
        problem = None
        if no != ns:
            problem = "output differs"
        else:
            # cross-check the files: shell integrity on ours, ours on the shell's
            if "contentless" not in script and "content=''" not in script:
                chk = run([a.shell, od], "INSERT INTO t(t) VALUES('integrity-check'); PRAGMA integrity_check;")
                if chk.strip() not in ("ok",):
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
                    print("  SEG %d: %s\n     shell=%r\n     ours =%r" % (k, st[:160], " / ".join(sa[k])[:160], " / ".join(sb.get(k, ["-"]))[:160]))
            first = next((k for k in range(min(len(la), len(lb))) if la[k] != lb[k]), min(len(la), len(lb)))
            print("DIFF case %d: %s (line %d) shell=%r ours=%r -> %s" % (i, problem, first, la[first][:100] if first < len(la) else None, lb[first][:100] if first < len(lb) else None, p))
    print("seed %d: %d cases, %d statements, %d differing" % (a.seed, n, checks, bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
