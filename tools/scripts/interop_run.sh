#!/usr/bin/env bash
# Two-way file interop of OneLite against the pinned sqlite3 on a fresh generated corpus, with the runner's own reader and writer commands:
# the reader is the B-tree reader (hot journals and WAL logs are recovered), the writer is the SQL engine reading the script's leading layout
# pragmas. Needs the pinned sqlite3 under $ONELITE_HOME (default ~/onelite); see fetch_oracles.py.
#
#   tools/scripts/interop_run.sh [WORKDIR] [REALWORLD_DIR]
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
art=${ONELITE_ARTIFACTS:-$root/artifacts}
export ONELITE_ARTIFACTS=$art
work=${1:-${ONELITE_HOME:-$HOME/onelite}/interop-run}
real=${2:-${ONELITE_HOME:-$HOME/onelite}/corpus/realworld}
mkdir -p "$work"
for p in tools/OneLite.Interop tools/OneLite.SqlRun tools/OneLite.BTreeRead; do dotnet build "$p" -c Release -v quiet -clp:NoSummary; done
bin=$art/bin
dotnet "$bin/OneLite.Interop/release/OneLite.Interop.dll" gen --out "$work/corpus/generated" --force
args=(run --corpus "$work/corpus/generated" --reader "dotnet $bin/OneLite.BTreeRead/release/OneLite.BTreeRead.dll render {db}"
  --writer "dotnet $bin/OneLite.SqlRun/release/OneLite.SqlRun.dll {db} --script {script}" --json "$work/interop.json" --md "$work/interop.md")
[ -d "$real" ] && args+=(--realworld "$real")
exec dotnet "$bin/OneLite.Interop/release/OneLite.Interop.dll" "${args[@]}"
