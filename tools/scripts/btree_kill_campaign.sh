#!/usr/bin/env bash
# Process-kill campaign for OneLite's own storage engine: the durability tool's workload run through the B-tree on the pager, killed with
# SIGKILL at random moments, judged by the pinned sqlite3 library (recovery, integrity_check, no lost acknowledged commit).
# Usage: [MODES=wal] btree_kill_campaign.sh [KILLS_PER_CONFIG] [PARALLEL] [LOG_DIR]
# MODES=wal runs the WAL configurations (full and normal, several page sizes, auto-vacuum modes, encodings and cache sizes, small caches that spill
# frames into the log before the commit); the default runs the rollback-journal ones.
# Needs the durability tool built in Release (dotnet build tools/OneLite.Durability -c Release) and the oracles under ~/onelite.
set -u
kills=${1:-100}
par=${2:-4}
logs=${3:-$HOME/onelite/kill-logs}
art=${ONELITE_ARTIFACTS:-$(cd "$(dirname "$0")/../.." && pwd)/artifacts}
tool=$art/bin/OneLite.Durability/release/OneLite.Durability
work=${TMPDIR:-/tmp}/btree-kill-$$
mkdir -p "$logs" "$work"
# journal sync page-size auto-vacuum cache encoding
configs=(
  "delete full 512 1 2000 utf8"
  "delete normal 512 1 2000 utf8"
  "truncate full 1024 0 2000 utf8"
  "truncate normal 4096 1 2000 utf8"
  "persist full 512 0 2000 utf8"
  "persist normal 1024 1 2000 utf8"
  "delete full 4096 0 2000 utf16le"
  "truncate full 512 2 2000 utf8"
  "persist full 4096 2 8 utf8"
  "delete normal 1024 0 6 utf8"
  "truncate normal 512 0 8 utf16be"
  "persist normal 512 1 8 utf8"
  "delete full 512 1 8 utf8"
  "truncate full 1024 1 8 utf8"
)
run_one() {
  read -r jm sy ps av cache enc <<<"$1"
  name="$jm-$sy-p$ps-av$av-c$cache-$enc"
  seed=$(( $(printf '%s' "$name" | cksum | cut -d' ' -f1) % 100000 ))
  "$tool" kill --mode "$jm" --sync "$sy" --kills "$kills" --episode 25 --seed "$seed" --work "$work/$name" \
    --writer "$tool btree-writer {db} {ledger} {journal} {sync} ps=$ps av=$av cache=$cache enc=$enc" \
    --json "$logs/$name.json" > "$logs/$name.txt" 2>&1
  echo "$name exit=$? $(grep -E '^mode=' "$logs/$name.txt" | cut -c1-260)"
}
export -f run_one
export kills logs work tool
if [ "${MODES:-}" = wal ]; then
  configs=(
    "wal full 512 1 2000 utf8"
    "wal normal 512 0 2000 utf8"
    "wal full 1024 0 2000 utf8"
    "wal normal 4096 1 2000 utf8"
    "wal full 4096 0 2000 utf16le"
    "wal normal 1024 2 2000 utf8"
    "wal full 512 0 8 utf8"
    "wal normal 512 1 8 utf8"
    "wal full 4096 2 6 utf16be"
    "wal normal 1024 0 8 utf8"
  )
fi
printf '%s\n' "${configs[@]}" | xargs -P "$par" -I{} bash -c 'run_one "$@"' _ {}
rm -rf "$work"
