#!/usr/bin/env bash
# Builds the benchmark project (Release) and runs it. Every argument goes to `OneLite.Bench run`; see `OneLite.Bench --help`.
#   bench/run.sh --rows lookup_rowid,join --levels engine --reps 3 --out ~/onelite/bench/result
# Set BENCH_NO_BUILD=1 to skip the build (build first, then time).
# Defaults: the reference library is ~/onelite/oracles/native-3.53.4/libe_sqlite3.{so,dylib}; pass --native to change it.
# Pass --vec <path to the sqlite-vec loadable extension, without suffix> to include the reference side of the vec0 row.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
art=${ONELITE_ARTIFACTS:-$root/artifacts}
export ONELITE_ARTIFACTS=$art
if [ -z "${BENCH_NO_BUILD:-}" ]; then
  dotnet build bench/OneLite.Bench -c Release -v quiet -clp:NoSummary
fi
BENCH_SDK=$(dotnet --version) exec dotnet "$art/bin/OneLite.Bench/release/OneLite.Bench.dll" run "$@"
