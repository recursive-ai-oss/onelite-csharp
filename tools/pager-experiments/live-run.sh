#!/bin/sh
# usage: live-run.sh ARTIFACTS [ROUNDS] [ALTERNATIONS]
# A live sqlite3 reader and a live sqlite3 writer beside our pager, on Chinook. First a baseline without the reader (commit latency on this
# disk), then with the reader loop, then alternating writers.
A=$1; R=${2:-100}; ALT=${3:-80}; W=~/onelite/u5/live; rm -rf "$W"
export ONELITE_SQLITE3=~/onelite/oracles/sqlite-3.53.4/sqlite3
T=$A/bin/OneLite.PagerKill/checked/OneLite.PagerKill
for NR in 1 0; do
  echo "--- noreader=$NR"
  $T live --src ~/onelite/corpus/realworld/chinook.sqlite --work "$W" \
    --sql "update Track set Milliseconds = Milliseconds + 1 - 2*(Milliseconds & 1);" \
    --probe "select sum(Milliseconds) from Track" \
    --other-sql "update Customer set SupportRepId = SupportRepId + 1 - 2*(SupportRepId & 1)" \
    --other-probe "select sum(SupportRepId) from Customer" --rounds $R --alternations $ALT --noreader $NR
done
