"""Shared helpers for the fetch scripts: hashing, verified downloads, no third-party packages.

Every download is checked against a hash that is written down in the calling script. A file that is already present and
verifies is left alone, so a second run does nothing and prints "ok (present)".
"""
import hashlib
import os
import sys
import time
import urllib.request


def log(msg):
    print(msg, flush=True)


def default_root():
    # Where oracles and corpora are installed unless --root says otherwise.
    return os.environ.get("ONELITE_HOME") or os.path.join(os.path.expanduser("~"), "onelite")


def file_hash(path, algo):
    h = hashlib.new(algo)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def hash_matches(path, algo, expected):
    return os.path.isfile(path) and file_hash(path, algo) == expected


def download(url, dest, retries=4, headers=None):
    """Download url to dest atomically (via a .part file). Returns the number of bytes written."""
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    part = dest + ".part"
    last = None
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers=dict({"User-Agent": "onelite-fetch"}, **(headers or {})))
            with urllib.request.urlopen(req, timeout=120) as r, open(part, "wb") as f:
                n = 0
                while True:
                    chunk = r.read(1 << 20)
                    if not chunk:
                        break
                    f.write(chunk)
                    n += len(chunk)
            os.replace(part, dest)
            return n
        except Exception as e:  # network errors are retried; the last one is reported
            last = e
            time.sleep(2 ** attempt)
    raise RuntimeError("download failed: %s: %s" % (url, last))


def fetch_verified(url, dest, checks):
    """Make sure dest exists and matches every (algo, hex) in checks, downloading when it is missing or wrong.

    Returns True when the file was already present and correct.
    """
    if all(hash_matches(dest, a, h) for a, h in checks):
        return True
    log("  downloading %s" % url)
    download(url, dest)
    for a, h in checks:
        got = file_hash(dest, a)
        if got != h:
            os.remove(dest)
            raise RuntimeError("%s: %s mismatch: expected %s, got %s" % (dest, a, h, got))
    return False


def die(msg):
    print("error: " + msg, file=sys.stderr)
    sys.exit(1)
