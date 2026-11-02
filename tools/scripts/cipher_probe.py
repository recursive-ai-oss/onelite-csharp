#!/usr/bin/env python3
"""Black-box probe of what the SQLCipher shell puts in its rollback journal and WAL.

Environment: SQLCIPHER (shell command line), WORK (scratch dir). Prints facts only; it reads files the shell wrote and never touches its source.
Questions answered:
  1. Are journal page records and WAL frame payloads stored encrypted (same bytes the database file would hold)?
  2. Is the journal record checksum / WAL frame checksum computed over the stored (encrypted) bytes?
Both checks use only the documented formats (fileformat2 'rollback journal', walformat 'checksum algorithm').
"""
import os, shutil, struct, subprocess, sys

SQLCIPHER = os.environ["SQLCIPHER"].split()
WORK = os.environ["WORK"]
os.makedirs(WORK, exist_ok=True)


def hold(db, setup, sql):
    """Run setup to completion, then start sql in a shell that is killed (SIGKILL) while it waits, leaving its journal or WAL behind."""
    run(db, setup)
    p = subprocess.Popen(SQLCIPHER + [db], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    p.stdin.write(sql + "\n")
    p.stdin.flush()
    import time
    time.sleep(1.5)
    p.kill()
    p.wait()


def run(db, sql):
    p = subprocess.run(SQLCIPHER + [db, sql], capture_output=True, text=True)
    return p.stdout.strip() + p.stderr.strip()


def journal_cksum(nonce, page):
    c = nonce
    i = len(page) - 200
    while i > 0:
        c = (c + page[i]) & 0xFFFFFFFF
        i -= 200
    return c


def wal_cksum(data, s0, s1, big):
    fmt = (">" if big else "<") + "%dI" % (len(data) // 4)
    w = struct.unpack(fmt, data)
    for i in range(0, len(w), 2):
        s0 = (s0 + w[i] + s1) & 0xFFFFFFFF
        s1 = (s1 + w[i + 1] + s0) & 0xFFFFFFFF
    return s0, s1


def probe_journal(ps, mode):
    d = os.path.join(WORK, f"j{ps}{mode}")
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    db = os.path.join(d, "a.db")
    pre = f"PRAGMA key='k'; PRAGMA cipher_page_size={ps}; "
    run(db, pre + "CREATE TABLE t(a,b); INSERT INTO t VALUES(1,randomblob(100));")
    shutil.copy(db, os.path.join(d, "before.db"))
    hold(db, "", pre + "PRAGMA synchronous=OFF; BEGIN; UPDATE t SET b=randomblob(100);")
    j = open(db + "-journal", "rb").read()
    magic, nrec, nonce, init, sector, jps = struct.unpack(">8sIIIII", j[:28])
    before = open(os.path.join(d, "before.db"), "rb").read()
    out = [f"journal ps={ps} {mode}: magic={magic.hex()} nrec={nrec} init={init} sector={sector} pagesize={jps}"]
    off = sector
    n = 0
    while off + 8 + ps <= len(j) and n < 4:
        pgno = struct.unpack(">I", j[off:off + 4])[0]
        page = j[off + 4:off + 4 + ps]
        ck = struct.unpack(">I", j[off + 4 + ps:off + 8 + ps])[0]
        stored = before[(pgno - 1) * ps:pgno * ps] if pgno >= 1 else b""
        out.append(f"  rec pgno={pgno} cksum={ck:#x} over-record-bytes={journal_cksum(nonce, page):#x} "
                   f"match={ck == journal_cksum(nonce, page)} same-as-db-bytes={page == stored} "
                   f"zero={not any(page)}")
        off += 8 + ps
        n += 1
    return "\n".join(out)


def probe_wal(ps):
    d = os.path.join(WORK, f"w{ps}")
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    db = os.path.join(d, "a.db")
    pre = f"PRAGMA key='k'; PRAGMA cipher_page_size={ps}; "
    # keep the wal: a shell exit would checkpoint, so write through a connection that is killed by sqlite3's wal_autocheckpoint=0
    hold(db, "", pre + "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t(a,b); INSERT INTO t VALUES(1,randomblob(100));")
    if not os.path.exists(db + "-wal") or os.path.getsize(db + "-wal") == 0:
        return f"wal ps={ps}: no wal left after shell exit ({os.listdir(d)})"
    w = open(db + "-wal", "rb").read()
    magic, ver, wps, seq, salt1, salt2, c1, c2 = struct.unpack(">IIIIIIII", w[:32])
    big = magic & 1
    s0, s1 = wal_cksum(w[:24], 0, 0, big)
    out = [f"wal ps={ps}: magic={magic:#x} pagesize={wps} header-cksum-ok={(s0, s1) == (c1, c2)} frames={(len(w) - 32) // (wps + 24)}"]
    off = 32
    n = 0
    while off + 24 + wps <= len(w) and n < 3:
        fh = w[off:off + 24]
        pgno, dbsz, fs1, fs2, k1, k2 = struct.unpack(">IIIIII", fh)
        page = w[off + 24:off + 24 + wps]
        a0, a1 = wal_cksum(fh[:8], s0, s1, big)
        a0, a1 = wal_cksum(page, a0, a1, big)
        out.append(f"  frame pgno={pgno} dbsize={dbsz} cksum-over-stored-bytes-ok={(a0, a1) == (k1, k2)} zero={not any(page)} "
                   f"first16={page[:16].hex()}")
        s0, s1 = k1, k2
        off += 24 + wps
        n += 1
    return "\n".join(out)


for ps in (1024, 4096):
    for mode in ("DELETE",):
        print(probe_journal(ps, mode))
    print(probe_wal(ps))
