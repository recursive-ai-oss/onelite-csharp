#!/usr/bin/env python3
"""Fetch the fetch-only corpora under <root>/corpus (default ~/onelite/corpus). Nothing here is ever committed.

  corpus/sqllogictest/   the sqllogictest *test scripts* (the `test/` tree of the pinned mirror commit) and nothing else
  corpus/realworld/      Chinook, Northwind and Sakila SQLite databases

Every file is verified: sqllogictest blobs by their git SHA-1, the databases by SHA-256. A second run re-verifies what is on
disk and downloads nothing. The harness's C source (the mirror's `src/`) is never listed, fetched or opened: the tree is
listed by the id of the `test/` subtree, so no path outside it can appear.

usage: fetch_corpora.py [--root DIR] [--only sqllogictest|realworld] [--jobs N]
Set GITHUB_TOKEN to lift the API rate limit if the unauthenticated limit bites.
"""
import argparse
import hashlib
import json
import os
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fetchlib import default_root, die, download, fetch_verified, log  # noqa: E402

# github.com/gregrahn/sqllogictest is an unofficial mirror of the sqllogictest repository; its COPYRIGHT.md offers the scripts
# under GPL/BSD/MIT/CC0 terms with no attribution needed.
SLT_REPO = "gregrahn/sqllogictest"
SLT_COMMIT = "c93ac416f05635ff5cc490009add936d9753d01d"
SLT_TEST_TREE = "0043d08de7ebe0a4f0dc870a1512f0c7589530f4"   # git tree id of test/ at SLT_COMMIT

REALWORLD = [
    # (file name, url, sha256, licence)
    ("chinook.sqlite", "https://github.com/lerocha/chinook-database/releases/download/v1.4.5/Chinook_Sqlite.sqlite",
     "bdf635be69850bd3be09c9a2dbeef7ddfb80036bd3ef3381383cd03b61e4a61a", "MIT"),
    ("northwind.db", "https://raw.githubusercontent.com/jpwhite3/northwind-SQLite3/4f56e7f5906dfd23b25244c5bfe8fb5da6402efd/dist/northwind.db",
     "2f4f5c68dfcd33ba27373eae48c7a4869800c68095ee0f9f0da494f83382a877", "MIT"),
    ("sakila.db", "https://raw.githubusercontent.com/bradleygrant/sakila-sqlite3/9394b42d13888c3d3d3d56cd7e9c84fadafb71c7/sakila_master.db",
     "88c91a4a1a6b61f9d3f35904c0a173c887b25e73f20c3c2fdb073818c06f4268", "BSD-3-Clause"),
]


def git_blob_sha1(path):
    size = os.path.getsize(path)
    h = hashlib.sha1(b"blob %d\0" % size)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def list_tree(dest):
    cache = os.path.join(dest, ".tree.json")
    if os.path.isfile(cache):
        with open(cache) as f:
            doc = json.load(f)
        if doc.get("sha") == SLT_TEST_TREE and not doc.get("truncated"):
            return doc["entries"]
    url = "https://api.github.com/repos/%s/git/trees/%s?recursive=1" % (SLT_REPO, SLT_TEST_TREE)
    headers = {"User-Agent": "onelite-fetch", "Accept": "application/vnd.github+json"}
    if os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=120) as r:
        doc = json.load(r)
    if doc.get("sha") != SLT_TEST_TREE:
        die("API returned tree %s, expected %s" % (doc.get("sha"), SLT_TEST_TREE))
    if doc.get("truncated"):
        die("tree listing is truncated; the corpus would be incomplete")
    entries = [(e["path"], e["sha"], e["size"]) for e in doc["tree"] if e["type"] == "blob"]
    for p, _, _ in entries:
        if p.startswith("/") or ".." in p.split("/"):
            die("unexpected path in tree: " + p)
    os.makedirs(dest, exist_ok=True)
    with open(cache, "w") as f:
        json.dump({"sha": SLT_TEST_TREE, "truncated": False, "entries": entries}, f)
    return entries


def fetch_blob(dest, path, sha, size):
    target = os.path.join(dest, path)
    if os.path.isfile(target) and os.path.getsize(target) == size and git_blob_sha1(target) == sha:
        return "present", size
    # The URL always lives under test/ at the pinned commit.
    url = "https://raw.githubusercontent.com/%s/%s/test/%s" % (SLT_REPO, SLT_COMMIT, path)
    download(url, target)
    if git_blob_sha1(target) != sha:
        os.remove(target)
        raise RuntimeError("git blob sha-1 mismatch for " + path)
    return "downloaded", size


def fetch_sqllogictest(corpus, jobs):
    dest = os.path.join(corpus, "sqllogictest")
    os.makedirs(dest, exist_ok=True)
    entries = list_tree(dest)
    log("sqllogictest: %d files in tree %s (commit %s)" % (len(entries), SLT_TEST_TREE[:12], SLT_COMMIT[:12]))
    present = downloaded = total = 0
    with ThreadPoolExecutor(max_workers=jobs) as ex:
        futs = [ex.submit(fetch_blob, dest, p, s, z) for p, s, z in entries]
        for fu in as_completed(futs):
            state, size = fu.result()
            total += size
            if state == "present":
                present += 1
            else:
                downloaded += 1
    log("sqllogictest: all %d git blob SHA-1s verified (%d already present, %d downloaded), %d bytes" % (len(entries), present, downloaded, total))


def fetch_realworld(corpus):
    dest = os.path.join(corpus, "realworld")
    for name, url, sha, licence in REALWORLD:
        present = fetch_verified(url, os.path.join(dest, name), [("sha256", sha)])
        log("realworld %s (%s): sha-256 %s.. ok (%s)" % (name, licence, sha[:12], "present" if present else "downloaded"))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=default_root())
    ap.add_argument("--only", choices=["sqllogictest", "realworld"])
    ap.add_argument("--jobs", type=int, default=8)
    a = ap.parse_args()
    corpus = os.path.join(os.path.expanduser(a.root), "corpus")
    if a.only != "sqllogictest":
        fetch_realworld(corpus)
    if a.only != "realworld":
        fetch_sqllogictest(corpus, a.jobs)
    log("done")


if __name__ == "__main__":
    main()
