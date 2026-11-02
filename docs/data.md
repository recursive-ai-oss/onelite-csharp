# ADO.NET provider (`RecursiveAI.OneLite.Data`)

A standard ADO.NET provider on the engine layer: `DbConnection`, `DbCommand`, `DbDataReader`, `DbTransaction`, `DbBatch`, `DbDataSource`. It references only
the core package. Dapper and other `DbConnection` consumers work unchanged. Where ADO.NET SQLite providers document a behaviour, this one matches it, and
the test project checks each such behaviour against `Microsoft.Data.Sqlite` on the native library (the oracle).

## Real async

Every `...Async` method awaits the storage layer: `OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`, `NextResultAsync`, `ExecuteNonQueryAsync`,
`ExecuteScalarAsync`, `CommitAsync`, `RollbackAsync`, `CloseAsync`, `DisposeAsync`. When a page is not cached the call returns an incomplete task and
resumes when the backend completes the read; when everything is cached it completes synchronously without an async state machine. A token cancels a query that
is waiting for I/O (`OperationCanceledException`; the connection stays usable). Cancellation is observed at I/O waits only: a fully cached query does not wait.
`ReadAsync` over a cached database allocates nothing; reading numbers allocates nothing per row; `GetString` allocates the string; `GetUtf8Span` and `GetBlobSpan`
return spans into the page cache that stay valid until the next `Read`.

Over a local file on Linux or macOS, async does not make disk reads faster; it frees the thread while a slow backend (browser storage, HTTP range) answers.

## Connection strings

| Key | Meaning |
|---|---|
| `Data Source` (`Filename`) | A path, `:memory:`, or `file:name?mode=memory`. With `Mode=Memory;Cache=Shared`, connections that use the same name share one in-memory database. |
| `Mode` | `ReadWriteCreate` (default), `ReadWrite` (file must exist), `ReadOnly`, `Memory`. Writing on a read-only connection fails with SQLITE_READONLY (8); opening a missing file read-only or read-write fails with SQLITE_CANTOPEN (14). |
| `Cache` | `Default`, `Private`, `Shared` (see above). |
| `Password` | The passphrase of an encrypted (SQLCipher-4-compatible) database. |
| `Foreign Keys` | `True`/`False` sets the foreign-key enforcement of the connection; absent keeps SQLite's default (off). |
| `Default Timeout` | Seconds to wait for a lock before SQLITE_BUSY. Default 30. |
| `Vfs` | A storage backend registered with `OneLiteConnection.RegisterVfs(name, vfs)`; the data source is then a path in that backend. |
| `Pooling` | Accepted and ignored: connections are not pooled. |

## Commands and parameters

* The text may hold several statements. `ExecuteReader` runs them in order: statements that return no columns run immediately, and the reader stops at the first
  that returns columns; `NextResult` moves on. Closing a reader early still runs the statements it did not reach, as executing the command would.
* `RecordsAffected` is -1 when no `INSERT`, `UPDATE` or `DELETE` (or other statement that may write) ran, else the sum of `changes()` after each of them
  (so an `UPDATE` that matched nothing reports 0).
* Parameters: `@x`, `:x`, `$x` match a parameter named `x` with or without its prefix (case-sensitive). `?NNN` matches a parameter named `?NNN`, else the NNNth parameter of
  the collection; a plain `?` takes the parameter at its position. A statement parameter with no value throws `InvalidOperationException`
  ("Must add values for the following parameters: ..."). A `null` `Value` throws ("Value must be set."): use `DBNull.Value`. `double.NaN` cannot be stored.
* Types bind as `long`/`int`/`short`/`byte`/`bool`/enums → INTEGER; `double`/`float` → REAL; `string`/`char` → TEXT; `byte[]`, `ReadOnlyMemory<byte>` → BLOB;
  `Guid` → upper-case TEXT; `DateTime` → `yyyy-MM-dd HH:mm:ss.FFFFFFF`; `DateTimeOffset` with offset; `TimeSpan` → `c`; `decimal` → invariant TEXT without trailing zeros.
* `GetValue` returns `long`, `double`, `string`, `byte[]` or `DBNull`. Typed getters on NULL throw `InvalidOperationException`. `GetDataTypeName` and `GetFieldType`
  describe the current value's storage class (declared column types are not exposed).
* `Cancel()` does nothing: cancel an `...Async` call with its token.
* The text is never rewritten; there is no `CommandType.StoredProcedure`.

## Transactions

`BeginTransaction()` runs `BEGIN DEFERRED`; `BeginTransaction(deferred: false)` runs `BEGIN IMMEDIATE`. One transaction per connection (a second throws
"OneLiteConnection does not support nested transactions."). `CreateCommand` hands the command the connection's current transaction; a command created earlier
must have `Transaction` set, or execution throws (as `Microsoft.Data.Sqlite` does). Disposing an uncommitted transaction rolls it back.

## Batches

`CreateBatch()` returns a `DbBatch`. `ExecuteNonQuery` and `ExecuteScalar` run the commands atomically (in a transaction of their own when the connection has
none). `ExecuteReader` returns each command's result sets in turn and adds no transaction.

## Errors

Engine errors surface as `OneLiteDataException : DbException` with `ResultCode`, `ExtendedResultCode` (also named `SqliteErrorCode`/`SqliteExtendedErrorCode`),
`Detail` (what OneLite adds, for corruption the page and cause) and the message `SQLite Error 1: 'no such table: t'.` The inner exception is the engine's `OneLiteException`.

## Limits

A connection is not thread-safe. `GetSchemaTable` is minimal (name, ordinal, type of the current value, type name). `GetSchema` and `CommandType` other than `Text` are not supported.
