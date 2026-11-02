#!/usr/bin/env bash
# Installs the headless browser and driver that run the browser tests, user-locally, at pinned versions.
#
#   ONELITE_HOME   install root (default ~/onelite/browser)
#
# Installs:
#   $ONELITE_HOME/chrome-linux64/chrome        Chrome for Testing, version and SHA-256 pinned below
#   $ONELITE_HOME/venv                         a uv virtual environment holding the pinned Playwright driver (Python); it drives the browser above,
#                                              and downloads no browser of its own
# Nothing is installed system-wide. Chrome for Testing is Google's build of Chrome made for automated testing
# (https://googlechromelabs.github.io/chrome-for-testing/).
set -euo pipefail

CHROME_VERSION=156.0.8078.80
CHROME_URL="https://storage.googleapis.com/chrome-for-testing-public/$CHROME_VERSION/linux64/chrome-linux64.zip"
CHROME_SHA256=b711c720f774be355197f32edf37e0651418ba036d92446ee2da19ac017540a7
PLAYWRIGHT_VERSION=1.55.0

HOME_DIR=${ONELITE_HOME:-$HOME/onelite/browser}
mkdir -p "$HOME_DIR"
cd "$HOME_DIR"

if [ ! -x chrome-linux64/chrome ]; then
  curl -fsSL -o chrome.zip "$CHROME_URL"
  echo "$CHROME_SHA256  chrome.zip" | sha256sum -c - >/dev/null || { echo "chrome.zip hash mismatch" >&2; rm -f chrome.zip; exit 1; }
  rm -rf chrome-linux64
  # zipfile drops the permission bits, so put them back from the archive's own records.
  python3 - <<'PY'
import os, zipfile
z = zipfile.ZipFile('chrome.zip')
for info in z.infolist():
    z.extract(info, '.')
    mode = info.external_attr >> 16
    if mode:
        os.chmod(info.filename, mode & 0o777)
PY
  rm -f chrome.zip
fi

if [ ! -x venv/bin/python ]; then
  UV=${UV:-$HOME/.local/bin/uv}
  "$UV" venv --python 3.13 venv
fi
if ! venv/bin/python -c "import playwright" 2>/dev/null; then
  "${UV:-$HOME/.local/bin/uv}" pip install --python venv/bin/python "playwright==$PLAYWRIGHT_VERSION" >/dev/null
fi

./chrome-linux64/chrome --version
venv/bin/python -c "import importlib.metadata as m; print('playwright', m.version('playwright'))"
