#!/bin/sh
# usage: fuzz-range.sh FIRST_SEED LAST_SEED COUNT [extra querydiff fuzz args]
# Runs querydiff fuzz over the corpus in $ONELITE_CORPUS (default ~/onelite/corpus), one seed after another, appending to $ONELITE_LOGS.
FIRST=$1; LAST=$2; COUNT=$3; shift 3
C=${ONELITE_CORPUS:-$HOME/onelite/corpus}
L=${ONELITE_LOGS:-$HOME/onelite/u13/logs}
mkdir -p "$L"
QD=${QD:-$(find "${ONELITE_ARTIFACTS:-$HOME/onelite/u13/art}/bin" -name OneLite.QueryDiff.dll | head -1)}
s=$FIRST
while [ "$s" -le "$LAST" ]; do
  if [ -e "$HOME/WORKER-BENCH.lock" ]; then echo "bench lock present, stopping at seed $s"; exit 3; fi
  nice dotnet "$QD" fuzz --seed "$s" --count "$COUNT" --jobs 6 --show 600 "$@" "$C/realworld" "$C/generated/db" > "$L/fuzz-$s.log" 2>&1
  tail -1 "$L/fuzz-$s.log"
  s=$((s+1))
done
