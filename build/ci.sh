#!/usr/bin/env bash
# What CI runs on linux-x64: build, test, NativeAOT smoke publish, browser-wasm smoke build, pack.
# Run from anywhere; needs the .NET SDK from global.json on PATH and, for the AOT step, a C toolchain (clang or gcc) and zlib.
# Set ONELITE_ARTIFACTS to keep build output elsewhere than <repo>/artifacts.
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
art=${ONELITE_ARTIFACTS:-$root/artifacts}
export ONELITE_ARTIFACTS=$art
packages=$art/packages

step() { printf '\n== %s\n' "$*"; }

step "restore"
dotnet restore OneLite.slnx

step "build (Release, warnings are errors)"
dotnet build OneLite.slnx -c Release --no-restore

step "test"
dotnet test --solution OneLite.slnx -c Release --no-build

step "build and test (Checked)"
dotnet build OneLite.slnx -c Checked --no-restore
dotnet test --solution OneLite.slnx -c Checked --no-build

step "NativeAOT smoke: publish with warnings as errors, then run"
rm -rf "$art/smoke"
dotnet publish tests/OneLite.AotSmoke -c Release -r linux-x64 -o "$art/smoke" -warnaserror
"$art/smoke/OneLite.AotSmoke"

step "NativeAOT smoke: Microsoft.Data.Sqlite on the SQLitePCLRaw adapter"
rm -rf "$art/smoke-adapter"
dotnet publish tests/OneLite.AotSmokeAdapter -c Release -r linux-x64 -o "$art/smoke-adapter" -warnaserror
"$art/smoke-adapter/OneLite.AotSmokeAdapter"

step "browser-wasm smoke: build"
dotnet build tests/OneLite.WasmSmoke -c Release -warnaserror

step "pack"
rm -rf "$packages"
dotnet pack OneLite.slnx -c Release --no-build -o "$packages"
ls -l "$packages"

step "package check: ids, versions, dependencies"
version=$(dotnet msbuild src/OneLite -getProperty:PackageVersion | tr -d "[:space:]")
# The core has no dependencies; the other libraries depend on the core, and the adapter also on SQLitePCLRaw.core.
declare -A expected=(
  [RecursiveAI.OneLite]=''
  [RecursiveAI.OneLite.Data]='RecursiveAI.OneLite '
  [RecursiveAI.OneLite.SQLitePCLRaw]='RecursiveAI.OneLite SQLitePCLRaw.core '
  [RecursiveAI.OneLite.Cli]=''
)
for id in "${!expected[@]}"; do
  [ -f "$packages/$id.$version.nupkg" ] || { echo "missing $id nupkg"; exit 1; }
  [ -f "$packages/$id.$version.snupkg" ] || { echo "missing $id snupkg"; exit 1; }
  nuspec=$(unzip -p "$packages/$id.$version.nupkg" "$id.nuspec")
  deps=$(printf '%s' "$nuspec" | { grep -o '<dependency id="[^"]*"' || true; } | sed 's/<dependency id=//' | tr -d '"' | sort | tr '\n' ' ')
  echo "$id depends on: ${deps:-nothing}"
  [ "$deps" = "${expected[$id]}" ] || { echo "unexpected dependencies for $id"; exit 1; }
done

echo
echo "ci.sh: ok"
