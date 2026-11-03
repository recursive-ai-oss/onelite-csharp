#!/usr/bin/env bash
# Fetches the public test projects of Microsoft.Data.Sqlite and of Entity Framework Core's SQLite provider, at a pinned commit, into a cache
# directory outside the repository. Only paths under test/ are extracted: the libraries' own source is never unpacked, read or built.
# The tests are MIT-licensed (dotnet/efcore LICENSE.txt); nothing fetched here is committed or redistributed.
#
#   fetch_efcore_tests.sh [destination]      default: ~/onelite/cache/efcore-tests
#
# EFCORE_SHA overrides the pinned commit (the v10.0.14 release, which matches the Microsoft.Data.Sqlite.Core version the tests run with).
set -euo pipefail

sha=${EFCORE_SHA:-4c96c90aa32d1d339911d82609f80200248c64f1}
dest=${1:-$HOME/onelite/cache/efcore-tests}
tarball=$dest/../efcore-$sha.tar.gz

if [ -f "$dest/.fetched-$sha" ]; then
  echo "already fetched: $dest ($sha)"
  exit 0
fi

mkdir -p "$dest"
if [ ! -s "$tarball" ]; then
  curl -fsSL --retry 3 -o "$tarball" "https://codeload.github.com/dotnet/efcore/tar.gz/$sha"
fi

# --wildcards with explicit test/ patterns: anything under src/ stays inside the archive.
tar -xzf "$tarball" -C "$dest" --strip-components=1 --wildcards \
  "efcore-$sha/test/Microsoft.Data.Sqlite.*" \
  "efcore-$sha/test/EFCore.Sqlite.FunctionalTests/*" \
  "efcore-$sha/test/EFCore.Sqlite.Tests/*" \
  "efcore-$sha/LICENSE.txt"
touch "$dest/.fetched-$sha"
find "$dest/test" -maxdepth 1 -mindepth 1 | sort
rm -f "$tarball"
