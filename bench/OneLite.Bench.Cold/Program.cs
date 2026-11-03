using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Bench;

// Usage: OneLite.Bench.Cold <onelite|native> <database> [reference library]
// Opens the database, answers one point query and prints the time since the process started (runtime start included).
string side = args[0];
string path = args[1];
long check = 0;
if (side == "onelite")
{
    using Database db = Database.Open(path);
    using Connection c = db.Connect();
    using Statement s = c.Prepare("select b from t where id = @p1");
    s.Bind(1, 4242);
    while (s.Step())
    {
        check += s.GetTextUtf8(0).Length;
    }
}
else
{
    Native.Configure(args[2]);
    _ = Native.sqlite3_open_v2(path, out nint db, Native.OpenReadWrite, 0);
    _ = Native.sqlite3_prepare_v2(db, "select b from t where id = @p1", -1, out nint st, 0);
    _ = Native.sqlite3_bind_int64(st, 1, 4242);
    while (Native.sqlite3_step(st) == Native.Row)
    {
        _ = Native.sqlite3_column_text(st, 0);
        check += Native.sqlite3_column_bytes(st, 0);
    }

    _ = Native.sqlite3_finalize(st);
    _ = Native.sqlite3_close_v2(db);
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"COLD {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:F1} {check}"));
