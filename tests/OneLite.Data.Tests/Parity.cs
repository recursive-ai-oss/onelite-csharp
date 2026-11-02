using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>
/// Runs one scenario against both ADO.NET providers on the same engine-neutral <see cref="DbConnection"/> surface and compares what it prints.
/// <c>Microsoft.Data.Sqlite</c> on the native library is the oracle for the behaviours the provider documents; nothing of its source is used.
/// </summary>
internal static class Parity
{
    private static readonly object s_init = new();
    private static bool s_done;

    internal static void InitNative()
    {
        lock (s_init)
        {
            if (!s_done)
            {
                SQLitePCL.Batteries_V2.Init();
                s_done = true;
            }
        }
    }

    internal sealed class Dirs : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "onelite-data-" + Guid.NewGuid().ToString("N"));

        internal Dirs() => Directory.CreateDirectory(Root);

        internal string Path1(string name) => System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }

    /// <summary>The scenario gets a factory that makes a connection string for a database file name (private per provider).</summary>
    internal static string Run(bool oneLite, Func<string, string> cs, Func<Func<string, string>, string> scenario)
    {
        InitNative();
        _ = oneLite;
        try
        {
            return scenario(cs);
        }
        catch (Exception e)
        {
            return "THROW " + Describe(e);
        }
    }

    internal static string Describe(Exception e) => e switch
    {
        SqliteException s => $"{s.GetType().Name} {s.SqliteErrorCode}/{s.SqliteExtendedErrorCode}: {s.Message}",
        OneLiteDataException o => $"{nameof(SqliteException)} {o.ResultCode}/{o.ExtendedResultCode}: {o.Message}",
        _ => e.GetType().Name + ": " + e.Message.Replace("SqliteConnection", "OneLiteConnection", StringComparison.Ordinal),
    };

    internal static DbConnection Open(bool oneLite, string connectionString)
    {
        DbConnection c = oneLite ? new OneLiteConnection(connectionString + ";Default Timeout=1") : new SqliteConnection(connectionString + ";Pooling=False;Default Timeout=1");
        c.Open();
        return c;
    }

    /// <summary>Runs <paramref name="scenario"/> on each provider (fresh temp directory each) and asserts equal output.</summary>
    internal static void Same(Func<Func<bool, string, DbConnection>, string> scenario, string? expectContains = null)
    {
        string a = Exec(false, scenario);
        string b = Exec(true, scenario);
        Assert.True(a == b, "\n--- Microsoft.Data.Sqlite:\n" + a + "\n--- OneLite:\n" + b);
        if (expectContains != null)
        {
            Assert.Contains(expectContains, a, StringComparison.Ordinal);
        }
    }

    private static string Exec(bool oneLite, Func<Func<bool, string, DbConnection>, string> scenario)
    {
        InitNative();
        using var dirs = new Dirs();
        try
        {
            return scenario((_, file) => Open(oneLite, "Data Source=" + (file == ":memory:" ? file : dirs.Path1(file))));
        }
        catch (Exception e)
        {
            return "THROW " + Describe(e);
        }
    }

    internal static string Cell(object? v) => v switch
    {
        null => "<null>",
        DBNull => "<DBNull>",
        byte[] b => "x'" + Convert.ToHexString(b) + "'",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + ":double",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) + ":" + v.GetType().Name,
        _ => v + ":" + v.GetType().Name,
    };

    internal static string Rows(DbDataReader r)
    {
        var sb = new StringBuilder();
        do
        {
            sb.Append('[');
            for (int i = 0; i < r.FieldCount; i++)
            {
                sb.Append(i > 0 ? "," : "").Append(r.GetName(i));
            }

            sb.Append("]\n");
            while (r.Read())
            {
                for (int i = 0; i < r.FieldCount; i++)
                {
                    sb.Append(i > 0 ? " | " : "").Append(Cell(r.GetValue(i)));
                }

                sb.Append('\n');
            }
        }
        while (r.NextResult());
        sb.Append("affected=").Append(r.RecordsAffected);
        return sb.ToString();
    }
}
