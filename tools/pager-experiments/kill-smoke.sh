#!/bin/sh
# usage: kill-smoke.sh ARTIFACTS KILLS MODE SYNC CACHE SEED  -- runs the page-level kill campaign on Chinook (needs ONELITE_SQLITE3 and the corpus)
A=$1; K=$2; MODE=$3; SYNC=$4; CACHE=$5; SEED=$6
# A tiny cache makes the transaction spill and take longer, so the kill window is wider to reach the commit point as well.
DELAY=250; [ "$CACHE" -lt 100 ] && DELAY=2500
W=~/onelite/u5/kill-$MODE-$SYNC-$CACHE-$SEED
rm -rf "$W"
export ONELITE_SQLITE3=~/onelite/oracles/sqlite-3.53.4/sqlite3
$A/bin/OneLite.PagerKill/checked/OneLite.PagerKill kill --src ~/onelite/corpus/realworld/chinook.sqlite --sql "update Track set Milliseconds = Milliseconds + 1 - 2*(Milliseconds & 1);" --work "$W" --kills "$K" --seed "$SEED" --mode "$MODE" --sync "$SYNC" --cache "$CACHE" --max-delay-ms "$DELAY"
