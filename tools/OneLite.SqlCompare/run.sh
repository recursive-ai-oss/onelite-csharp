#!/usr/bin/env bash
# Runs the comparer on the worker (or any machine with the pinned oracles under ~/onelite): ./run.sh <mode> <args...>
set -euo pipefail
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1
export ONELITE_ARTIFACTS=${ONELITE_ARTIFACTS:-$HOME/onelite/u8/artifacts}
here=$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")
(cd "$here" && dotnet build -c Release -v quiet -clp:NoSummary 2>&1 | grep -E "error|rror\(s\)" | head -20)
exec dotnet "$ONELITE_ARTIFACTS/bin/OneLite.SqlCompare/release/OneLite.SqlCompare.dll" "$@"
