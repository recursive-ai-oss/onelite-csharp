#!/bin/sh
# Black-box probe: which files the pinned sqlite3 shell creates or changes, and which result it gives, when it opens a WAL-mode database
# read-only in various states; then the same states through OneLite's `querydiff run`.
# usage: readonly_wal_probe.sh [workdir]     (env: SQLITE3 = the shell, QD = path of OneLite.QueryDiff.dll, DOTNET = dotnet)
SQ=${SQLITE3:-$HOME/onelite/oracles/sqlite-3.53.4/sqlite3}
W=${1:-${TMPDIR:-/tmp}/ro-probe}
rm -rf "$W"; mkdir -p "$W"; cd "$W" || exit 1
# a WAL database with committed frames in the -wal and a valid -shm, captured from a live writer
mkbase() {
  rm -rf base; mkdir base
  (printf 'PRAGMA journal_mode=wal;\ncreate table t(a);\ninsert into t values(1),(2),(3);\n.shell sleep 2\n' | "$SQ" base/d.db >/dev/null) &
  sleep 1
  cp base/d.db base/d.db-wal base/d.db-shm .; wait
}
st() { for f in d.db d.db-wal d.db-shm; do [ -e "$1/$f" ] && printf '%s=%s ' "$f" "$(wc -c < "$1/$f")"; done; echo; }
setup() { D=$W/$1-$2; rm -rf "$D"; mkdir "$D"; cp d.db d.db-wal d.db-shm "$D"/; eval "$3"; }
case_() { # label, setup commands (use $D)
  setup ref "$1" "$2"; ref=$D
  printf '%s\n   before: ' "$1"; st "$ref"
  printf '   sqlite3: '; "$SQ" -readonly "$ref/d.db" "select count(*) from t" 2>&1 | tr '\n' ' '; printf ' -> '; st "$ref"
  setup our "$1" "$2"; our=$D
  printf '   onelite: '
  if [ -n "$QD" ]; then ${DOTNET:-dotnet} "$QD" run "$our/d.db" "select count(*) from t" 2>&1 | tr '\n' ' '; printf ' -> '; st "$our"; else echo skipped; fi
  chmod -R u+w "$ref" "$our"
}
mkbase
case_ wal_shm ':'
case_ shm_stale 'rm -f $D/d.db-wal'
case_ wal_only 'rm -f $D/d.db-shm'
case_ wal_only_dir_ro 'rm -f $D/d.db-shm; chmod a-w $D'
case_ files_ro 'chmod a-w $D/d.db $D/d.db-wal $D/d.db-shm'
case_ files_ro_dir_ro 'chmod a-w $D/d.db $D/d.db-wal $D/d.db-shm $D'
case_ shm_ro_only 'chmod a-w $D/d.db-shm'
case_ nowal_noshm_dir_ro 'rm -f $D/d.db-wal $D/d.db-shm; chmod a-w $D'
case_ nowal_noshm 'rm -f $D/d.db-wal $D/d.db-shm'
case_ wal_ro_shm_absent 'rm -f $D/d.db-shm; chmod a-w $D/d.db-wal'
# a wal-index that needs recovery (empty, or all zeros) with nothing writable
case_ shm_empty_all_ro 'cp /dev/null $D/d.db-shm; chmod a-w $D/d.db $D/d.db-wal $D/d.db-shm $D'
case_ shm_zeros_all_ro 'head -c 32768 /dev/zero > $D/d.db-shm; chmod a-w $D/d.db $D/d.db-wal $D/d.db-shm $D'
case_ shm_zeros_files_rw 'head -c 32768 /dev/zero > $D/d.db-shm'
case_ shm_empty_shm_ro 'cp /dev/null $D/d.db-shm; chmod a-w $D/d.db-shm'
