#!/usr/bin/env bash
# History-checker campaign for BEGIN CONCURRENT: random workloads against OneLite, checked at serializable (client "onelite")
# and at snapshot isolation (client "onelite-snapshot"), in-process threads and separate processes, 2/4/8 clients,
# list-append and register models. Runs until each level has used its time budget.
#   conc_history_campaign.sh <History.dll> <work dir> [minutes per level (10)] [seed base (1)]
set -u
dll=$1; work=$2; mins=${3:-10}; seed=${4:-1}
mkdir -p "$work"
summary=$work/summary.tsv
: > "$summary"
for level in serializable snapshot-isolation; do
  client=onelite; [ "$level" = snapshot-isolation ] && client=onelite-snapshot
  end=$(( $(date +%s) + mins * 60 ))
  i=0
  while [ "$(date +%s)" -lt "$end" ]; do
    model=$([ $((i % 2)) = 0 ] && echo list-append || echo register)
    exec=$([ $(((i / 2) % 2)) = 0 ] && echo threads || echo processes)
    case $(((i / 4) % 3)) in 0) w=2;; 1) w=4;; *) w=8;; esac
    s=$((seed + i)); db=$work/h.db; rm -f "$db" "$db-wal" "$db-shm"
    log=$work/run-$level-$i.log
    dotnet "$dll" run --client "$client" --db "$db" --model "$model" --exec "$exec" --workers "$w" --txns 1500 \
      --keys 12 --seed "$s" --level "$level" --json "$work/run-$level-$i.json" > "$log" 2>&1
    rc=$?
    echo -e "$level\t$model\t$exec\t$w\t$s\t$rc\t$(grep -m1 -iE 'transactions' "$log" | tr '\t' ' ')" >> "$summary"
    [ $rc -ne 0 ] && cp "$log" "$work/FAIL-$level-$i.log"
    i=$((i + 1))
  done
done
awk -F'\t' '{n[$1]++; if($6!=0)f[$1]++} END{for(l in n) print l, "runs", n[l], "failing", f[l]+0}' "$summary"
