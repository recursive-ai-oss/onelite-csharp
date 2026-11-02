using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>
/// Reading a database over HTTP range requests from the page: <c>http.open|name|url|blockSize</c> opens it (read-only, through <see cref="HttpRangeVfs"/> and the
/// async engine API), <c>http.q|name|sql</c> runs one statement and returns its rows and the number of requests so far, <c>http.close|name</c> closes it.
/// </summary>
internal static class HttpReader
{
    private static readonly Dictionary<string, (Database Db, Connection Conn, HttpRangeVfs Vfs)> s_open = [];

    internal static async Task<string> Handle(string cmd, string[] a)
    {
        switch (cmd)
        {
            case "http.open":
            {
                var vfs = new HttpRangeVfs(new HttpClient(), new HttpRangeOptions { BlockSize = int.Parse(a[2], CultureInfo.InvariantCulture), CachedBlocks = 2 });
                Database db = await Database.OpenReadOnlyAsync(a[1], new DatabaseOptions { Vfs = vfs, CacheSize = 8 }).ConfigureAwait(false);
                Connection c = await db.ConnectAsync().ConfigureAwait(false);
                s_open[a[0]] = (db, c, vfs);
                return "ok";
            }

            case "http.q":
            {
                (Database _, Connection c, HttpRangeVfs vfs) = s_open[a[0]];
                var sb = new StringBuilder();
                await using (Statement st = await c.PrepareAsync(a[1]).ConfigureAwait(false))
                {
                    while (await st.StepAsync().ConfigureAwait(false))
                    {
                        for (int i = 0; i < st.ColumnCount; i++)
                        {
                            sb.Append(i == 0 ? "" : "|").Append(st.GetFieldValue<object?>(i));
                        }

                        sb.Append('\n');
                    }
                }

                return sb.ToString().TrimEnd('\n') + " ## requests=" + vfs.RequestCount.ToString(CultureInfo.InvariantCulture);
            }

            case "http.close":
            {
                (Database db, Connection c, HttpRangeVfs _) = s_open[a[0]];
                s_open.Remove(a[0]);
                await c.DisposeAsync().ConfigureAwait(false);
                db.Dispose();
                return "ok";
            }

            default:
                return "FAIL unknown command " + cmd;
        }
    }
}
