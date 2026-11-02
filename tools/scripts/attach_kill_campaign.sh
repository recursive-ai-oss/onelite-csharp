#!/usr/bin/env bash
# Kill campaign for multi-database commits and journal-mode changes: the engine writer commits to main and an attached database in every transaction
# (a super-journal commit), or switches the journal mode between commits, and is killed with SIGKILL at random moments. Both files are judged by the pinned
# sqlite3 library and by OneLite's recovery: integrity_check ok, no lost acknowledged commit, both databases hold the same transactions.
# Usage: attach_kill_campaign.sh [KILLS_PER_CONFIG] [PARALLEL] [LOG_DIR]   (needs OneLite.Durability built in Release and the oracles under ~/onelite)
set -u
kills=${1:-50}
par=${2:-4}
logs=${3:-$HOME/onelite/kill-logs}
art=${ONELITE_ARTIFACTS:-$(cd "$(dirname "$0")/../.." && pwd)/artifacts}
tool=$art/bin/OneLite.Durability/release/OneLite.Durability
work=${TMPDIR:-/tmp}/attach-kill-$$
mkdir -p "$logs" "$work"
# kind journal sync page-size auto-vacuum cache
configs=(
  "attach delete full 4096 0 2000"
  "attach delete normal 1024 1 2000"
  "attach truncate full 512 0 2000"
  "attach persist full 4096 2 2000"
  "attach persist normal 512 1 8"
  "attach delete full 512 0 8"
  "jmode delete full 4096 0 2000"
  "jmode delete normal 1024 1 2000"
  "jmode truncate full 512 0 2000"
  "jmode persist full 4096 0 8"
  "jmode delete full 512 1 8"
  "jmode truncate normal 4096 1 2000"
)
run_one() {
  read -r kind jm sy ps av cache <<<"$1"
  name="$kind-$jm-$sy-p$ps-av$av-c$cache"
  seed=$(( $(printf '%s' "$name" | cksum | cut -d' ' -f1) % 100000 ))
  flag=""; extra="jmode=1"; [ "$kind" = attach ] && { flag="--attach"; extra="attach=1"; }
  "$tool" kill --mode "$jm" --sync "$sy" --kills "$kills" --episode 25 --seed "$seed" --work "$work/$name" $flag \
    --writer "$tool engine-writer {db} {ledger} {journal} {sync} ps=$ps av=$av cache=$cache enc=utf8 $extra" \
    --json "$logs/$name.json" > "$logs/$name.txt" 2>&1
  echo "$name exit=$? $(grep -E '^mode=' "$logs/$name.txt" | cut -c1-260)"
}
export -f run_one
export kills logs work tool
printf '%s\n' "${configs[@]}" | xargs -P "$par" -I{} bash -c 'run_one "{}"'
rc=$?
rm -rf "$work"
exit $rc
