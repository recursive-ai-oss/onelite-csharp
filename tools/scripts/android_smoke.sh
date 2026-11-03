#!/usr/bin/env bash
# Builds the Android smoke app (Release, trimmed, warnings as errors), boots a headless x86_64 emulator, installs the
# app, starts it and waits for the one result line in logcat. Exits 0 only on "mobile smoke: ok".
#
# Needs: a .NET 10 SDK with the `android` workload (dotnet workload install android), a JDK 17+ (JAVA_HOME), curl and
# unzip, and read/write access to /dev/kvm. Everything else is fetched once into $ONELITE_MOBILE (default ~/onelite/mobile):
# the Android command-line tools, platform-tools, the emulator and one system image. Running this script accepts the
# Android SDK licences for that user-local SDK.
#
#   tools/scripts/android_smoke.sh            build, boot, install, run, shut the emulator down
#   BUILD_ONLY=1 tools/scripts/android_smoke.sh   build and report the app size only
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mobile="${ONELITE_MOBILE:-$HOME/onelite/mobile}"
sdk="$mobile/android-sdk"
avd_home="${ONELITE_AVD_HOME:-$mobile/avd}"
api="${ANDROID_API:-36}"
image="system-images;android-$api;google_apis;x86_64"
tools_zip="commandlinetools-linux-15859902_latest.zip"
out="${ONELITE_ARTIFACTS:-$mobile/artifacts}/android"
timeout_s="${SMOKE_TIMEOUT:-600}"
mkdir -p "$mobile" "$out"

: "${JAVA_HOME:?set JAVA_HOME to a JDK 17 or newer}"
export PATH="$JAVA_HOME/bin:$sdk/cmdline-tools/latest/bin:$sdk/platform-tools:$sdk/emulator:$PATH"
export ANDROID_HOME="$sdk" ANDROID_SDK_ROOT="$sdk" ANDROID_AVD_HOME="$avd_home"

if [ ! -x "$sdk/cmdline-tools/latest/bin/sdkmanager" ]; then
  echo "fetching Android command-line tools"
  curl -fsSL -o "$mobile/$tools_zip" "https://dl.google.com/android/repository/$tools_zip"
  mkdir -p "$sdk/cmdline-tools"
  unzip -q -o "$mobile/$tools_zip" -d "$sdk/cmdline-tools"
  rm -rf "$sdk/cmdline-tools/latest"
  mv "$sdk/cmdline-tools/cmdline-tools" "$sdk/cmdline-tools/latest"
fi

# --- build ---------------------------------------------------------------------------------------------------------
proj="$here/../../tests/OneLite.AndroidSmoke/OneLite.AndroidSmoke.csproj"
echo "building (Release, trimmed)"
# The .NET Android targets fetch the platform and build-tools they need into the SDK directory.
dotnet build "$proj" -c Release -t:InstallAndroidDependencies -p:AndroidSdkDirectory="$sdk" -p:JavaSdkDirectory="$JAVA_HOME" \
  -p:AcceptAndroidSDKLicenses=True -v quiet -clp:NoSummary
dotnet build "$proj" -c Release -p:AndroidSdkDirectory="$sdk" -p:JavaSdkDirectory="$JAVA_HOME" -v quiet -clp:NoSummary \
  -p:AndroidKeyStore=false 2>&1 | tee "$out/build.log" | tail -n 20
apk="$(find "${ONELITE_ARTIFACTS:-$here/../../artifacts}" -name '*-Signed.apk' -path '*Android*' 2>/dev/null | head -n 1)"
[ -n "$apk" ] || { echo "no signed apk produced" >&2; exit 2; }
echo "apk: $apk ($(stat -c %s "$apk") bytes)"
if grep -qE ': warning' "$out/build.log"; then
  echo "build produced warnings" >&2; exit 2
fi
[ "${BUILD_ONLY:-0}" = 1 ] && exit 0

# --- emulator ------------------------------------------------------------------------------------------------------
[ -r /dev/kvm ] && [ -w /dev/kvm ] || { echo "/dev/kvm is not readable and writable for this user" >&2; exit 3; }
if [ ! -d "$sdk/emulator" ] || [ ! -d "$sdk/system-images/android-$api/google_apis/x86_64" ]; then
  yes | sdkmanager --licenses >/dev/null || true
  sdkmanager "platform-tools" "emulator" "$image" >/dev/null
fi
mkdir -p "$avd_home"
if ! avdmanager list avd 2>/dev/null | grep -q 'Name: onelite-smoke'; then
  echo no | avdmanager create avd -n onelite-smoke -k "$image" -d pixel_6 --force >/dev/null
fi
emulator -avd onelite-smoke -no-window -no-audio -no-boot-anim -no-snapshot -gpu swiftshader_indirect -no-metrics \
  -port "${EMULATOR_PORT:-5584}" >"$out/emulator.log" 2>&1 &
emu_pid=$!
cleanup() { adb -s "emulator-${EMULATOR_PORT:-5584}" emu kill >/dev/null 2>&1 || true; sleep 2; kill "$emu_pid" 2>/dev/null || true; }
trap cleanup EXIT
serial="emulator-${EMULATOR_PORT:-5584}"
adb start-server >/dev/null
adb -s "$serial" wait-for-device
for _ in $(seq 1 180); do
  [ "$(adb -s "$serial" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = 1 ] && break
  sleep 2
done
echo "booted: $(adb -s "$serial" shell getprop ro.build.version.release | tr -d '\r') (API $(adb -s "$serial" shell getprop ro.build.version.sdk | tr -d '\r'))"

# --- run -----------------------------------------------------------------------------------------------------------
adb -s "$serial" install -r "$apk" >/dev/null
adb -s "$serial" logcat -c
adb -s "$serial" shell monkey -p dev.recursiveai.onelite.smoke -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
adb -s "$serial" logcat -v brief >"$out/logcat.txt" 2>&1 &
logcat_pid=$!
for _ in $(seq 1 "$timeout_s"); do
  if grep -qE 'mobile smoke: (ok|FAILED)' "$out/logcat.txt" 2>/dev/null; then break; fi
  sleep 1
done
kill "$logcat_pid" 2>/dev/null || true
# The runtime copies console output to logcat twice (stdout and the DOTNET tag); keep the first of each line.
grep -E 'DOTNET|OneLiteSmoke' "$out/logcat.txt" | sed -E 's/^[A-Z]\/[^:]*: *//; s/^\( *[0-9]+\): *//' | awk '!seen[$0]++' >"$out/checks.txt" || true
cat "$out/checks.txt"
if grep -q 'mobile smoke: ok' "$out/logcat.txt"; then
  echo "PASS"
  exit 0
fi
echo "FAIL (no pass line within ${timeout_s}s)" >&2
exit 1
