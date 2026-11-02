#!/bin/bash
# Runs the SQLCipher pragma cases (cipher_pragma_cases.txt) through SQLCipher and OneLite and compares them with pragma_diff.py.
# Environment: ONELITE_SQLCIPHER (the sqlcipher binary), ONELITE_SQLCIPHER_LIB (its library directory), ONELITE_SQLRUN, ONELITE_WORK.
set -e
here=$(cd "$(dirname "$0")" && pwd)
export LD_LIBRARY_PATH="${ONELITE_SQLCIPHER_LIB:-}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec python3 "$here/pragma_diff.py" "$here/cipher_pragma_cases.txt" --sqlite3 "${ONELITE_SQLCIPHER:-sqlcipher}" "$@"
