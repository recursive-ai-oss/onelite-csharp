#!/usr/bin/env python3
"""Two-way interop of encrypted databases through the pager, against the SQLCipher shell and sqlite3.

Environment (all required):
  SQLCIPHER  the SQLCipher shell command line (LD_LIBRARY_PATH set by the caller when it needs one)
  SQLITE3    the sqlite3 shell
  CIPHER     command that runs the OneLite.Cipher tool, e.g. "dotnet /path/OneLite.Cipher.dll"
  DURA       command that runs the OneLite.Durability tool
  WORK       scratch directory on a real file system
Optional: IMAGES (crash images, default 300), SEED, ONLY (run only the settings whose name contains this).

read   : the shell writes (page sizes 1024-65536, journal modes DELETE/TRUNCATE/PERSIST/WAL, WAL frames left uncheckpointed by a killed shell,
         overflow and freelist pages, kdf_iter variants, cipher_compatibility 3, raw keys); the tool reads through the pager (pager-dump) and
         sqlite3's integrity_check and .dump of the result must equal the shell's own .dump of an identical copy.
write  : the tool builds the btree workload through the pager under a key; the shell must pass integrity_check and cipher_integrity_check and
         its .dump must equal sqlite3's .dump of the same workload built without a key.
crash  : the shell is killed (SIGKILL) at a random moment while committing; the tool recovers one copy of the file set, the shell another; the dumps must be equal.
"""
import os, random, shutil, subprocess, sys, time

SQLCIPHER = os.environ["SQLCIPHER"].split()
SQLITE3 = os.environ["SQLITE3"].split()
CIPHER = os.environ["CIPHER"].split()
DURA = os.environ["DURA"].split()
WORK = os.environ["WORK"]
os.makedirs(WORK, exist_ok=True)

passed = failed = 0
fails = []
counts = {}


def run(cmd, inp=None, timeout=300):
    p = subprocess.run(cmd, input=inp, capture_output=True, text=True, timeout=timeout)
    return p.returncode, p.stdout, p.stderr


def record(ok, tag, msg=""):
    global passed, failed
    counts[tag] = counts.get(tag, 0) + 1
    if ok:
        passed += 1
    else:
        failed += 1
        fails.append(f"{tag}: {msg}")
        print("FAIL", tag, msg[:300], flush=True)


def copy_set(src, dst):
    for suf in ("", "-wal", "-journal"):
        if os.path.exists(src + suf):
            shutil.copy(src + suf, dst + suf)
        elif os.path.exists(dst + suf):
            os.remove(dst + suf)


# settings: name -> (key text, shell pragmas, tool options, dura words)
RAW = "x'2DD29CA851E7B56E4697B0E1F08507293D761A05CE4D1B628663F411A8086D99'"
RAWS = "x'98483C6EB40B6C31A448C22A66DED3B5E5E8D5119CAC8327B655C8B5C483648101010101010101010101010101010101'"


def cfg(name, key, ps=4096, iters=1000, compat=4, kdf=None, hmac=None):
    pr = [f'PRAGMA key="{key}";']
    if compat == 3:
        pr.append("PRAGMA cipher_compatibility=3;")
    pr.append(f"PRAGMA cipher_page_size={ps};")
    pr.append(f"PRAGMA kdf_iter={iters};")
    tool = ["--key", key, "--compat", str(compat), "--page-size", str(ps), "--kdf-iter", str(iters)]
    dura = [f"key={key}", f"compat={compat}", f"ps={ps}", f"iter={iters}"]
    if kdf:
        pr.append(f"PRAGMA cipher_kdf_algorithm=PBKDF2_HMAC_{kdf.upper()};")
        tool += ["--kdf", kdf]
        dura.append(f"kdf={kdf}")
    if hmac:
        pr.append(f"PRAGMA cipher_hmac_algorithm=HMAC_{hmac.upper()};")
        tool += ["--hmac", hmac]
        dura.append(f"hmac={hmac}")
    return name, key, " ".join(pr), tool, dura


CONFIGS = [cfg(f"v4-p{ps}", f"pass{ps}", ps=ps) for ps in (1024, 2048, 4096, 8192, 16384, 32768, 65536)]
CONFIGS += [cfg(f"v3-p{ps}", f"pass3{ps}", ps=ps, compat=3, iters=1000) for ps in (1024, 4096, 8192)]
CONFIGS += [
    cfg("v4-default-iter", "pw", iters=256000),
    cfg("v4-unicode", "pässwörd✓"),
    cfg("v4-rawkey", RAW),
    cfg("v4-rawkey-salt", RAWS),
    cfg("v3-rawkey", RAW, ps=1024, compat=3),
    cfg("mix-sha256-sha1", "pw", iters=2000, kdf="sha256", hmac="sha1"),
    cfg("mix-sha1-sha512", "pw", iters=2000, kdf="sha1", hmac="sha512"),
    cfg("mix-sha256-sha256", "pw", iters=2000, kdf="sha256", hmac="sha256"),
]

CONTENT = """
CREATE TABLE t1(id INTEGER PRIMARY KEY, a TEXT, b BLOB, c REAL);
CREATE INDEX t1a ON t1(a);
CREATE TABLE t2(x, y);
CREATE VIEW v1 AS SELECT id, a FROM t1 WHERE id % 2 = 0;
WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM s WHERE i<500) INSERT INTO t1 SELECT i, 'row'||i, randomblob((i*37)%3000), i*1.5 FROM s;
INSERT INTO t2 VALUES(NULL, 'x'), (1, zeroblob(70000)), ('é', 2.5);
DELETE FROM t1 WHERE id % 5 = 0;
"""


def shell_dump(db, c):
    """The shell's view of the file as sqlite3 dumps it: the shell exports to a plain database, sqlite3 dumps that (so one dumper judges both sides)."""
    name, key, pragmas, tool, dura = c
    exp = db + ".exp"
    for suf in ("", "-wal", "-journal", "-shm"):
        if os.path.exists(exp + suf):
            os.remove(exp + suf)
    rc, out, err = run(SQLCIPHER + [db], f".output /dev/null\n{pragmas}\n.output stdout\nATTACH DATABASE '{exp}' AS p KEY '';\nSELECT sqlcipher_export('p');\n")
    if rc != 0 or not os.path.exists(exp):
        return 1, "", out + err
    rc, d, e = run(SQLITE3 + [exp, ".dump"])
    return rc, d, e


def our_dump(db, c, tag):
    name, key, pragmas, tool, dura = c
    plain = db + ".plain"
    if os.path.exists(plain):
        os.remove(plain)
    rc, out, err = run(CIPHER + ["pager-dump", db, plain] + tool)
    if rc != 0:
        return None, f"pager-dump rc={rc}: {(out + err)[:200]}"
    rc, ic, _ = run(SQLITE3 + [plain, "PRAGMA integrity_check;"])
    if ic.strip() != "ok":
        return None, "integrity_check: " + ic.strip()[:200]
    rc, d, e = run(SQLITE3 + [plain, ".dump"])
    return d, ""


def compare(db_ours, db_theirs, c, tag, label):
    want = shell_dump(db_theirs, c)
    if "file is not a database" in want[1] + want[2] or want[0] != 0:
        record(False, tag, f"{label}: the shell cannot read its own file: {(want[1] + want[2])[:200]}")
        return
    got, why = our_dump(db_ours, c, tag)
    if got is None:
        record(False, tag, f"{label}: {why}")
        return
    if got != want[1]:
        gl, wl = got.split("\n"), want[1].split("\n")
        first = next((i for i in range(min(len(gl), len(wl))) if gl[i] != wl[i]), min(len(gl), len(wl)))
        record(False, tag, f"{label}: dumps differ ({len(got)} vs {len(want[1])} bytes) at line {first}: ours {gl[first][:100] if first < len(gl) else None!r} theirs {wl[first][:100] if first < len(wl) else None!r}")
        return
    record(True, tag)


def hold(db, script, wait):
    """Start the shell on `script`, wait, SIGKILL it (leaving its journal or WAL behind)."""
    p = subprocess.Popen(SQLCIPHER + [db], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, text=True)
    p.stdin.write(script)
    p.stdin.flush()
    time.sleep(wait)
    p.kill()
    p.wait()


def read_cases():
    for c in CONFIGS:
        name, key, pragmas, tool, dura = c
        if os.environ.get("ONLY") and os.environ["ONLY"] not in name:
            continue
        for mode in ("DELETE", "TRUNCATE", "PERSIST", "WAL", "WAL-HOT"):
            if name.startswith(("v4-p", "v3-p")) is False and mode in ("TRUNCATE", "PERSIST", "WAL-HOT"):
                continue
            d = os.path.join(WORK, f"r-{name}-{mode}")
            shutil.rmtree(d, ignore_errors=True)
            os.makedirs(d)
            db = os.path.join(d, "enc.db")
            if mode == "WAL-HOT":
                hold(db, f"{pragmas}\nPRAGMA journal_mode=WAL;\nPRAGMA wal_autocheckpoint=0;\n{CONTENT}\n", 3.0)
                ok = os.path.exists(db + "-wal") and os.path.getsize(db + "-wal") > 0
                if not ok:
                    record(False, "read", f"{name}-{mode}: the killed shell left no WAL")
                    continue
            else:
                jm = "WAL" if mode == "WAL" else mode
                rc, out, err = run(SQLCIPHER + [db], f"{pragmas}\nPRAGMA journal_mode={jm};\n{CONTENT}\n")
                if rc != 0 or "Error" in out + err:
                    record(False, "read", f"{name}-{mode}: create: {(out + err)[:200]}")
                    continue
            a, b = os.path.join(d, "a.db"), os.path.join(d, "b.db")
            copy_set(db, a)
            copy_set(db, b)
            compare(a, b, c, "read", f"{name}-{mode}")
            shutil.rmtree(d, ignore_errors=True)


def write_cases():
    plain_cache = {}
    for c in CONFIGS:
        name, key, pragmas, tool, dura = c
        if os.environ.get("ONLY") and os.environ["ONLY"] not in name:
            continue
        for mode in ("delete", "truncate", "persist", "wal"):
            if not name.startswith(("v4-p", "v3-p")) and mode in ("truncate", "persist"):
                continue
            d = os.path.join(WORK, f"w-{name}-{mode}")
            shutil.rmtree(d, ignore_errors=True)
            os.makedirs(d)
            enc = os.path.join(d, "enc.db")
            words = [w for w in dura if not w.startswith("iter=")] + [f"mode={mode}", "iter=" + [w for w in dura if w.startswith("iter=")][0][5:]]
            rc, out, err = run(DURA + ["cipher-build", enc, "txns=40"] + words)
            if rc != 0:
                record(False, "write", f"{name}-{mode}: build: {(out + err)[:300]}")
                continue
            # the same workload without a key, once per page size
            ps = [w for w in dura if w.startswith("ps=")][0]
            if ps not in plain_cache:
                pf = os.path.join(WORK, f"plain-{ps[3:]}.db")
                for suf in ("", "-wal", "-journal"):
                    if os.path.exists(pf + suf):
                        os.remove(pf + suf)
                rc, out, err = run(DURA + ["cipher-build", pf, "txns=40", "key=none", ps])
                if rc != 0:
                    record(False, "write", f"plain build: {(out + err)[:300]}")
                    continue
                plain_cache[ps] = run(SQLITE3 + [pf, ".dump"])[1]
            pre = pragmas
            rc, out, err = run(SQLCIPHER + [enc], f".output /dev/null\n{pre}\n.output stdout\nPRAGMA integrity_check;\nselect '@';\nPRAGMA cipher_integrity_check;\nselect '@end';\n")
            lines = out.split("\n")
            ok = "@" in lines and lines[:lines.index("@")] == ["ok"] and lines[lines.index("@") + 1:lines.index("@end")] == []
            if not ok:
                record(False, "write", f"{name}-{mode}: shell checks: {(out + err)[:300]}")
                continue
            want = plain_cache[ps]
            got = shell_dump(enc, c)[1]
            record(got == want, "write", f"{name}-{mode}: dump differs ({len(got)} vs {len(want)} bytes)")
            shutil.rmtree(d, ignore_errors=True)


def crash_cases(n, seed):
    rnd = random.Random(seed)
    cfgs = [c for c in CONFIGS if c[0] in ("v4-p1024", "v4-p4096", "v4-p16384", "v3-p1024", "v3-p4096", "mix-sha256-sha1", "v4-rawkey")]
    made = 0
    hotj = hotw = 0
    while made < n:
        c = cfgs[made % len(cfgs)]
        name, key, pragmas, tool, dura = c
        mode = "WAL" if made % 2 else "DELETE"
        d = os.path.join(WORK, f"c-{made}")
        shutil.rmtree(d, ignore_errors=True)
        os.makedirs(d)
        db = os.path.join(d, "enc.db")
        # many small commits with overflow-sized blobs and deletes, so a kill lands anywhere in a commit
        lines = [pragmas, f"PRAGMA journal_mode={mode};", "PRAGMA wal_autocheckpoint=" + str(rnd.choice([0, 20, 1000])) + ";",
                 "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT, b BLOB); CREATE INDEX ta ON t(a);"]
        for i in range(400):
            lines.append(f"BEGIN; INSERT INTO t(a,b) SELECT 'k'||(abs(random())%100000), randomblob({rnd.choice([10, 300, 2500, 9000])}) FROM (WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM s WHERE i<{rnd.randint(1, 40)}) SELECT i FROM s); "
                         f"DELETE FROM t WHERE id % {rnd.randint(3, 9)} = 0 AND id < (SELECT max(id) FROM t) - 30; COMMIT;")
        hold(db, "\n".join(lines) + "\n", rnd.uniform(0.25, 1.4))
        if not os.path.exists(db) or os.path.getsize(db) == 0:
            shutil.rmtree(d, ignore_errors=True)
            continue
        hj = os.path.exists(db + "-journal") and os.path.getsize(db + "-journal") > 0
        hw = os.path.exists(db + "-wal") and os.path.getsize(db + "-wal") > 0
        hotj += hj
        hotw += hw
        a, b = os.path.join(d, "a.db"), os.path.join(d, "b.db")
        copy_set(db, a)
        copy_set(db, b)
        made += 1
        compare(a, b, c, "crash", f"image {made} {name} {mode}")
        shutil.rmtree(d, ignore_errors=True)
    print(f"crash images: {made}, with a hot journal {hotj}, with a WAL {hotw}", flush=True)


def main():
    what = sys.argv[1] if len(sys.argv) > 1 else "all"
    if what in ("all", "read"):
        read_cases()
    if what in ("all", "write"):
        write_cases()
    if what in ("all", "crash"):
        crash_cases(int(os.environ.get("IMAGES", "300")), int(os.environ.get("SEED", "1")))
    print("passed", passed, "failed", failed, counts)
    for f in fails[:30]:
        print("  FAIL", f[:300])
    return 0 if failed == 0 else 1


sys.exit(main())
