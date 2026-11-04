# OneLite

A pure-managed, SQLite-compatible embedded SQL database for .NET, built on the base class library alone: no native binaries, so it works in trimmed, NativeAOT and browser builds.

It aims to read and write SQLite database, journal and WAL files both ways and to behave identically to SQLite by default, with opt-in improvements (row-level concurrent writers, SQLCipher-compatible encryption, real async over a pluggable VFS, an exact-kNN vector table) that keep files standard.

The storage layer, the SQL engine, ADO.NET (`RecursiveAI.OneLite.Data`), the happy-path API, FTS3/4/5, R*Tree, the `vec0` vector table, the backup API, SQLCipher-4-compatible encryption and opt-in `BEGIN CONCURRENT` are included. Measured compatibility and every known difference from SQLite 3.53.4: https://github.com/recursive-ai-oss/onelite-csharp/blob/main/docs/compatibility.md.

- Repository: https://github.com/recursive-ai-oss/onelite-csharp
- Project page: https://recursiveai.org/onelite/
- Changelog: https://github.com/recursive-ai-oss/onelite-csharp/blob/main/CHANGELOG.md
- Licence: MIT OR Apache-2.0

OneLite is an independent project and is not affiliated with, endorsed by, or sponsored by the SQLite developers or Hipp, Wyrick & Company. SQLite is a registered trademark of Hipp, Wyrick & Company.
