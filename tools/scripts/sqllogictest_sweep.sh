#!/usr/bin/env bash
# Runs the sqllogictest corpus against an engine as parallel shards of one process each, then writes the report.
#
#   tools/scripts/sqllogictest_sweep.sh OUT_DIR [SHARDS] [extra runner options...]
#
# Environment: SLT_ENGINE (sqlite | onelite, default sqlite), SLT_CORPUS (default ~/onelite/corpus/sqllogictest),
# SLT_KNOWN (known-differences file, default testdata/sqllogictest/known-differences.json), SLT_MAX_MINUTES
# (stop starting files after this long; rerun the same command to resume), SLT_EXE (the built runner; default: dotnet run).
# Results are one JSON file per script under OUT_DIR/files, so a stopped or killed sweep resumes where it left off.
set -u
OUT=${1:?usage: sqllogictest_sweep.sh OUT_DIR [SHARDS] [runner options]}
SHARDS=${2:-4}
shift 2 2>/dev/null || shift $#
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
ENGINE=${SLT_ENGINE:-sqlite}
CORPUS=${SLT_CORPUS:-$HOME/onelite/corpus/sqllogictest}
KNOWN=${SLT_KNOWN:-$ROOT/testdata/sqllogictest/known-differences.json}
if [ -n "${SLT_EXE:-}" ]; then RUN=("$SLT_EXE"); else RUN=(dotnet run --no-build -c Release --project "$ROOT/tools/OneLite.SqlLogicTest" --); fi
EXTRA=()
if [ -n "${SLT_MAX_MINUTES:-}" ]; then EXTRA+=(--max-minutes "$SLT_MAX_MINUTES"); fi

mkdir -p "$OUT/logs"
pids=()
for i in $(seq 1 "$SHARDS"); do
  "${RUN[@]}" run --corpus "$CORPUS" --out "$OUT" --engine "$ENGINE" --shard "$i/$SHARDS" \
    --scratch "$OUT/scratch/$i" "${EXTRA[@]}" "$@" >"$OUT/logs/shard-$i.log" 2>&1 &
  pids+=($!)
done
rc=0
for p in "${pids[@]}"; do wait "$p" || { r=$?; [ "$r" -gt "$rc" ] && rc=$r; }; done
echo "shards finished, worst exit code $rc"
"${RUN[@]}" report --out "$OUT" --corpus "$CORPUS" --known "$KNOWN" | head -20
exit $rc
