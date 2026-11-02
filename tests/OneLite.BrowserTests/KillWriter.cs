using System;
using System.Globalization;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>
/// The writer a kill-the-tab run starts and then kills. It goes through the engine (<c>Database.OpenAsync</c>, <c>WriteAsync</c>) on one of the browser file
/// systems and appends rows to the standard test database in transactions of one to three rows. <see cref="Acked"/> is the highest row number whose
/// transaction has returned: the driver reads it from outside and checks, after the tab is gone, that every row up to the last value it saw is in the
/// recovered file and that no transaction is half there.
/// </summary>
internal static class KillWriter
{
    // Rows written here start above the rows DbContent writes (a column values up to a few thousand).
    internal const long Base = 1_000_000;

    private static Backend? s_backend;
    private static Database? s_db;
    private static long s_acked;
    private static Task? s_loop;

    internal static async Task<string> Handle(string cmd, string[] a)
    {
        switch (cmd)
        {
            // kill.run|backend|path|transactions : opens the database through the engine, starts the writer loop and returns at once.
            case "kill.run":
            {
                s_backend = await Backend.Create(a[0]).ConfigureAwait(false);
                s_db = await Database.OpenAsync(a[1], new DatabaseOptions { Vfs = s_backend.Vfs, JournalMode = JournalMode.Delete, Synchronous = SynchronousMode.Full }).ConfigureAwait(false);
                long start = await s_db.ScalarAsync<long>($"select coalesce(max(a), {Base}) from t where a >= {Base}").ConfigureAwait(false);
                s_acked = start;
                s_loop = Loop(start, int.Parse(a[2], CultureInfo.InvariantCulture));
                return "started " + start.ToString(CultureInfo.InvariantCulture);
            }

            case "kill.acked":
                if (s_loop is { IsFaulted: true } f)
                {
                    return "FAIL writer: " + f.Exception!.GetBaseException().Message;
                }

                return Volatile_Acked().ToString(CultureInfo.InvariantCulture);

            default:
                return "FAIL unknown command " + cmd;
        }
    }

    private static long Volatile_Acked() => System.Threading.Volatile.Read(ref s_acked);

    private static async Task Loop(long start, int transactions)
    {
        Database db = s_db!;
        long next = start + 1;
        for (int g = 0; g < transactions; g++)
        {
            int size = 1 + g % 3;
            long first = next;
            string group = first.ToString(CultureInfo.InvariantCulture) + ":" + size.ToString(CultureInfo.InvariantCulture);
            await db.WriteAsync(async tx =>
            {
                for (int j = 0; j < size; j++)
                {
                    long a = first + j;
                    int len = 200 + (int)(a * 37 % 900);
                    await tx.ExecuteAsync($"insert into t values ({a}, {group}, zeroblob({len}))").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            next += size;
            System.Threading.Volatile.Write(ref s_acked, next - 1);

            // Let the page answer the driver's poll between transactions.
            await Task.Yield();
        }
    }
}
