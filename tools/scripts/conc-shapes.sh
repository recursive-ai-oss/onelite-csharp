#!/usr/bin/env bash
# Runs the conc-shapes matrix (BEGIN CONCURRENT against BEGIN IMMEDIATE, transactions with a body that takes time).
#   conc-shapes.sh DLL WORKDIR SHAPE [SECONDS] [ISOLATE]
# DLL is the built OneLite.Durability.dll; SHAPE is update, insert, mixed or procs (update in separate processes).
# One line per run on stdout. Needs a real disk file system for WORKDIR (not a RAM-backed /tmp).
set -u
dll=$1; work=$2; shape=$3; secs=${4:-5}; isolate=${5:-thread}
run() { dotnet "$dll" conc-shapes --work "$work" --seconds "$secs" "$@"; }
case $shape in
  update|insert)
    for sync in normal full; do for b in 0 100 1000; do for w in 1 2 4 8; do for m in immediate concurrent; do
      run --shape "$shape" --mode "$m" --writers "$w" --body-us "$b" --sync "$sync" --isolate "$isolate"
    done; done; done; done ;;
  mixed)
    for hot in 5 20; do for sync in normal full; do for b in 100 1000; do for w in 2 4 8; do for m in immediate concurrent; do
      run --shape mixed --hot-pct "$hot" --mode "$m" --writers "$w" --body-us "$b" --sync "$sync" --isolate "$isolate"
    done; done; done; done; done ;;
  procs)
    for sync in normal full; do for b in 100 1000; do for w in 4 8; do for m in immediate concurrent; do
      run --shape update --mode "$m" --writers "$w" --body-us "$b" --sync "$sync" --isolate process
    done; done; done; done ;;
esac
