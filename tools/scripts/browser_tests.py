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

    def do_GET(self):
        path = urllib.parse.unquote(self.path.split("?")[0])
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
    return page.evaluate("([w, op, a]) => window.ol.call(w, op, ...a)", [where, op, list(a)])


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


def main():
    a = args()
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
