#!/usr/bin/env bash
# Builds and runs Microsoft.Data.Sqlite's own test suite on OneLite through the SQLitePCLRaw adapter.
# Needs the .NET 10 SDK on PATH and the tests fetched by tools/scripts/fetch_efcore_tests.sh.
#
#   mds_suite.sh [extra vstest arguments, e.g. --TestCaseFilter:"FullyQualifiedName~SqliteBlobTest"]
#
# EFCORE_TESTS selects the fetched tests (default ~/onelite/cache/efcore-tests); results go to $ONELITE_ARTIFACTS/mds-suite.trx.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export EFCORE_TESTS=${EFCORE_TESTS:-$HOME/onelite/cache/efcore-tests}
art=${ONELITE_ARTIFACTS:-$root/artifacts}
export ONELITE_ARTIFACTS=$art
# The suite reads one internal property of the released library (SqliteConnection.Transaction); its three uses are redirected to the shim.
src="$art/mds-tests"
rm -rf "$src"
mkdir -p "$src/test"
cp -r "$EFCORE_TESTS/test/Microsoft.Data.Sqlite.Tests" "$src/test/"
sed -i 's/Assert\.Null(connection\.Transaction)/Assert.Null(SqliteConnectionHelpers.TransactionOf(connection))/' "$src/test/Microsoft.Data.Sqlite.Tests/SqliteTransactionTest.cs"
# The suite is xUnit v2 (VSTest); the repository's own projects run on Microsoft.Testing.Platform, so build here and hand the assembly to vstest.
dotnet build tests/OneLite.MdsSuite -c Release -p:EfCoreTests="$src" -v q -clp:NoSummary
dll=$(find "$art/bin/OneLite.MdsSuite" -name Microsoft.Data.Sqlite.Tests.dll | head -1)
exec dotnet vstest "$dll" --logger:"trx;LogFileName=$art/mds-suite.trx" --ResultsDirectory:"$art/mds-results" "$@"
