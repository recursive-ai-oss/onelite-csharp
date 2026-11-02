#!/usr/bin/env bash
# Builds the browser test app and runs it in headless Chrome (see browser_tests.py for what is checked).
#
#   ONELITE_HOME   where fetch_browser.sh installed Chrome and the driver (default ~/onelite/browser)
#   SQLITE3        the sqlite3 shell used to check what the browser wrote
#   CORPUS         space-separated directories of .db files to import (default: none)
#   OUT            scratch directory for the published site and uploads (default $ONELITE_HOME/run)
#   ROWS           rows in the databases the browser writes (default 1500)
# Extra arguments go to browser_tests.py (for example --only pager).
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
home=${ONELITE_HOME:-$HOME/onelite/browser}
out=${OUT:-$home/run}
sqlite3=${SQLITE3:-$HOME/onelite/oracles/sqlite-3.53.4/sqlite3}
mkdir -p "$out"
[ -x "$home/chrome-linux64/chrome" ] || "$root/tools/scripts/fetch_browser.sh"

rm -rf "$out/site"
ONELITE_ARTIFACTS="$out/artifacts" dotnet publish "$root/tests/OneLite.BrowserTests" -c "${CONFIG:-Release}" -o "$out/site" -v quiet -clp:NoSummary

corpus=()
for d in ${CORPUS:-}; do corpus+=(--corpus "$d"); done
exec "$home/venv/bin/python" "$root/tools/scripts/browser_tests.py" --site "$out/site/wwwroot" --chrome "$home/chrome-linux64/chrome" \
  --sqlite3 "$sqlite3" --out "$out/uploads" --rows "${ROWS:-1500}" "${corpus[@]}" "$@"
