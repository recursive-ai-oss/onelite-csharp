#!/usr/bin/env python3
"""Install the pinned oracle binaries under <root>/oracles (default ~/onelite/oracles).

Layout (the same on every machine):
  oracles/dl/                          downloaded archives, kept so a re-run can verify without the network
  oracles/sqlite-3.53.4/               sqlite3, sqldiff, sqlite3_analyzer, sqlite3_rsync   (sqlite.org precompiled tools)
  oracles/native-3.53.4/               libe_sqlite3.so | libe_sqlite3.dylib                 (native library for P/Invoke in tools and bench)
  oracles/sqlcipher/usr/bin/sqlcipher  Linux x64: Ubuntu noble packages unpacked user-locally
  oracles/sqlcipher/current/bin/sqlcipher  macOS arm64: Homebrew bottle, libcrypto path rewritten, ad-hoc signed

The binaries are only ever run, never built and never committed. Re-running with everything in place is a no-op.

usage: fetch_oracles.py [--root DIR] [--platform linux-x64|osx-arm64] [--skip-sqlcipher]
"""
import argparse
import glob
import os
import platform
import shutil
import subprocess
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fetchlib import default_root, die, fetch_verified, file_hash, log  # noqa: E402

SQLITE_VERSION = "3.53.4"

# sqlite.org precompiled tools. SHA3-256 as pinned; SHA-256 is recorded too so tools without SHA3 can check.
TOOLS = {
    "linux-x64": dict(
        url="https://sqlite.org/2026/sqlite-tools-linux-x64-3530400.zip",
        sha3_256="6eeb57e8f2aef7687f9f016a980992cf2799c8c07a87c5e21495530f91915047",
        sha256="7a6f4d1720e4bc13faa3d934bfce37b816a496c0a2480deacd64cfd8be6cf224"),
    "osx-arm64": dict(
        url="https://sqlite.org/2026/sqlite-tools-osx-arm64-3530400.zip",
        sha3_256="58d53e0eb69c17cabebed2754bf399e4d44939be42dcf194769c00078bfd776d",
        sha256="e56768d61017ab3dbd9ff5690b5551846d9945830fdc0b8a9610df722f05e33b"),
    # Windows runs only the lock-interop tests against a live shell (no native library, no SQLCipher).
    "win-x64": dict(
        url="https://sqlite.org/2026/sqlite-tools-win-x64-3530400.zip",
        sha3_256="88b4659fe747896b853af10157316b4ade143553efb89c1c8ca7423a278dcc8b",
        sha256="f46ee2475de4cbe287e6e5f7d43c838796b14e7379cd216bdbb28d391429f9fc"),
}

# The same SQLite release as a native library, from the SQLite package that SQLitePCLRaw's e_sqlite3 bundle uses.
# Only runtimes/<rid>/native/* is extracted.
NUPKG = dict(
    url="https://api.nuget.org/v3-flatcontainer/sqlite/3.53.4/sqlite.3.53.4.nupkg",
    sha3_256="05137809704da9c8d98b2ead1e2e30eb6744f00c5b6f0cea43318d72a5babc02",
    sha256="5641eb58e88429fd73d09fc294bdaa610a6e99099f4141de912a5e4aa6d773ca")

# SQLCipher 4.5.6 community from Ubuntu noble (x64): the CLI package and its library.
DEBS = {
    "sqlcipher_4.5.6-1build2_amd64.deb": "237b2dbee00330358f62edc86203180e7b60d3a7ed100b0a3f5c0f65c9f7865e",
    "libsqlcipher1_4.5.6-1build2_amd64.deb": "30ffc3589facffbd72fc8720fb5ab7448b4dfb6e99929f0f41b4b3321f45a611",
}

# SQLCipher 4.20.0 community from the Homebrew bottle for macOS arm64.
BOTTLE_TAG = "arm64_golden_gate"
BOTTLE_SHA256 = "3dfcba2386d9c18d2e143dd4b0dac1d797fe5db5741a8bb5035651eeb5e9dea4"


def detect_platform():
    sysname, machine = platform.system(), platform.machine().lower()
    if sysname == "Linux" and machine in ("x86_64", "amd64"):
        return "linux-x64"
    if sysname == "Darwin" and machine == "arm64":
        return "osx-arm64"
    if sysname == "Windows" and machine in ("amd64", "x86_64"):
        return "win-x64"
    die("unsupported platform %s/%s" % (sysname, machine))


def run(cmd, **kw):
    subprocess.run(cmd, check=True, **kw)


def install_sqlite_tools(oracles, plat):
    t = TOOLS[plat]
    dest_dir = os.path.join(oracles, "sqlite-" + SQLITE_VERSION)
    zip_path = os.path.join(oracles, "dl", os.path.basename(t["url"]))
    present = fetch_verified(t["url"], zip_path, [("sha3_256", t["sha3_256"]), ("sha256", t["sha256"])])
    log("sqlite tools %s: sha3-256 %s.. sha-256 %s.. ok (%s)" % (plat, t["sha3_256"][:12], t["sha256"][:12], "present" if present else "downloaded"))
    exe = os.path.join(dest_dir, "sqlite3.exe" if plat == "win-x64" else "sqlite3")
    if os.path.isfile(exe) and version_of(exe) == SQLITE_VERSION:
        log("  %s: %s, no-op" % (exe, SQLITE_VERSION))
        return
    os.makedirs(dest_dir, exist_ok=True)
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            name = os.path.basename(info.filename)   # flatten; the archive holds four executables
            if not name or info.is_dir():
                continue
            target = os.path.join(dest_dir, name)
            with z.open(info) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            os.chmod(target, 0o755)
    if plat == "osx-arm64":
        subprocess.run(["xattr", "-dr", "com.apple.quarantine", dest_dir], check=False, stderr=subprocess.DEVNULL)
    v = version_of(exe)
    if v != SQLITE_VERSION:
        die("installed sqlite3 reports %r, expected %s" % (v, SQLITE_VERSION))
    log("  installed into %s (sqlite3 %s)" % (dest_dir, v))


def version_of(exe):
    try:
        out = subprocess.run([exe, "--version"], capture_output=True, text=True, timeout=30).stdout
        return out.split()[0] if out else None
    except Exception:
        return None


def install_native(oracles, plat):
    dest_dir = os.path.join(oracles, "native-" + SQLITE_VERSION)
    lib = "libe_sqlite3.so" if plat == "linux-x64" else "libe_sqlite3.dylib"
    nupkg = os.path.join(oracles, "dl", "sqlite.%s.nupkg" % SQLITE_VERSION)
    present = fetch_verified(NUPKG["url"], nupkg, [("sha3_256", NUPKG["sha3_256"]), ("sha256", NUPKG["sha256"])])
    log("native e_sqlite3 nupkg: sha3-256 %s.. sha-256 %s.. ok (%s)" % (NUPKG["sha3_256"][:12], NUPKG["sha256"][:12], "present" if present else "downloaded"))
    target = os.path.join(dest_dir, lib)
    if os.path.isfile(target):
        log("  %s present, no-op" % target)
        return
    prefix = "runtimes/%s/native/" % plat
    os.makedirs(dest_dir, exist_ok=True)
    with zipfile.ZipFile(nupkg) as z:
        names = [n for n in z.namelist() if n.startswith(prefix) and not n.endswith("/")]
        if not names:
            die("no %s* in the nupkg" % prefix)
        for n in names:
            with z.open(n) as src, open(os.path.join(dest_dir, os.path.basename(n)), "wb") as dst:
                shutil.copyfileobj(src, dst)
    log("  extracted %s into %s" % (", ".join(os.path.basename(n) for n in names), dest_dir))


def install_sqlcipher_linux(oracles):
    root = os.path.join(oracles, "sqlcipher")
    exe = os.path.join(root, "usr", "bin", "sqlcipher")
    dl = os.path.join(oracles, "dl")
    for name, sha in DEBS.items():
        path = os.path.join(dl, name)
        if os.path.isfile(path) and file_hash(path, "sha256") == sha:
            log("sqlcipher %s: sha-256 %s.. ok (present)" % (name, sha[:12]))
            continue
        pkg, ver = name.split("_")[0], name.split("_")[1]
        os.makedirs(dl, exist_ok=True)
        log("  apt-get download %s=%s" % (pkg, ver))
        run(["apt-get", "download", "%s=%s" % (pkg, ver)], cwd=dl)
        got = file_hash(path, "sha256")
        if got != sha:
            os.remove(path)
            die("%s: sha-256 mismatch: expected %s, got %s" % (name, sha, got))
        log("sqlcipher %s: sha-256 %s.. ok (downloaded)" % (name, sha[:12]))
    if os.path.isfile(exe):
        log("  %s present, no-op" % exe)
        return
    os.makedirs(root, exist_ok=True)
    for name in DEBS:
        run(["dpkg-deb", "-x", os.path.join(dl, name), root])
    log("  unpacked into %s" % root)


def install_sqlcipher_mac(oracles):
    root = os.path.join(oracles, "sqlcipher")
    link = os.path.join(root, "current")
    exe = os.path.join(link, "bin", "sqlcipher")
    if os.path.isfile(exe):
        log("sqlcipher %s present, no-op" % exe)
        return
    run(["brew", "fetch", "--bottle-tag=" + BOTTLE_TAG, "sqlcipher"], stdout=subprocess.DEVNULL)
    bottle = subprocess.run(["brew", "--cache", "--bottle-tag=" + BOTTLE_TAG, "sqlcipher"], capture_output=True, text=True, check=True).stdout.strip()
    got = file_hash(bottle, "sha256")
    if got != BOTTLE_SHA256:
        die("sqlcipher bottle sha-256 %s does not match the pinned %s" % (got, BOTTLE_SHA256))
    log("sqlcipher bottle: sha-256 %s ok" % got)
    run(["tar", "xzf", bottle, "-C", root])
    # The bottle unpacks to sqlcipher/<version>/. Point `current` at it.
    vdirs = sorted(glob.glob(os.path.join(root, "sqlcipher", "*")))
    if not vdirs:
        die("bottle did not unpack to sqlcipher/<version>")
    vdir = vdirs[-1]
    if os.path.islink(link):
        os.remove(link)
    os.symlink(vdir, link)
    # The bottle refers to its libcrypto through a placeholder; point it at the Homebrew prefix and re-sign ad hoc
    # (rewriting load commands invalidates the signature, and arm64 refuses unsigned code).
    prefix = subprocess.run(["brew", "--prefix"], capture_output=True, text=True, check=True).stdout.strip()
    run(["brew", "install", "openssl@4"], stdout=subprocess.DEVNULL) if not os.path.isdir(prefix + "/opt/openssl@4") else None
    binpath = os.path.join(vdir, "bin", "sqlcipher")
    os.chmod(binpath, 0o755)
    otool = subprocess.run(["otool", "-L", binpath], capture_output=True, text=True, check=True).stdout
    for line in otool.splitlines()[1:]:
        dep = line.strip().split(" ")[0]
        if "@@HOMEBREW_PREFIX@@" in dep or "@@HOMEBREW_CELLAR@@" in dep:
            new = dep.replace("@@HOMEBREW_PREFIX@@", prefix).replace("@@HOMEBREW_CELLAR@@", prefix + "/Cellar")
            run(["install_name_tool", "-change", dep, new, binpath])
    run(["codesign", "--force", "--sign", "-", binpath], stderr=subprocess.DEVNULL)
    subprocess.run(["xattr", "-dr", "com.apple.quarantine", vdir], check=False, stderr=subprocess.DEVNULL)
    out = subprocess.run([exe, "--version"], capture_output=True, text=True).stdout.strip()
    log("  installed %s: %s" % (exe, out))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=default_root(), help="install root (default ~/onelite or $ONELITE_HOME)")
    ap.add_argument("--platform", choices=sorted(TOOLS), default=None)
    ap.add_argument("--skip-sqlcipher", action="store_true")
    a = ap.parse_args()
    plat = a.platform or detect_platform()
    oracles = os.path.join(os.path.expanduser(a.root), "oracles")
    os.makedirs(os.path.join(oracles, "dl"), exist_ok=True)
    install_sqlite_tools(oracles, plat)
    if plat != "win-x64":
        install_native(oracles, plat)
    if plat == "win-x64":
        pass
    elif not a.skip_sqlcipher:
        if plat == "linux-x64":
            install_sqlcipher_linux(oracles)
        else:
            install_sqlcipher_mac(oracles)
    log("done")


if __name__ == "__main__":
    main()
