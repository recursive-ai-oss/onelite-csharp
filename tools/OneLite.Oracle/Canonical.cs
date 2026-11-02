using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace RecursiveAI.OneLite.Oracle;

/// <summary>
/// The canonical text rendering of a value together with its storage class. Later comparison tools render both sides with
/// the same code, so two results are equal exactly when every column has the same storage class and the same value.
/// <code>
/// N                 NULL
/// I:-42             INTEGER, decimal
/// R:1.5             FLOAT, shortest text that round-trips (-0 keeps its sign; Infinity / -Infinity are spelled out)
/// T:"a\"b\n"        TEXT as UTF-8: backslash, double quote and control characters (&lt; 0x20, 0x7f) escaped; \n \r \t by name, others \xNN
/// T!6162ff          TEXT that is not valid UTF-8, as hex of its bytes
/// B:00ff            BLOB, hex
/// </code>
/// A row is its values joined by a tab; escaping guarantees no tab or newline appears inside a value.
/// </summary>
public static class Canonical
{
    /// <summary>Renders column <paramref name="i"/> of the current row.</summary>
    public static string Value(SqliteStatement st, int i)
    {
        switch (st.ColumnType(i))
        {
            case Native.TypeNull: return "N";
            case Native.TypeInteger: return "I:" + st.GetInt64(i).ToString(CultureInfo.InvariantCulture);
            case Native.TypeFloat: return "R:" + Real(st.GetDouble(i));
            case Native.TypeText: return Text(st.Bytes(i));
            default: return "B:" + Convert.ToHexStringLower(st.Bytes(i));
        }
    }

    /// <summary>Renders a REAL.</summary>
    public static string Real(double d)
        => d == 0 && double.IsNegative(d) ? "-0" : d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Renders TEXT given its UTF-8 bytes.</summary>
    public static string Text(ReadOnlySpan<byte> utf8)
    {
        if (!Utf8.IsValid(utf8))
        {
            return "T!" + Convert.ToHexStringLower(utf8);
        }
        string s = Encoding.UTF8.GetString(utf8);
        var sb = new StringBuilder(s.Length + 4);
        sb.Append("T:\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7f)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Joins the values of one row.</summary>
    public static string Row(IEnumerable<string> values) => string.Join('\t', values);
}
