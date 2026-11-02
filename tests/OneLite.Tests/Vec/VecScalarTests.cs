using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vec;

/// <summary>The scalar functions against what a released sqlite-vec answered for the same statements (testdata/vec/scalar_golden.jsonl).</summary>
public sealed class VecScalarTests : IDisposable
{
    private readonly string _dir = VecHelper.TempDir("vec-scalar");

    public void Dispose() => Directory.Delete(_dir, true);

    internal static string Cell(Statement st, int i)
    {
        switch (st.ColumnType(i))
        {
            case ColumnType.Null:
                return "NULL";
            case ColumnType.Integral:
                return st.GetInt64(i).ToString(CultureInfo.InvariantCulture);
            case ColumnType.Real:
            {
                string r = st.GetDouble(i).ToString("R", CultureInfo.InvariantCulture);
                return r.Contains('.') || r.Contains('E') || r.Contains('N') ? r : r + ".0";
            }

            case ColumnType.Text:
                return "'" + st.GetString(i)!.Replace("'", "''", StringComparison.Ordinal) + "'";
            default:
                return "x'" + Convert.ToHexString(st.GetBlob(i)).ToLowerInvariant() + "'";
        }
    }

    internal static bool Same(string expected, string actual)
    {
        if (expected == actual)
        {
            return true;
        }

        bool eNum = double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out double e);
        bool aNum = double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double a);
        if (!eNum || !aNum || expected.StartsWith('\'') || expected.StartsWith('x'))
        {
            return false;
        }

        // Both real or both integer, and equal to a part in a million (the reference measures in float32).
        bool eReal = expected.Contains('.') || expected.Contains('e');
        bool aReal = actual.Contains('.') || actual.Contains('E') || actual.Contains('e');
        return eReal == aReal && Math.Abs(e - a) <= 1e-6 * Math.Max(Math.Abs(e), 1.0);
    }

    [Fact]
    public void ScalarFunctionsMatchTheReference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "vec", "scalar_golden.jsonl");
        string[] lines = File.ReadAllLines(path);
        using var db = Database.Open(Path.Combine(_dir, "s.db"));
        using Connection conn = db.Connect();
        var bad = new List<string>();
        int n = 0;
        foreach (string line in lines.Skip(1))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            string sql = root.GetProperty("sql").GetString()!;
            var got = new List<string[]>();
            string? error = null;
            try
            {
                using Statement st = conn.Prepare(sql);
                while (st.Step())
                {
                    got.Add([.. Enumerable.Range(0, st.ColumnCount).Select(i => Cell(st, i))]);
                }
            }
            catch (OneLiteException e)
            {
                error = e.Message;
            }

            n++;
            if (root.TryGetProperty("error", out JsonElement err))
            {
                if (error != err.GetString())
                {
                    bad.Add(sql + "\n   want error: " + err.GetString() + "\n   got: " + (error ?? string.Join(" | ", got.Select(r => string.Join(",", r)))));
                }

                continue;
            }

            string[][] want = [.. root.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()!).ToArray())];
            bool ok = error is null && want.Length == got.Count && want.Zip(got).All(p => p.First.Length == p.Second.Length && p.First.Zip(p.Second).All(c => Same(c.First, c.Second)));
            if (!ok)
            {
                bad.Add(sql + "\n   want: " + string.Join(" | ", want.Select(r => string.Join(",", r))) + "\n   got: " + (error is null ? string.Join(" | ", got.Select(r => string.Join(",", r))) : "error " + error));
            }
        }

        File.WriteAllText(Path.Combine(_dir, "diffs.txt"), string.Join("\n", bad));
        Assert.True(bad.Count == 0, bad.Count + " of " + n + " differ:\n" + string.Join("\n", bad.Take(25)));
    }
}
