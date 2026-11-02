using System;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.SqlLogicTest;

/// <summary>
/// OneLite writing as well as reading: every statement of a script, setup included, runs on OneLite over its own database file, with the
/// pager's synchronous setting off as the reference engine's run uses. Label <c>onelite-rw</c>.
/// </summary>
public sealed class OneLiteWriteEngine : ISltEngine
{
    private int _counter;

    /// <inheritdoc/>
    public string DialectName => "sqlite";

    /// <inheritdoc/>
    public string Label => "onelite-rw";

    /// <inheritdoc/>
    public string Describe() => PinnedSqlite.Header() + "# OneLite read-write engine; every statement runs on OneLite\n";

    /// <inheritdoc/>
    public ISltConnection OpenFresh(string scratchDir)
    {
        string path = Path.Combine(scratchDir, "slt-olw-" + Environment.ProcessId + "-" + (++_counter) + ".db");
        foreach (string suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
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

        var db = Database.Open(path, new DatabaseOptions { Synchronous = SynchronousMode.Off });
        return new Conn(db, db.Connect(), path);
    }

    private sealed class Conn(Database db, Connection conn, string path) : ISltConnection
    {
        public SltError? Execute(string sql)
        {
            try
            {
                // Like sqlite3_exec, run every statement of the text and stop at the first error.
                string rest = sql;
                while (!string.IsNullOrWhiteSpace(rest))
                {
                    using Statement st = conn.Prepare(rest);
                    rest = st.Tail;
                    while (st.Step())
                    {
                        // Rows of a statement run for its effect are dropped.
                    }
                }

                return null;
            }
            catch (OneLiteException e)
            {
                return new SltError(e.ExtendedCode, e.Message);
            }
        }

        public SltQueryResult Query(string sql)
        {
            try
            {
                using Statement st = conn.Prepare(sql);
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
            conn.Dispose();
            db.Dispose();
            foreach (string suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // Already gone; the scratch directory is disposable.
                }
            }
        }
    }
}
