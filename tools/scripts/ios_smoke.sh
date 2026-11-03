#!/usr/bin/env bash
# Builds the iOS smoke app (tests/OneLite.IosSmoke), installs it in an iOS simulator and launches it; passes when the app prints its OK line.
# Needs a Mac with Xcode, an installed iOS simulator runtime and the .NET `ios` workload (`dotnet workload install ios`).
#
#   tools/scripts/ios_smoke.sh [SIMULATOR_NAME_OR_UDID]
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dev=${1:-$(xcrun simctl list devices available | awk -F'[()]' '/iPhone/ {print $2; exit}')}
[ -n "$dev" ] || { echo "no available iPhone simulator" >&2; exit 2; }
xcrun simctl boot "$dev" 2>/dev/null || true
# The simulator runs JIT-compiled code, so the (slow) ahead-of-time compile of Release is switched off.
dotnet build tests/OneLite.IosSmoke -c "${CONFIG:-Release}" -v quiet -clp:NoSummary -p:ValidateXcodeVersion=false -p:UseInterpreter=false -p:MtouchUseLlvm=false -p:RunAOTCompilation=false
app=$(find "${ONELITE_ARTIFACTS:-$root/artifacts}" tests/OneLite.IosSmoke -name '*.app' -path '*/bin/*' -path '*iossimulator-arm64*' -type d 2>/dev/null | head -1)
[ -n "$app" ] || { echo "app bundle not found" >&2; exit 3; }
xcrun simctl install "$dev" "$app"
out=$(xcrun simctl launch --console-pty "$dev" dev.recursiveai.onelite.smoke 2>&1 || true)
echo "$out" | tail -20
xcrun simctl shutdown "$dev" 2>/dev/null || true
echo "$out" | grep -q "ONELITE-IOS-SMOKE: OK"
