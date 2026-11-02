using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.QueryDiff;
using RecursiveAI.OneLite.Storage;

namespace RecursiveAI.OneLite.Differential;

/// <summary>Builds a case's database by running its setup statements on OneLite, and compares the result with the file the reference built.</summary>
internal static class OneLiteSetup
{
#pragma warning disable CA1031 // any failure inside the engine under test is a finding
    /// <summary>
    /// Runs the statements on a new file. Returns null when the file was built and its <c>.dump</c> equals the reference file's, a description of the
    /// first difference otherwise. <paramref name="unsupported"/> is true when a statement uses something the engine does not run yet (the case then
    /// falls back to the reference-built file).
    /// </summary>
    internal static string? Build(string path, string referencePath, IReadOnlyList<string> statements, bool utf16, out bool unsupported)
    {
        if (Qd.Cold)
        {
            // Build through the backend with only asynchronous calls. A script that uses something that cannot suspend (VACUUM, a journal mode change, ...)
            // is built again on the sync driver from an empty file, and the tool counts it.
            string? r = BuildCore(path, referencePath, statements, utf16, true, out unsupported);
            if (r != AsyncBlocked)
            {
                return r;
            }

            Qd.CountFallback();
            Executor.DeleteFiles(path);
        }

        return BuildCore(path, referencePath, statements, utf16, false, out unsupported);
    }

    private const string AsyncBlocked = "\0async-blocked";

    private static string? BuildCore(string path, string referencePath, IReadOnlyList<string> statements, bool utf16, bool cold, out bool unsupported)
    {
        unsupported = false;
        try
        {
            var options = new DatabaseOptions
            {
                Encoding = !utf16 ? TextEncoding.Utf8 : statements.Any(x => x.Contains("16be", StringComparison.OrdinalIgnoreCase)) ? TextEncoding.Utf16Be : TextEncoding.Utf16Le,
                Synchronous = RecursiveAI.OneLite.Storage.Pager.SynchronousMode.Off,
                Vfs = cold ? Qd.ColdVfs : null,
                CacheSize = cold ? 6 : -2000,
                YieldEvery = cold ? Qd.Options.YieldEvery : 0,
            };
            using (Database db = Database.Open(path, options))
            {
                Connection conn = cold ? db.ConnectAsync().AsTask().GetAwaiter().GetResult() : db.Connect();
                try
                {
                foreach (string sql in statements)
                {
                    if (sql.Contains("encoding", StringComparison.OrdinalIgnoreCase) && sql.TrimStart().StartsWith("pragma", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // The shell, like this loop, goes on after a statement that fails; the dumps then show whether both ended in the same state.
                    try
                    {
                        string rest = sql;
                        while (!string.IsNullOrWhiteSpace(rest))
                        {
                            Statement st = Qd.Prepare(conn, rest, cold);
                            try
                            {
                                rest = st.Tail;
                                while (cold ? st.StepAsync().AsTask().GetAwaiter().GetResult() : st.Step())
                                {
                                }
                            }
                            finally
                            {
                                Qd.Close(st, cold);
                            }
                        }
                    }
                    catch (OneLiteException e) when (cold && Qd.NeedsBlocking(e))
                    {
                        return AsyncBlocked;
                    }
                    catch (OneLiteException e) when (!e.Message.StartsWith("unsupported", StringComparison.Ordinal))
                    {
                    }
                }
                }
                finally
                {
                    if (cold)
                    {
                        conn.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    else
                    {
                        conn.Dispose();
                    }
                }
            }
        }
        catch (OneLiteException e) when (e.Message.StartsWith("unsupported", StringComparison.Ordinal))
        {
            unsupported = true;
            return null;
        }
        catch (Exception e)
        {
            return "setup failed on OneLite: " + e.GetType().Name + ": " + e.Message;
        }

        // The generator builds referencePath through the native library, whose compile options differ from the shell's (LIKE never matches a BLOB,
        // foreign keys default on). The engine is held to the shell, so the setup is replayed there for the comparison.
        string shellPath = path + "-sh.db";
        string a;
        try
        {
            Executor.DeleteFiles(shellPath);
            SqliteCli.Run(shellPath, string.Join('\n', statements.Select(x => x.TrimEnd().EndsWith(';') ? x : x + ";")) + "\n");
            a = SortStatRows(SqliteCli.Dump(shellPath));
        }
        finally
        {
            Executor.DeleteFiles(shellPath);
        }

        string b = SortStatRows(SqliteCli.Dump(path));
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return null;
        }

        string[] la = a.Split('\n'), lb = b.Split('\n');
        for (int i = 0; i < Math.Min(la.Length, lb.Length); i++)
        {
            if (!string.Equals(la[i], lb[i], StringComparison.Ordinal))
            {
                return "dump differs at line " + i + ": reference [" + Trim(la[i]) + "] OneLite [" + Trim(lb[i]) + "]";
            }
        }

        return "dump differs in length: reference " + la.Length + " lines, OneLite " + lb.Length;
    }
#pragma warning restore CA1031

    // ANALYZE visits tables in the order of the reference's internal hash table, which is not documented; the rows of sqlite_stat1 are compared as a set.
    private static string SortStatRows(string dump)
    {
        string[] lines = dump.Split('\n');
        var rows = lines.Where(l => l.StartsWith("INSERT INTO sqlite_stat1", StringComparison.Ordinal)).OrderBy(l => l, StringComparer.Ordinal).ToList();
        int k = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("INSERT INTO sqlite_stat1", StringComparison.Ordinal))
            {
                lines[i] = rows[k++];
            }
        }

        return string.Join('\n', lines);
    }

    private static string Trim(string s) => s.Length > 160 ? s[..160] + "..." : s;
}
