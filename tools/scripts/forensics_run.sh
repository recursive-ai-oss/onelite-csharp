#!/bin/bash
# Builds the forensic tool on the worker and runs one subcommand: tools/scripts/forensics_run.sh <subcommand> [args...]
# Light work: waits while a benchmark holds the worker, and stops itself if one starts.
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONELITE_ARTIFACTS=$HOME/onelite/u9/artifacts TMPDIR=$HOME/onelite/u9/tmp
mkdir -p "$TMPDIR" "$HOME/onelite/u9/logs"
while [ -e ~/WORKER-BENCH.lock ]; do sleep 20; done
nice dotnet build tools/OneLite.Forensics/OneLite.Forensics.csproj -c Release -v quiet -clp:NoSummary 2>&1 | tail -3
setsid nice dotnet "$ONELITE_ARTIFACTS/bin/OneLite.Forensics/release/OneLite.Forensics.dll" "$@" &
pid=$!
( while kill -0 $pid 2>/dev/null; do
    if [ -e ~/WORKER-BENCH.lock ]; then echo "bench lock appeared; stopping" >&2; kill -- -$pid 2>/dev/null; break; fi
    sleep 20
  done ) &
wait $pid
