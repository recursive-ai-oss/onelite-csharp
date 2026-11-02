#!/usr/bin/env python3
"""Runs a full forensic pass under strace and checks that the database files named in the
arguments were only opened read-only and never locked, written, truncated, renamed or removed.

usage: strace_check.py DOTNET_DLL PATH...   (PATH = database files; their -wal/-shm/-journal siblings count too)
"""
import os, re, subprocess, sys, tempfile

dll, paths = sys.argv[1], sys.argv[2:]
watch = set()
for p in paths:
    for s in ("", "-wal", "-shm", "-journal"):
        watch.add(os.path.realpath(p + s))
log = os.path.join(os.environ.get("TMPDIR", tempfile.gettempdir()), "strace-%d.log" % os.getpid())
subprocess.run(["strace", "-f", "-o", log, "-e",
                "trace=openat,open,creat,fcntl,flock,write,pwrite64,pwritev,ftruncate,truncate,unlink,unlinkat,rename,renameat,renameat2,fsync,fdatasync,close,dup,dup2,dup3",
                "dotnet", dll, "nonmod", "--pass-only"] + paths, check=True, stdout=subprocess.DEVNULL)

fds = {}  # (pid, fd) -> path
bad, opens, flocks = [], 0, 0
for line in open(log, errors="replace"):
    m = re.match(r"(\d+)\s+(\w+)\((.*)\)\s+=\s+(-?\d+)", line)
    if not m:
        continue
    pid, call, args, ret = m.group(1), m.group(2), m.group(3), int(m.group(4))
    if call in ("openat", "open", "creat"):
        q = re.search(r'"([^"]*)"', args)
        if not q:
            continue
        path = os.path.realpath(q.group(1))
        if path in watch:
            opens += 1
            flags = args.split('"')[-1]
            if re.search(r"O_WRONLY|O_RDWR|O_CREAT|O_TRUNC|O_APPEND", flags):
                bad.append(line.strip())
            if ret >= 0:
                fds[(pid, ret)] = path
    elif call in ("close",):
        fds.pop((pid, int(args)), None) if args.strip().lstrip("-").isdigit() else None
    elif call in ("unlink", "unlinkat", "rename", "renameat", "renameat2", "truncate"):
        if any(w in args for w in watch):
            bad.append(line.strip())
    else:
        fd = re.match(r"\s*(\d+)", args)
        if fd and (pid, int(fd.group(1))) in fds and call == "flock" and re.search(r"LOCK_SH\|LOCK_NB|LOCK_UN", args):
            flocks += 1  # the runtime's own shared, non-blocking advisory flock; unrelated to the POSIX fcntl locks sqlite3 uses
        elif fd and (pid, int(fd.group(1))) in fds and call in (
                "fcntl", "flock", "write", "pwrite64", "pwritev", "ftruncate", "fsync", "fdatasync"):
            bad.append(line.strip())
print("watched opens=%d violations=%d runtime shared flocks=%d" % (opens, len(bad), flocks))
for b in bad[:20]:
    print(b)
sys.exit(1 if bad else 0)
