#!/usr/bin/env bash
# Builds and runs Entity Framework Core's SQLite functional tests on OneLite through the SQLitePCLRaw adapter.
# Needs the .NET 10 SDK on PATH and the tests fetched by tools/scripts/fetch_efcore_tests.sh.
#
#   ef_suite.sh [extra vstest arguments, e.g. --TestCaseFilter:"FullyQualifiedName~BuiltInDataTypesSqliteTest"]
#
# EFCORE_TESTS selects the fetched tests (default ~/onelite/cache/efcore-tests); results go to $ONELITE_ARTIFACTS/ef-suite.trx.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export EFCORE_TESTS=${EFCORE_TESTS:-$HOME/onelite/cache/efcore-tests}
art=${ONELITE_ARTIFACTS:-$root/artifacts}
export ONELITE_ARTIFACTS=$art
dotnet build tests/OneLite.EfSuite -c Release -p:EfCoreTests="$EFCORE_TESTS" -v q -clp:NoSummary
dll=$(find "$art/bin/OneLite.EfSuite" -name Microsoft.EntityFrameworkCore.Sqlite.FunctionalTests.dll | head -1)
exec dotnet vstest "$dll" --logger:"trx;LogFileName=$art/ef-suite.trx" --ResultsDirectory:"$art/ef-results" "$@"
