using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.SqlLogicTest;

/// <summary>An error an engine reports for a statement.</summary>
/// <param name="Code">The extended result code, or 0 when the engine has none.</param>
/// <param name="Message">The message text.</param>
public sealed record SltError(int Code, string Message);

/// <summary>The outcome of a query: an error, or the column count and the rows.</summary>
public sealed class SltQueryResult
{
    /// <summary>The error, or null when the query ran.</summary>
    public SltError? Error { get; init; }

    /// <summary>The number of result columns.</summary>
    public int ColumnCount { get; init; }

    /// <summary>The rows, each <see cref="ColumnCount"/> values.</summary>
    public List<SltValue[]> Rows { get; } = [];
}

/// <summary>One connection to a fresh, empty database. Not thread-safe.</summary>
public interface ISltConnection : IDisposable
{
    /// <summary>Runs one statement, discarding rows. Returns null on success.</summary>
    SltError? Execute(string sql);

    /// <summary>Runs one query and returns its rows with their storage classes.</summary>
    SltQueryResult Query(string sql);
}

/// <summary>Opens connections to one engine. The runner's only view of an engine.</summary>
public interface ISltEngine
{
    /// <summary>The name <c>skipif</c>/<c>onlyif</c> lines are matched against. Every engine reports <c>sqlite</c>: the target is SQLite's behaviour.</summary>
    string DialectName { get; }

    /// <summary>A short label for reports (<c>sqlite-native</c>, <c>onelite</c>).</summary>
    string Label { get; }

    /// <summary>Header lines for a report: versions, compile options and connection settings.</summary>
    string Describe();

    /// <summary>Opens a new empty database. <paramref name="scratchDir"/> is a writable directory for engines that need a file.</summary>
    ISltConnection OpenFresh(string scratchDir);
}

/// <summary>
/// The pinned native SQLite library through the oracle tool's wrapper. The library is a different build from the pinned
/// <c>sqlite3</c> shell (which is the behavioural reference), so each connection is set up to behave like the shell where a
/// setting can change a script's result. See docs/sqllogictest.md for the list.
/// </summary>
public sealed class SqliteNativeEngine : ISltEngine
{
    private int _counter;

    /// <inheritdoc/>
    public string DialectName => "sqlite";

    /// <inheritdoc/>
    public string Label => "sqlite-native";

    /// <summary>The settings applied to every connection, in order. They are statements so the header can print them.</summary>
    public static readonly string[] ConnectionSettings =
    [
        // The native library is built with foreign keys on by default; the shell leaves them off (pragma foreign_keys).
        "pragma foreign_keys = off",
        // Speed only: a script's database is thrown away, so durability buys nothing. Results do not depend on it.
        "pragma synchronous = off",
        "pragma cache_size = -131072",
    ];

    /// <inheritdoc/>
    public string Describe() => PinnedSqlite.Header()
        + "# connection settings: " + string.Join("; ", ConnectionSettings) + "\n";

    /// <inheritdoc/>
    public ISltConnection OpenFresh(string scratchDir)
    {
        string path = Path.Combine(scratchDir, "slt-" + Environment.ProcessId + "-" + (++_counter) + ".db");
        DeleteFiles(path);
        SqliteDb db = SqliteDb.Open(path, busyTimeoutMs: 0);
        try
        {
            foreach (string s in ConnectionSettings)
            {
                db.Exec(s);
            }
            NativeConfig.MatchShell(db);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        return new Conn(db, path);
    }

    private static void DeleteFiles(string path)
    {
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
                // The file is gone or in use; a leftover is overwritten by the next open of the same name.
            }
        }
    }

    private sealed class Conn(SqliteDb db, string path) : ISltConnection
    {
        public SltError? Execute(string sql)
        {
            try
            {
                db.Exec(sql);
                return null;
            }
            catch (SqliteException e)
            {
                return new SltError(e.ExtendedCode, e.Message);
            }
        }

        public SltQueryResult Query(string sql)
        {
            try
            {
                using SqliteStatement st = db.Prepare(sql);
                int n = st.ColumnCount;
                var result = new SltQueryResult { ColumnCount = n };
                while (st.Step())
                {
                    var row = new SltValue[n];
                    for (int i = 0; i < n; i++)
                    {
                        switch (st.ColumnType(i))
                        {
                            case 1: row[i] = SltValue.FromInt(st.GetInt64(i)); break;
                            case 2: row[i] = SltValue.FromReal(st.GetDouble(i)); break;
                            case 3: row[i] = SltValue.FromText(Encoding.UTF8.GetString(st.Bytes(i))); break;
                            case 4: row[i] = SltValue.FromBlob(Encoding.UTF8.GetString(st.Bytes(i))); break;
                            default: row[i] = SltValue.Null; break;
                        }
                    }
                    result.Rows.Add(row);
                }
                return result;
            }
            catch (SqliteException e)
            {
                return new SltQueryResult { Error = new SltError(e.ExtendedCode, e.Message) };
            }
        }

        public void Dispose()
        {
            db.Dispose();
            DeleteFiles(path);
        }
    }
}

/// <summary>
/// The OneLite engine. It is read-only, so a script's setup statements (CREATE, INSERT, ...) are applied to the database file
/// by the pinned native library, and every query runs on OneLite over that file. A query that fails to prepare or run reports
/// OneLite's own error. Scripts that need OneLite to write are not meaningful here until the engine can.
/// </summary>
public sealed class OneLiteEngine : ISltEngine
{
    private int _counter;

    /// <inheritdoc/>
    public string DialectName => "sqlite";

    /// <inheritdoc/>
    public string Label => "onelite";

    /// <inheritdoc/>
    public string Describe() => PinnedSqlite.Header() + "# OneLite read-only engine; setup statements are written by the native library\n";

    /// <inheritdoc/>
    public ISltConnection OpenFresh(string scratchDir)
    {
        string path = Path.Combine(scratchDir, "slt-ol-" + Environment.ProcessId + "-" + (++_counter) + ".db");
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
                // A leftover is overwritten by the next open of the same name.
            }
        }

        SqliteDb db = SqliteDb.Open(path, busyTimeoutMs: 0);
        db.Exec("pragma foreign_keys = off");
        db.Exec("pragma synchronous = off");
        db.Exec("pragma user_version = 1");
        return new Conn(db, path);
    }

    private sealed class Conn(SqliteDb setup, string path) : ISltConnection
    {
        private Database? _db;
        private Connection? _conn;

        public SltError? Execute(string sql)
        {
            Close();
            try
            {
                setup.Exec(sql);
                return null;
            }
            catch (SqliteException e)
            {
                return new SltError(e.ExtendedCode, e.Message);
            }
        }

        private void Close()
        {
            _conn?.Dispose();
            _db?.Dispose();
            _conn = null;
            _db = null;
        }

        public SltQueryResult Query(string sql)
        {
            try
            {
                if (_conn == null)
                {
                    _db = Database.OpenReadOnly(path);
                    _conn = _db.Connect();
                }

                using Statement st = _conn.Prepare(sql);
                int n = st.ColumnCount;
                var result = new SltQueryResult { ColumnCount = n };
                while (st.Step())
                {
                    var row = new SltValue[n];
                    for (int i = 0; i < n; i++)
                    {
                        switch (st.ColumnType(i))
                        {
                            case ColumnType.Integral: row[i] = SltValue.FromInt(st.GetInt64(i)); break;
                            case ColumnType.Real: row[i] = SltValue.FromReal(st.GetDouble(i)); break;
                            case ColumnType.Text: row[i] = SltValue.FromText(Encoding.UTF8.GetString(st.GetTextUtf8(i))); break;
                            case ColumnType.Blob: row[i] = SltValue.FromBlob(Encoding.UTF8.GetString(st.GetBlob(i))); break;
                            default: row[i] = SltValue.Null; break;
                        }
                    }

                    result.Rows.Add(row);
                }

                return result;
            }
            catch (OneLiteException e)
            {
                return new SltQueryResult { Error = new SltError(e.ExtendedCode, e.Message) };
            }
        }

        public void Dispose()
        {
            Close();
            setup.Dispose();
            foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // Already gone or still open elsewhere; the scratch directory is disposable.
                }
            }
        }
    }
}

/// <summary>Connection settings that are not statements.</summary>
internal static class NativeConfig
{
    /// <summary>Applies the settings that make the native library behave like the shell. None are needed yet beyond the pragmas.</summary>
    internal static void MatchShell(SqliteDb db)
    {
        _ = db;
    }
}
