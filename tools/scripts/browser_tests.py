#!/usr/bin/env python3
"""Runs the OneLite browser tests in headless Chrome and checks what the browser writes with the sqlite3 shell.

    browser_tests.py --site SITE --chrome CHROME --sqlite3 SQLITE3 [--corpus DIR ...] [--out DIR] [--rows N] [--only SUBSTRING]

SITE is the published output of tests/OneLite.BrowserTests (its wwwroot). CHROME is the Chrome for Testing binary that
tools/scripts/fetch_browser.sh installs; the driver is the Playwright package from the same script's virtual environment, so run this file with
that environment's python. Everything the run needs is served from a local port bound to 127.0.0.1; nothing leaves the machine.

What it runs
  1. every test the page (main thread) and a dedicated worker report, one call each: the VFS behaviour, pager, B-tree and transfer tests;
  2. two contexts on one origin: a second page and a second worker contend for locks on the same file;
  3. each database file in --corpus is imported into OPFS through each backend, read back through the B-tree and exported; the export must equal the source;
  4. databases written in the browser (rollback and WAL mode, each backend) are exported to this host, checked with sqlite3 `PRAGMA integrity_check`,
     and their `.dump` is compared with the dump of the same content built by sqlite3.
  5. with --kill N: kill-the-tab runs, N kills for each of the four combinations (main-thread backend and worker backend, each in rollback and WAL mode).
     A page opens the database through the engine (Database.OpenAsync) and starts a writer loop that commits transactions of one to three rows; this
     driver polls how many have been acknowledged, then kills the page at a random moment (closing it, or navigating it away). A fresh page then recovers the file (opening it rolls back a hot journal or reads the log), exports it, and this host checks with sqlite3:
     `PRAGMA integrity_check` is ok, every row up to the last acknowledged one is present, and rows are contiguous with no transaction half there.
     --kill-only skips 1 to 4.
"""
import argparse
import http.server
import json
import os
import socketserver
import subprocess
import sys
import threading
import time
import urllib.parse

from playwright.sync_api import sync_playwright


def args():
    p = argparse.ArgumentParser()
    p.add_argument("--site", required=True)
    p.add_argument("--chrome", required=True)
    p.add_argument("--sqlite3", required=True)
    p.add_argument("--corpus", action="append", default=[], help="directories with .db files to import (non-recursive)")
    p.add_argument("--out", default=os.path.join(os.environ.get("TMPDIR", "/tmp"), "browser-tests"))
    p.add_argument("--rows", type=int, default=1500)
    p.add_argument("--only", default="")
    p.add_argument("--max-corpus-bytes", type=int, default=30_000_000)
    p.add_argument("--kill", type=int, default=0, help="kills per backend and journal mode (see the module docstring)")
    p.add_argument("--kill-only", action="store_true")
    p.add_argument("--trace", type=int, default=0, help="dump every thread's stack each N seconds (to find where a run is stuck)")
    p.add_argument("--kill-seed", type=int, default=1)
    p.add_argument("--kill-backends", default="page/async,worker/sync", help="comma-separated where/backend pairs")
    return p.parse_args()


class Host(http.server.SimpleHTTPRequestHandler):
    """Serves the site with the headers that make the page cross-origin isolated, the corpus files, and receives uploads."""

    site = ""
    corpus = {}
    out = ""

    def __init__(self, *a, **k):
        super().__init__(*a, directory=Host.site, **k)

    def end_headers(self):
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    range_files = {}
    range_version = 0

    def do_HEAD(self):
        self.serve_range(head=True)

    def serve_range(self, head=False):
        """/range/NAME: a file with range requests, an ETag that changes when range_version does, and If-Match."""
        name = urllib.parse.unquote(self.path.split("?")[0])[len("/range/"):]
        versions = Host.range_files.get(name)
        if versions is None:
            self.send_error(404)
            return
        data = open(versions[min(Host.range_version, len(versions) - 1)], "rb").read()
        etag = f'"{name}-{Host.range_version}"'
        if self.headers.get("If-Match") not in (None, etag):
            self.send_response(412)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        rng = self.headers.get("Range")
        start, end, status = 0, len(data) - 1, 200
        if rng and not head:
            a, b = rng.split("=")[1].split("-")
            start, end, status = int(a), min(int(b), len(data) - 1) if b else len(data) - 1, 206
        body = b"" if head else data[start:end + 1]
        self.send_response(status)
        self.send_header("ETag", etag)
        self.send_header("Last-Modified", "Fri, 02 Jan 2026 03:04:05 GMT")
        self.send_header("Accept-Ranges", "bytes")
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{len(data)}")
        self.send_header("Content-Length", str(len(body) if not head else len(data)))
        self.end_headers()
        if not head:
            self.wfile.write(body)

    def do_GET(self):
        path = urllib.parse.unquote(self.path.split("?")[0])
        if path.startswith("/range/"):
            self.serve_range()
            return
        if path.startswith("/corpus/"):
            f = Host.corpus.get(path[len("/corpus/"):])
            if f is None:
                self.send_error(404)
                return
            data = open(f, "rb").read()
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        super().do_GET()

    def do_POST(self):
        path = urllib.parse.unquote(self.path)
        if not path.startswith("/upload/"):
            self.send_error(404)
            return
        name = os.path.basename(path[len("/upload/"):])
        n = int(self.headers.get("Content-Length", "0"))
        with open(os.path.join(Host.out, name), "wb") as f:
            left = n
            while left > 0:
                chunk = self.rfile.read(min(left, 1 << 20))
                if not chunk:
                    break
                f.write(chunk)
                left -= len(chunk)
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def log_message(self, *a):
        pass


class Results:
    def __init__(self):
        self.rows = []

    def add(self, group, name, ok, detail=""):
        self.rows.append({"group": group, "name": name, "ok": ok, "detail": detail[:600]})
        if not ok:
            print(f"FAIL [{group}] {name}: {detail[:300]}", flush=True)

    def summary(self):
        groups = {}
        for r in self.rows:
            g = groups.setdefault(r["group"], [0, 0])
            g[0 if r["ok"] else 1] += 1
        return groups


def call(page, where, op, *a):
    # A page that never answers (a worker stuck on a lock, say) is a finding, not a reason for the driver to wait for ever.
    return page.evaluate("([w, op, a]) => Promise.race([window.ol.call(w, op, ...a), new Promise(r => setTimeout(() => r('HANG ' + op), 300000))])", [where, op, list(a)])


def cmd(page, where, name, arg=""):
    return call(page, where, "cmd", name, arg)


def sh(sqlite3, db, sql):
    r = subprocess.run([sqlite3, db, sql], capture_output=True, text=True, timeout=120)
    return r.stdout, r.stderr, r.returncode


def expected_dump(sqlite3, path, rows):
    """The same content the browser writes (see DbContent in the test app), built by sqlite3."""
    if os.path.exists(path):
        os.remove(path)
    lines = ["CREATE TABLE t(a INT, b TEXT, c BLOB);", "CREATE INDEX tb ON t(b);", "BEGIN;"]
    for i in range(1, rows + 1):
        b = f"r{i:06d}-" + chr(ord("a") + i % 26) * (i % 17)
        if i % 11 == 0:
            c = "NULL"
        else:
            n = 9000 if i % 50 == 0 else (i * 37) % 700
            c = "x'" + bytes(((i * 31) + j * 7) & 255 for j in range(n)).hex() + "'"
        lines.append(f"INSERT INTO t VALUES({i * 7},'{b}',{c});")
    lines.append("COMMIT;")
    r = subprocess.run([sqlite3, path], input="\n".join(lines), capture_output=True, text=True, timeout=300)
    if r.returncode != 0:
        raise RuntimeError("building the expected database failed: " + r.stderr[:200])
    out, _, _ = sh(sqlite3, path, ".dump")
    return out


KILL_BASE = 1_000_000


def new_page(ctx, base):
    page = ctx.new_page()
    page.on("pageerror", lambda e: print("pageerror:", str(e)[:200], flush=True))
    page.goto(base + "/index.html")
    page.wait_for_function("window.olReady === true", timeout=60000)
    call(page, "page", "init", "")
    call(page, "worker", "init", "")
    return page


def check_recovered(sqlite3, f, acked):
    """What a killed writer may leave: an intact database, every acknowledged row, whole transactions only."""
    ic, err, _ = sh(sqlite3, f, "PRAGMA integrity_check;")
    if ic.strip() != "ok":
        return False, f"integrity_check: {ic.strip()[:200]} {err[:100]}"
    out, err, rc = sh(sqlite3, f, f"select count(*), coalesce(min(a),0), coalesce(max(a),0) from t where a > {KILL_BASE};")
    if rc != 0:
        return False, "query: " + err[:200]
    n, lo, hi = (int(x) for x in out.strip().split("|"))
    if n and (lo != KILL_BASE + 1 or hi - lo + 1 != n):
        return False, f"rows are not contiguous: {n} rows from {lo} to {hi}"
    if max(hi, KILL_BASE) < acked:
        return False, f"acknowledged row {acked} is missing (the file ends at {hi})"
    out, err, rc = sh(sqlite3, f, f"select count(*) from (select b, count(*) c from t where a > {KILL_BASE} group by b) "
                                  "where c <> cast(substr(b, instr(b, ':') + 1) as int);")
    if out.strip() != "0":
        return False, f"{out.strip()} transactions are partly there"
    return True, f"{n} rows, last acknowledged {acked}"


def tcall(page, where, name, arg, what, seconds=90):
    """One command; a page that never answers comes back as HANG (see call) instead of stopping the driver."""
    return cmd(page, where, name, arg)


def kill_campaign(a, browser, ctx, base, res):
    import random
    rnd = random.Random(a.kill_seed)
    for combo in a.kill_backends.split(","):
        where, backend = combo.split("/")
        for mode in ("delete", "wal"):
            group = f"kill/{where}-{backend}-{mode}"
            path = f"kill/{where}-{backend}-{mode}-{a.kill_seed}.db"
            page = new_page(ctx, base)
            print(f"{group}: building {path}", flush=True)
            r = tcall(page, where, "db.build", f"{backend}|{path}|60|{mode}", "db.build")
            print(f"{group}: built: {r[:100]}", flush=True)
            page.close()
            if r != "ok":
                res.add(group, "build", False, r)
                continue
            acked = KILL_BASE
            up = f"{where}-{backend}-{mode}-kill.db"
            ok_n = 0
            for k in range(a.kill):
                page = new_page(ctx, base)
                try:
                    # Recover what the last kill left and check it.
                    r = "BUSY"
                    for _ in range(40):
                        r = call(page, where, "export-upload", backend, path, up)
                        if not r.startswith("BUSY") and not r.startswith("FAIL") or "BUSY" not in r:
                            break
                        time.sleep(0.25)
                    if not r.startswith("ok"):
                        res.add(group, f"kill {k}: recover", False, r)
                        break
                    good, detail = check_recovered(a.sqlite3, os.path.join(a.out, up), acked)
                    res.add(group, f"kill {k}: {detail}", good, detail)
                    print(f"{group} kill {k}: {'ok' if good else 'FAIL'} {detail}", flush=True)
                    if not good:
                        break
                    ok_n += 1
                    # Start a writer and kill the page while it is working.
                    r = cmd(page, where, "kill.run", f"{backend}|{path}|100000")
                    if not r.startswith("started"):
                        res.add(group, f"kill {k}: start", False, r)
                        break
                    acked = max(acked, int(r.split()[1]))
                    target = acked + rnd.randint(1, 25)
                    deadline = time.time() + 20
                    while time.time() < deadline:
                        v = cmd(page, where, "kill.acked")
                        if v.startswith("FAIL"):
                            res.add(group, f"kill {k}: writer", False, v)
                            break
                        acked = max(acked, int(v))
                        if acked >= target:
                            break
                        time.sleep(0.01)
                    time.sleep(rnd.random() * 0.02)
                    if rnd.random() < 0.5:
                        page.close()
                    else:
                        # Navigating away tears the page down in the middle of whatever its writer was doing, without a chance to finish it.
                        page.goto("about:blank", wait_until="commit")
                        page.close()
                except Exception as e:  # a page that dies under a call is part of the test, a driver error is not
                    res.add(group, f"kill {k}: driver", False, repr(e)[:300])
                    try:
                        page.close()
                    except Exception:
                        pass
                    break
            print(f"{group}: {ok_n}/{a.kill} recoveries checked", flush=True)


def http_range_tests(a, ctx, base, res):
    """HttpRangeVfs from the page and from the worker: queries match sqlite3, few bytes move, and a file that changes under a reader fails with a typed error."""
    for ver, rows in enumerate((3000, 3500)):
        f = os.path.join(a.out, f"range-v{ver}.db")
        if os.path.exists(f):
            os.remove(f)
        sql = "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT, c BLOB); CREATE INDEX tb ON t(b); BEGIN;" + "".join(
            f"INSERT INTO t VALUES({i}, 'row {i}', zeroblob(150));" for i in range(1, rows + 1)) + "COMMIT;"
        subprocess.run([a.sqlite3, f], input=sql, text=True, check=True, timeout=120)
        Host.range_files.setdefault("r.db", []).append(f)
    size = os.path.getsize(Host.range_files["r.db"][0])
    Host.range_version = 0
    page = new_page(ctx, base)
    for where in ("page", "worker"):
        group = f"http-range/{where}"
        name = f"h-{where}"
        r = cmd(page, where, "http.open", f"{name}|{base}/range/r.db|4096")
        res.add(group, "open", r == "ok", r)
        if r != "ok":
            continue
        for q in ("select count(*), min(b), max(b) from t", "select b from t where a = 1777", "select count(*) from t where b like 'row 2%'"):
            want, _, _ = sh(a.sqlite3, Host.range_files["r.db"][0], q)
            got = cmd(page, where, "http.q", f"{name}|{q}")
            res.add(group, q, got.split(" ## ")[0] == want.strip(), f"got {got!r}, want {want.strip()!r}")
        before = int(cmd(page, where, "http.q", f"{name}|select 1").split("requests=")[1])
        got = cmd(page, where, "http.q", f"{name}|select b from t where a = 2999")
        req = int(got.split("requests=")[1]) - before
        res.add(group, "requests stay few for point lookups", req < 60, f"{req} requests for a {size}-byte file with 4096-byte blocks")
        # The file changes (the page cache is 8 pages, so the next scan must fetch): the next read of a block that is no longer cached fails with the typed error.
        Host.range_version += 1
        got = cmd(page, where, "http.q", f"{name}|select sum(length(b)), sum(length(c)) from t")
        res.add(group, "a changed file fails the read", got.startswith("ERR 266") and "changed" in got, got[:200])
        cmd(page, where, "http.close", name)
        Host.range_version = 0
    page.close()


def main():
    a = args()
    if a.trace:
        import faulthandler
        faulthandler.dump_traceback_later(a.trace, repeat=True)
    os.makedirs(a.out, exist_ok=True)
    for f in os.listdir(a.out):
        os.remove(os.path.join(a.out, f))
    Host.site = os.path.abspath(a.site)
    Host.out = a.out
    for d in a.corpus:
        for f in sorted(os.listdir(os.path.expanduser(d))):
            p = os.path.join(os.path.expanduser(d), f)
            if f.endswith((".db", ".sqlite")) and os.path.getsize(p) <= a.max_corpus_bytes:
                Host.corpus[f] = p

    srv = socketserver.ThreadingTCPServer(("127.0.0.1", 0), Host)
    srv.daemon_threads = True
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{srv.server_address[1]}"
    res = Results()
    t0 = time.time()

    with sync_playwright() as pw:
        browser = pw.chromium.launch(executable_path=a.chrome, headless=True, args=["--no-sandbox"])
        print("browser:", browser.version, flush=True)
        ctx = browser.new_context()
        page = ctx.new_page()
        page.on("pageerror", lambda e: print("pageerror:", str(e)[:200], flush=True))
        page.goto(base + "/index.html")
        page.wait_for_function("window.olReady === true", timeout=60000)
        print("page :", call(page, "page", "init", ""), flush=True)
        print("worker:", call(page, "worker", "init", ""), flush=True)
        cmd(page, "page", "wipe")

        if not a.kill_only:
            http_range_tests(a, ctx, base, res)

        if a.kill:
            kill_campaign(a, browser, ctx, base, res)
            if a.kill_only:
                browser.close()
                srv.shutdown()
                return finish(a, res, t0)

        # 1. The suites.
        for where in ("page", "worker"):
            names = [n for n in call(page, where, "list", where).split("\n") if n and a.only in n]
            for n in names:
                t = time.time()
                r = call(page, where, "run", n)
                res.add(f"suite/{where}/{n.split('.')[0]}/{n.split('.')[1]}", n, r == "ok", r + f" ({time.time() - t:.1f}s)" if r != "ok" else "")
            print(f"suite/{where}: {len(names)} tests", flush=True)

        # 2. Two contexts, one origin.
        page2 = ctx.new_page()
        page2.goto(base + "/index.html")
        page2.wait_for_function("window.olReady === true", timeout=60000)
        call(page2, "page", "init", "")
        call(page2, "worker", "init", "")
        locks(res, page, page2)

        # 3. Corpus through each backend.
        for name in sorted(Host.corpus):
            for where, backend in (("page", "async"), ("worker", "async"), ("worker", "sync")):
                r = call(page, where, "import-url", backend, f"corpus/{name}", f"/corpus/{name}")
                res.add(f"corpus/{where}-{backend}", name, r.startswith("ok"), r)
                if r.startswith("ok"):
                    print(f"corpus {name} via {where}/{backend}: {r}", flush=True)

        # 4. What the browser writes, checked by sqlite3.
        exp = {}
        for where, backend in (("page", "async"), ("worker", "async"), ("worker", "sync")):
            for mode in ("delete", "wal"):
                rows = a.rows
                if rows not in exp:
                    exp[rows] = expected_dump(a.sqlite3, os.path.join(a.out, "expected.db"), rows)
                path = f"export/{where}-{backend}-{mode}.db"
                r = cmd(page, where, "db.build", f"{backend}|{path}|{rows}|{mode}")
                if r != "ok":
                    res.add("export", f"{where}/{backend}/{mode}", False, "build: " + r)
                    continue
                up = f"{where}-{backend}-{mode}.db"
                r = call(page, where, "export-upload", backend, path, up)
                if not r.startswith("ok"):
                    res.add("export", f"{where}/{backend}/{mode}", False, "export: " + r)
                    continue
                f = os.path.join(a.out, up)
                ic, err, _ = sh(a.sqlite3, f, "PRAGMA integrity_check;")
                d, derr, _ = sh(a.sqlite3, f, ".dump")
                ok = ic.strip() == "ok" and d == exp[rows]
                if d != exp[rows]:
                    import difflib
                    diff = [l for l in difflib.unified_diff(exp[rows].splitlines(), d.splitlines(), lineterm="", n=0)][:6]
                    err += " diff: " + " | ".join(x[:60] for x in diff)
                res.add("export", f"{where}/{backend}/{mode} ({rows} rows, {os.path.getsize(f)} bytes)", ok,
                        f"integrity_check={ic.strip()[:120]!r} dump_equal={d == exp[rows]} {err[:100]}")

        browser.close()

    srv.shutdown()
    return finish(a, res, t0)


def finish(a, res, t0):
    print("\n== summary (%.0fs)" % (time.time() - t0))
    total_ok = total_bad = 0
    for g, (ok, bad) in sorted(res.summary().items()):
        print(f"{g:32s} pass {ok:4d}  fail {bad:3d}")
        total_ok += ok
        total_bad += bad
    print(f"{'total':32s} pass {total_ok:4d}  fail {total_bad:3d}")
    with open(os.path.join(a.out, "results.json"), "w") as f:
        json.dump(res.rows, f, indent=1)
    return 1 if total_bad else 0


def locks(res, p1, p2):
    """lockingv3 across two contexts of one origin: the second context sees the first one's locks."""
    path = f"locks/{int(time.time())}.db"

    def step(group, name, got, want):
        res.add(group, name, got == want, f"got {got!r}, want {want!r}")

    def writer_scenario(group, first, second):
        (pa, wa), (pb, wb) = first, second
        step(group, "first opens", cmd(pa, wa, "lock.open", f"a|async|{path}"), "ok")
        step(group, "first SHARED", cmd(pa, wa, "lock.lock", "a|Shared"), "true")
        step(group, "first RESERVED", cmd(pa, wa, "lock.lock", "a|Reserved"), "true")
        step(group, "second opens", cmd(pb, wb, "lock.open", f"b|async|{path}"), "ok")
        step(group, "second SHARED beside a writer", cmd(pb, wb, "lock.lock", "b|Shared"), "true")
        step(group, "second RESERVED is BUSY while the first holds RESERVED", cmd(pb, wb, "lock.lock", "b|Reserved"), "false")
        step(group, "second sees the first's RESERVED", cmd(pb, wb, "lock.check-reserved", "b"), "true")
        step(group, "first EXCLUSIVE is BUSY while the second reads", cmd(pa, wa, "lock.lock", "a|Exclusive"), "false")
        step(group, "second SHARED still held", cmd(pb, wb, "lock.lock", "b|Shared"), "true")
        cmd(pb, wb, "lock.unlock", "b|None")
        step(group, "first EXCLUSIVE after the reader left", cmd(pa, wa, "lock.lock", "a|Exclusive"), "true")
        step(group, "second SHARED is BUSY while the first is EXCLUSIVE", cmd(pb, wb, "lock.lock", "b|Shared"), "false")
        cmd(pa, wa, "lock.unlock", "a|None")
        step(group, "second SHARED succeeds after release", cmd(pb, wb, "lock.lock", "b|Shared"), "true")
        step(group, "second RESERVED succeeds after release", cmd(pb, wb, "lock.lock", "b|Reserved"), "true")
        step(group, "second EXCLUSIVE succeeds", cmd(pb, wb, "lock.lock", "b|Exclusive"), "true")
        step(group, "second closes", cmd(pb, wb, "lock.close", "b"), "ok")
        step(group, "first closes", cmd(pa, wa, "lock.close", "a"), "ok")

    writer_scenario("locks/page-page", (p1, "page"), (p2, "page"))
    writer_scenario("locks/page-worker", (p1, "page"), (p1, "worker"))
    writer_scenario("locks/worker-page2", (p1, "worker"), (p2, "page"))

    # The worker backend: the platform lets one context hold a file's sync handle, so the second context is refused at open.
    spath = f"locks/sync-{int(time.time())}.db"
    step("locks/sync-handle", "first worker opens", cmd(p1, "worker", "lock.open", f"w1|sync|{spath}"), "ok")
    step("locks/sync-handle", "first worker SHARED+RESERVED", cmd(p1, "worker", "lock.lock", "w1|Shared") + cmd(p1, "worker", "lock.lock", "w1|Reserved"), "truetrue")
    r = cmd(p2, "worker", "lock.open", f"w2|sync|{spath}")
    res.add("locks/sync-handle", "second worker is BUSY while the first holds the file", r.startswith("BUSY"), r)
    step("locks/sync-handle", "first worker closes", cmd(p1, "worker", "lock.close", "w1"), "ok")
    step("locks/sync-handle", "second worker opens after release", cmd(p2, "worker", "lock.open", f"w2|sync|{spath}"), "ok")
    step("locks/sync-handle", "second worker closes", cmd(p2, "worker", "lock.close", "w2"), "ok")


if __name__ == "__main__":
    sys.exit(main())
