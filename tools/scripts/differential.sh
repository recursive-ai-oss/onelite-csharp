#!/usr/bin/env bash
# One chunk of a differential campaign: build the tool, run cases from START for DURATION, write the summary and minimized repros to OUT.
# Chunks are independent: the next one starts at the "resume with" line the run prints, so a long campaign is a series of short jobs.
#
#   tools/scripts/differential.sh SEED START DURATION OUT [differential run options...]
#   tools/scripts/differential.sh 1 0 25m out/seed1-0 --jobs 8 --families window,cte
#
# Needs the pinned sqlite3 under $ONELITE_HOME (default ~/onelite); see fetch_oracles.py.
set -euo pipefail
seed=${1:?seed} start=${2:?start case} duration=${3:?duration like 25m} out=${4:?output directory}
shift 4
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
art=${ONELITE_ARTIFACTS:-$root/artifacts}
export ONELITE_ARTIFACTS=$art
dotnet build tools/OneLite.Differential -c Release -v quiet -clp:NoSummary
exec dotnet "$art/bin/OneLite.Differential/release/OneLite.Differential.dll" run --seed "$seed" --start-case "$start" --duration "$duration" --out "$out" "$@"
