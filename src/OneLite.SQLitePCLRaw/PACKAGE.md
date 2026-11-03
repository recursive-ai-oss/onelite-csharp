# RecursiveAI.OneLite.SQLitePCLRaw

An `ISQLite3Provider` for SQLitePCLRaw that runs on OneLite, a pure-managed, SQLite-compatible database, so `Microsoft.Data.Sqlite` and Entity Framework Core's SQLite provider can use it without a native library.

```csharp
// instead of SQLitePCL.Batteries_V2.Init() / bundle_e_sqlite3
RecursiveAI.OneLite.SQLitePCLRaw.OneLiteBatteries.Init();
```

Reference `Microsoft.Data.Sqlite.Core` (not `Microsoft.Data.Sqlite`, whose default bundle brings the native library) and this package.

## Foreign keys are on by default here

The adapter turns `PRAGMA foreign_keys` on for every connection it opens. That matches `bundle_e_sqlite3`, the package this one replaces, whose native library is built with foreign keys on by default; `Microsoft.Data.Sqlite` and EF Core rely on it.

The OneLite core (`RecursiveAI.OneLite`) and `RecursiveAI.OneLite.Data` keep SQLite's own default, which is off.

To get SQLite's default (off) through the adapter, set `OneLiteBatteries.ForeignKeysByDefault = false` before opening connections. An explicit `PRAGMA foreign_keys` or the `Foreign Keys` connection-string key always wins.

See `docs/adapter.md` in the repository for the supported surface and known differences.

- Repository: https://github.com/recursive-ai-oss/onelite-csharp
- Licence: MIT OR Apache-2.0

OneLite is an independent project and is not affiliated with, endorsed by, or sponsored by the SQLite developers or Hipp, Wyrick & Company. SQLite is a registered trademark of Hipp, Wyrick & Company.
