#!/usr/bin/env bash
# Runs the encrypted-database interop and kill checks on a machine that has the pinned oracles (sqlite3 and the SQLCipher shell).
#
#   cipher_worker.sh interop [read|write|crash|all]   tools/scripts/cipher_pager_interop.py
#   cipher_worker.sh kill MODE [KILLS] [extra settings...]   cipher-kill with the SQLCipher shell as judge (MODE: delete truncate persist wal)
#   cipher_worker.sh rekey MODE [KILLS] [extra settings...]  rekey-kill
#
# Environment: ORACLES (default ~/onelite/oracles), SQLCIPHER_LIB (the SQLCipher shell's library directory, when it needs one),
# SQLCIPHER (the shell; default $ORACLES/sqlcipher/usr/bin/sqlcipher), WORK (scratch, default ~/onelite/cipher-work), SEED, IMAGES, CONFIG (Release|Checked, default Checked).
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
ORACLES=${ORACLES:-$HOME/onelite/oracles}
export SQLCIPHER=${SQLCIPHER:-$ORACLES/sqlcipher/usr/bin/sqlcipher}
if [ -z "${SQLCIPHER_LIB:-}" ] && [ -d "$ORACLES/sqlcipher/usr/lib/x86_64-linux-gnu" ]; then SQLCIPHER_LIB=$ORACLES/sqlcipher/usr/lib/x86_64-linux-gnu; fi
if [ -n "${SQLCIPHER_LIB:-}" ]; then export LD_LIBRARY_PATH=$SQLCIPHER_LIB${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}; fi
export SQLITE3=$ORACLES/sqlite-3.53.4/sqlite3
export ONELITE_SQLCIPHER=$SQLCIPHER
WORK=${WORK:-$HOME/onelite/cipher-work}
CONFIG=${CONFIG:-Checked}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONELITE_ARTIFACTS=${ONELITE_ARTIFACTS:-$HOME/onelite/artifacts}
mkdir -p "$WORK"
cd "$root"
for t in OneLite.Cipher OneLite.Durability; do dotnet build "tools/$t" -c "$CONFIG" -v quiet -clp:NoSummary 2>&1 | grep -E ' error |warning' | head -5 || true; done
bin=$ONELITE_ARTIFACTS/bin
lc=$(echo "$CONFIG" | tr 'A-Z' 'a-z')
export CIPHER="dotnet $bin/OneLite.Cipher/$lc/OneLite.Cipher.dll"
export DURA="dotnet $bin/OneLite.Durability/$lc/OneLite.Durability.dll"
cmd=${1:?}; shift
case "$cmd" in
  interop) WORK=$WORK/interop python3 tools/scripts/cipher_pager_interop.py "${1:-all}";;
  kill|rekey)
    mode=${1:?}; kills=${2:-1000}; shift 2 || true
    sub=cipher-kill; [ "$cmd" = rekey ] && sub=rekey-kill
    $DURA "$sub" --kills "$kills" --seed "${SEED:-1}" --work "$WORK/$cmd-$mode-$$" "$@" mode="$mode" ps="${PS:-1024}" cache="${CACHE:-50}" iter=1000;;
  *) echo "unknown $cmd"; exit 2;;
esac
