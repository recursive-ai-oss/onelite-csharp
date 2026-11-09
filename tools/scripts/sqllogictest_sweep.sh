#!/usr/bin/env bash
# Runs the sqllogictest corpus against an engine as parallel shards of one process each, then writes the report.
#
#   tools/scripts/sqllogictest_sweep.sh OUT_DIR [SHARDS] [extra runner options...]
#
# Environment:
#   SLT_ENGINE       onelite-rw (default: every statement runs on OneLite) | sqlite (the pinned native library) | onelite (read-only;
#                    setup statements written by the native library)
#   ONELITE_HOME     root of the fetched oracles and corpora (default ~/onelite)
#   SLT_CORPUS       the corpus (default $ONELITE_HOME/corpus/sqllogictest)
#   SLT_KNOWN        known-differences file (default testdata/sqllogictest/known-differences.json)
#   SLT_MAX_MINUTES  stop starting files after this long; rerun the same command to resume
#   SLT_EXE          the built runner (default: dotnet run --no-build -c Release on tools/OneLite.SqlLogicTest)
# Results are one JSON file per script under OUT_DIR/files, so a stopped or killed sweep resumes where it left off.
# The exit code is 0 only when every shard finished and the report found no unexplained failure, missing file or stopped file.
set -u
OUT=${1:?usage: sqllogictest_sweep.sh OUT_DIR [SHARDS] [runner options]}
SHARDS=${2:-4}
shift 2 2>/dev/null || shift $#
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
ENGINE=${SLT_ENGINE:-onelite-rw}
HOME_DIR=${ONELITE_HOME:-$HOME/onelite}
CORPUS=${SLT_CORPUS:-$HOME_DIR/corpus/sqllogictest}
KNOWN=${SLT_KNOWN:-$ROOT/testdata/sqllogictest/known-differences.json}
if [ -n "${SLT_EXE:-}" ]; then RUN=("$SLT_EXE"); else RUN=(dotnet run --no-build -c Release --project "$ROOT/tools/OneLite.SqlLogicTest" --); fi
EXTRA=()
if [ -n "${SLT_MAX_MINUTES:-}" ]; then EXTRA+=(--max-minutes "$SLT_MAX_MINUTES"); fi
if [ ! -d "$CORPUS" ]; then
  echo "no corpus at $CORPUS; run: python3 tools/scripts/fetch_corpora.py --only sqllogictest" >&2
  exit 2
fi

mkdir -p "$OUT/logs"
echo "engine $ENGINE, corpus $CORPUS, $SHARDS shards"
pids=()
for i in $(seq 1 "$SHARDS"); do
  "${RUN[@]}" run --corpus "$CORPUS" --out "$OUT" --engine "$ENGINE" --shard "$i/$SHARDS" \
    --scratch "$OUT/scratch/$i" "${EXTRA[@]}" "$@" >"$OUT/logs/shard-$i.log" 2>&1 &
  pids+=($!)
done
rc=0
for p in "${pids[@]}"; do
  wait "$p"
  r=$?
  if [ "$r" -gt "$rc" ]; then rc=$r; fi
done
echo "shards finished, worst exit code $rc"
"${RUN[@]}" report --out "$OUT" --corpus "$CORPUS" --known "$KNOWN" >"$OUT/logs/report.log" 2>&1
report_rc=$?
head -20 "$OUT/logs/report.log"
echo "report exit code $report_rc (summary in $OUT/summary.md)"
if [ "$report_rc" -ne 0 ] && [ "$rc" -eq 0 ]; then rc=$report_rc; fi
exit $rc
