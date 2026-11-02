#!/bin/sh
# Shows the bytes of the journal-header writes (first 40 bytes) for a one-row insert, per synchronous setting,
# and what `PRAGMA journal_mode=off` answers. usage: headers.sh SQLITE3 WORKDIR
S=$1; W=$2; mkdir -p "$W"; cd "$W" || exit 1
for sync in off normal full; do
  rm -f h.db h.db-journal
  $S h.db "pragma page_size=4096; create table t(a integer primary key, b); insert into t select value, hex(randomblob(40)) from generate_series(1,200);"
  echo "== delete synchronous=$sync: header-ish writes"
  strace -f -o h.trace -x -s 40 -e trace=pwrite64 $S h.db "pragma synchronous=$sync; begin; update t set b='x' where a=7; commit;" >/dev/null
  grep 'journal\|, 512,\|, 12,\|, 28,' h.trace | grep -E ', (512|12|28), [0-9]+\) = ' | head -3 | cut -c1-200
done
echo "== journal_mode=off answers:"; rm -f h.db; $S h.db "create table t(a); pragma journal_mode=off; begin; insert into t values(1); commit;" ; ls h.db*
