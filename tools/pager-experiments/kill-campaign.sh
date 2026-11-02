#!/bin/sh
# usage: kill-campaign.sh ARTIFACTS KILLS_PER_CONFIG SEED [MODES [SYNCS]]
# Journal modes (default: delete truncate persist) x {full, normal} x {large cache, tiny cache that forces spills}. Needs dotnet on PATH.
A=$1; K=$2; SEED=$3; MODES=${4:-delete truncate persist}; SYNCS=${5:-full normal}; fail=0; total=0
for mode in $MODES; do for sync in $SYNCS; do for cache in 2000 8; do
  out=$(sh "$(dirname "$0")/kill-smoke.sh" "$A" "$K" "$mode" "$sync" "$cache" "$SEED" 2>&1) || fail=1
  echo "$out" | tail -3
  total=$((total + K))
done; done; done
echo "campaign: $total kills requested, exit $fail"
exit $fail
