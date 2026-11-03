# Platforms

What has been run, and where. "Not run" means there is no host yet, not that it fails.

| Platform | Status |
|---|---|
| linux-x64, linux-arm64 | Full test suite in CI. NativeAOT smoke app clean. |
| osx-arm64 | Full test suite in CI and on a local Apple-silicon machine. File VFS resolves symlinked directories (`/var` to `/private/var`) in full pathnames, as the reference does. |
| win-x64 | Test suite in CI. A hosted-runner job (`windows-interop`) runs the lock tests against a live reference shell; it is marked non-blocking until it has run green once. |
| Browser (WASM) | Headless Chrome tests of the OPFS and memory backends; a kill-the-tab mode (`tools/scripts/browser_tests.sh --kill N`) closes the page mid-write and checks the file after. One connection per origin. Encryption is unsupported there. |
| iOS | `tests/OneLite.IosSmoke` (`tools/scripts/ios_smoke.sh`): the core and `.Data` run the shared smoke checks in an app on the iOS simulator (arm64, trimmed, invariant globalization). The build must use a conventional `bin`/`obj` tree (the shared artifacts layout breaks the runtime libraries copied into the bundle). |
| Android | `tests/OneLite.AndroidSmoke` (`tools/scripts/android_smoke.sh`): the same checks in an app on an x86_64 emulator, trimmed. Needs the `android` workload, a JDK, an Android SDK with the emulator and `/dev/kvm`; run on a self-hosted Linux machine, not on hosted runners. |
