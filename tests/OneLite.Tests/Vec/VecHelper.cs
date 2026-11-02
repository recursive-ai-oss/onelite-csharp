using System;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.Tests.Vec;

/// <summary>Runs SQL on a connection and renders the rows like the shell's quote mode, so expected text is short.</summary>
internal static class VecHelper
{
    internal static string Run(Connection conn, string sql)
    {
        var o = new StringBuilder();
        try
        {
            using Statement st = conn.Prepare(sql);
            while (st.Step())
            {
                for (int i = 0; i < st.ColumnCount; i++)
                {
                    o.Append(i > 0 ? "|" : string.Empty).Append(Render(st, i));
                }

                o.Append('\n');
            }
        }
        catch (OneLiteException e)
        {
            o.Append("Error: ").Append(e.Message).Append((char)10);
        }

        return o.ToString();
    }

    private static string Render(Statement st, int i)
    {
        if (st.IsNull(i))
        {
            return "NULL";
        }

        return st.GetString(i) ?? string.Empty;
    }

    internal static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "onelite-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
