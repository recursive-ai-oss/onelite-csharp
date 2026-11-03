using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

/// <summary>The output modes of <c>.mode</c>.</summary>
internal enum OutputMode
{
    List, Csv, Tabs, Ascii, Line, Json, Column, Box, Table, Markdown, Html, Quote, Qbox, Insert, Tcl,
}

/// <summary>How control characters are shown (<c>-escape</c>).</summary>
internal enum EscapeMode
{
    Ascii, Symbol, Off,
}

/// <summary>One result value: its bytes (null for NULL) and its storage class.</summary>
internal readonly record struct Cell(byte[]? Data, ColumnType Type);

/// <summary>A buffered stream of bytes the shell writes its output to.</summary>
internal sealed class ByteOut : IDisposable
{
    private readonly Stream _s;
    private readonly bool _owns;
    private readonly byte[] _buf = new byte[32768];
    private int _n;

    public ByteOut(Stream s, bool owns)
    {
        _s = s;
        _owns = owns;
    }

    public void Write(ReadOnlySpan<byte> b)
    {
        if (b.Length > _buf.Length - _n)
        {
            Flush();
            if (b.Length > _buf.Length)
            {
                _s.Write(b);
                return;
            }
        }

        b.CopyTo(_buf.AsSpan(_n));
        _n += b.Length;
    }

    public void Write(byte b)
    {
        if (_n == _buf.Length)
        {
            Flush();
        }

        _buf[_n++] = b;
    }

    public void Write(string s) => Write(Encoding.UTF8.GetBytes(s));

    public void Flush()
    {
        if (_n > 0)
        {
            _s.Write(_buf, 0, _n);
            _n = 0;
        }

        _s.Flush();
    }

    public void Dispose()
    {
        Flush();
        if (_owns)
        {
            _s.Dispose();
        }
    }
}

/// <summary>Formats result rows for the output modes, following the documented shell's output.</summary>
internal sealed class Renderer
{
    private readonly ByteOut _out;
    private readonly Settings _st;
    private string[] _names = [];
    private int _rowCount;
    private readonly List<Cell[]> _rows = [];

    public Renderer(ByteOut output, Settings settings)
    {
        _out = output;
        _st = settings;
    }

    public int RowCount => _rowCount;

    public void Begin(string[] names)
    {
        _names = names;
        _rowCount = 0;
        _rows.Clear();
    }

    public void Row(Cell[] cells)
    {
        _rowCount++;
        switch (_st.Mode)
        {
            case OutputMode.Column:
            case OutputMode.Box:
            case OutputMode.Table:
            case OutputMode.Markdown:
            case OutputMode.Qbox:
                _rows.Add(cells);
                break;
            default:
                Streaming(cells);
                break;
        }
    }

    public void End()
    {
        switch (_st.Mode)
        {
            case OutputMode.Json when _rowCount > 0:
                _out.Write("]\n");
                break;
            case OutputMode.Column:
            case OutputMode.Box:
            case OutputMode.Table:
            case OutputMode.Markdown:
            case OutputMode.Qbox:
                Buffered();
                break;
            default:
                break;
        }

        _rows.Clear();
    }

    // ------------------------------------------------------------------ streaming modes

    private static ReadOnlySpan<byte> UpToNul(byte[] d)
    {
        int z = Array.IndexOf(d, (byte)0);
        return z < 0 ? d : d.AsSpan(0, z);
    }

    private ReadOnlySpan<byte> TextOf(Cell c) => c.Data is null ? SqlText.Utf8(_st.NullValue) : Escape(UpToNul(c.Data));

    /// <summary>Control characters other than tab and newline are shown as <c>^A</c> (the default), as the Unicode control pictures, or left alone (<c>-escape</c>).</summary>
    private byte[] Escape(ReadOnlySpan<byte> v)
    {
        if (_st.Escape == EscapeMode.Off)
        {
            return v.ToArray();
        }

        bool any = false;
        foreach (byte b in v)
        {
            if (b < 0x20 && b != 9 && b != 10)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return v.ToArray();
        }

        var o = new List<byte>(v.Length + 4);
        for (int i = 0; i < v.Length; i++)
        {
            byte b = v[i];
            if (b < 0x20 && b != 9 && b != 10 && !(b == 13 && i + 1 < v.Length && v[i + 1] == 10))
            {
                if (_st.Escape == EscapeMode.Symbol)
                {
                    o.AddRange(Encoding.UTF8.GetBytes(((char)(0x2400 + b)).ToString()));
                }
                else
                {
                    o.Add((byte)'^');
                    o.Add((byte)(b + 0x40));
                }
            }
            else
            {
                o.Add(b);
            }
        }

        return [.. o];
    }

    private void Streaming(Cell[] cells)
    {
        switch (_st.Mode)
        {
            case OutputMode.List:
            case OutputMode.Tabs:
            case OutputMode.Ascii:
            case OutputMode.Csv:
                ListLike(cells);
                break;
            case OutputMode.Line:
                LineRow(cells);
                break;
            case OutputMode.Json:
                JsonRow(cells);
                break;
            case OutputMode.Html:
                HtmlRow(cells);
                break;
            case OutputMode.Quote:
                if (_rowCount == 1 && _st.Headers)
                {
                    for (int i = 0; i < _names.Length; i++)
                    {
                        _out.Write(i > 0 ? "," : "");
                        _out.Write(QuoteText(SqlText.Utf8(_names[i])));
                    }

                    _out.Write("\n");
                }

                for (int i = 0; i < cells.Length; i++)
                {
                    _out.Write(i > 0 ? "," : "");
                    _out.Write(QuoteCell(cells[i], _st.NullValue));
                }

                _out.Write("\n");
                break;
            case OutputMode.Insert:
                _out.Write("INSERT INTO ");
                _out.Write(SqlText.QuoteIdent(_st.InsertTable));
                if (_st.Headers)
                {
                    _out.Write("(");
                    for (int i = 0; i < _names.Length; i++)
                    {
                        _out.Write(i > 0 ? "," : "");
                        _out.Write(SqlText.QuoteIdent(_names[i]));
                    }

                    _out.Write(")");
                }

                _out.Write(" VALUES(");
                for (int i = 0; i < cells.Length; i++)
                {
                    _out.Write(i > 0 ? "," : "");
                    _out.Write(QuoteCell(cells[i], _st.NullValue));
                }

                _out.Write(");\n");
                break;
            case OutputMode.Tcl:
                if (_rowCount == 1 && _st.Headers)
                {
                    for (int i = 0; i < _names.Length; i++)
                    {
                        _out.Write(i > 0 ? _st.ColSep : "");
                        _out.Write(TclQuote(SqlText.Utf8(_names[i])));
                    }

                    _out.Write(_st.RowSep);
                }

                for (int i = 0; i < cells.Length; i++)
                {
                    _out.Write(i > 0 ? _st.ColSep : "");
                    _out.Write(TclQuote(TextOf(cells[i])));
                }

                _out.Write(_st.RowSep);
                break;
            default:
                break;
        }
    }

    private void ListLike(Cell[] cells)
    {
        bool csvLike = _st.Mode is OutputMode.Csv or OutputMode.Tabs;
        if (_rowCount == 1 && _st.Headers)
        {
            for (int i = 0; i < _names.Length; i++)
            {
                _out.Write(i > 0 ? _st.ColSep : "");
                byte[] n = SqlText.Utf8(_names[i]);
                if (csvLike)
                {
                    CsvField(n, false);
                }
                else
                {
                    _out.Write(n);
                }
            }

            _out.Write(_st.RowSep);
        }

        for (int i = 0; i < cells.Length; i++)
        {
            _out.Write(i > 0 ? _st.ColSep : "");
            Cell c = cells[i];
            if (csvLike)
            {
                if (c.Data is null)
                {
                    _out.Write(_st.NullValue);
                }
                else
                {
                    CsvField(UpToNul(c.Data), c.Type == ColumnType.Text || c.Type == ColumnType.Blob);
                }
            }
            else
            {
                _out.Write(TextOf(c));
            }
        }

        _out.Write(_st.RowSep);
    }

    private void CsvField(ReadOnlySpan<byte> v, bool isText)
    {
        // Quote an empty text value, and any value with a space, control character, quote, separator or non-ASCII byte in it.
        bool quote = isText && v.Length == 0;
        byte sep = _st.ColSep.Length > 0 ? (byte)_st.ColSep[0] : (byte)0;
        if (!quote)
        {
            foreach (byte b in v)
            {
                if (b <= 0x20 || b >= 0x7f || b is (byte)'"' or (byte)'\'' || b == sep)
                {
                    quote = true;
                    break;
                }
            }
        }

        if (!quote)
        {
            _out.Write(Escape(v));
            return;
        }

        _out.Write((byte)'"');
        foreach (byte b in Escape(v))
        {
            if (b == (byte)'"')
            {
                _out.Write((byte)'"');
            }

            _out.Write(b);
        }

        _out.Write((byte)'"');
    }

    private void LineRow(Cell[] cells)
    {
        if (_rowCount > 1)
        {
            _out.Write("\n");
        }

        int w = 0;
        foreach (string n in _names)
        {
            w = Math.Max(w, SqlText.DisplayWidth(SqlText.Utf8(n)));
        }

        for (int i = 0; i < cells.Length; i++)
        {
            int nw = SqlText.DisplayWidth(SqlText.Utf8(_names[i]));
            _out.Write(new string(' ', w - nw));
            _out.Write(_names[i]);
            _out.Write(": ");
            ReadOnlySpan<byte> t = TextOf(cells[i]);
            int start = 0;
            for (int k = 0; k < t.Length; k++)
            {
                if (t[k] == (byte)'\n')
                {
                    _out.Write(t[start..(k + 1)]);
                    _out.Write(new string(' ', w + 2));
                    start = k + 1;
                }
            }

            _out.Write(t[start..]);
            _out.Write("\n");
        }
    }

    private void JsonRow(Cell[] cells)
    {
        _out.Write(_rowCount == 1 ? "[{" : ",\n{");
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                _out.Write(",");
            }

            JsonString(SqlText.Utf8(_names[i]));
            _out.Write(":");
            Cell c = cells[i];
            if (c.Data is null)
            {
                _out.Write("null");
            }
            else if (c.Type is ColumnType.Integral or ColumnType.Real)
            {
                string s = Encoding.UTF8.GetString(c.Data);
                _out.Write(s);
            }
            else if (c.Type == ColumnType.Blob)
            {
                _out.Write("\"");
                foreach (byte b in c.Data)
                {
                    _out.Write("\\u" + b.ToString("x4", CultureInfo.InvariantCulture));
                }

                _out.Write("\"");
            }
            else
            {
                JsonString(UpToNul(c.Data));
            }
        }

        _out.Write("}");
    }

    private void JsonString(ReadOnlySpan<byte> v)
    {
        _out.Write((byte)'"');
        foreach (byte b in v)
        {
            switch (b)
            {
                case (byte)'"':
                    _out.Write("\\\"");
                    break;
                case (byte)'\\':
                    _out.Write("\\\\");
                    break;
                case 8:
                    _out.Write("\\b");
                    break;
                case 12:
                    _out.Write("\\f");
                    break;
                case 10:
                    _out.Write("\\n");
                    break;
                case 13:
                    _out.Write("\\r");
                    break;
                case 9:
                    _out.Write("\\t");
                    break;
                default:
                    if (b < 0x20)
                    {
                        _out.Write("\\u" + ((int)b).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _out.Write(b);
                    }

                    break;
            }
        }

        _out.Write((byte)'"');
    }

    private void HtmlRow(Cell[] cells)
    {
        if (_rowCount == 1 && _st.Headers)
        {
            _out.Write("<TR>\n");
            foreach (string n in _names)
            {
                _out.Write("<TH>");
                HtmlText(SqlText.Utf8(n));
                _out.Write("\n");
            }

            _out.Write("</TR>\n");
        }

        _out.Write("<TR>\n");
        foreach (Cell c in cells)
        {
            _out.Write("<TD>");
            HtmlText(c.Data is null ? "null"u8 : TextOf(c));
            _out.Write("\n");
        }

        _out.Write("</TR>\n");
    }

    private void HtmlText(ReadOnlySpan<byte> v)
    {
        foreach (byte b in v)
        {
            switch (b)
            {
                case (byte)'&':
                    _out.Write("&amp;");
                    break;
                case (byte)'<':
                    _out.Write("&lt;");
                    break;
                case (byte)'>':
                    _out.Write("&gt;");
                    break;
                case (byte)'"':
                    _out.Write("&quot;");
                    break;
                case (byte)'\'':
                    _out.Write("&#39;");
                    break;
                default:
                    _out.Write(b);
                    break;
            }
        }
    }

    private static byte[] TclQuote(ReadOnlySpan<byte> v)
    {
        var sb = new List<byte> { (byte)'"' };
        foreach (byte b in v)
        {
            switch (b)
            {
                case (byte)'\\':
                case (byte)'"':
                    sb.Add((byte)'\\');
                    sb.Add(b);
                    break;
                case 9:
                    sb.AddRange("\\t"u8.ToArray());
                    break;
                case 10:
                    sb.AddRange("\\n"u8.ToArray());
                    break;
                case 13:
                    sb.AddRange("\\r"u8.ToArray());
                    break;
                default:
                    if (b < 0x20)
                    {
                        sb.AddRange(Encoding.ASCII.GetBytes("\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
                    }
                    else
                    {
                        sb.Add(b);
                    }

                    break;
            }
        }

        sb.Add((byte)'"');
        return [.. sb];
    }

    // ------------------------------------------------------------------ quoting

    /// <summary>The value as an SQL literal, the way the quote() function prints it.</summary>
    public static byte[] QuoteCell(Cell c, string nullText = "NULL")
    {
        if (c.Data is null)
        {
            return SqlText.Utf8(nullText);
        }

        switch (c.Type)
        {
            case ColumnType.Integral:
            case ColumnType.Real:
                return c.Data;
            case ColumnType.Blob:
                return Encoding.ASCII.GetBytes("x'" + Convert.ToHexStringLower(c.Data) + "'");
            default:
                return QuoteText(UpToNul(c.Data));
        }
    }

    /// <summary>Text as an SQL string literal; control characters switch to the <c>unistr()</c> form with <c>\uXXXX</c> escapes.</summary>
    public static byte[] QuoteText(ReadOnlySpan<byte> v)
    {
        bool uni = false;
        foreach (byte b in v)
        {
            if (b < 0x20)
            {
                uni = true;
                break;
            }
        }

        var o = new List<byte>(v.Length + 8);
        if (uni)
        {
            o.AddRange("unistr("u8.ToArray());
        }

        o.Add((byte)'\'');
        foreach (byte b in v)
        {
            if (b == (byte)'\'')
            {
                o.Add(b);
                o.Add(b);
            }
            else if (uni && b == (byte)'\\')
            {
                o.Add(b);
                o.Add(b);
            }
            else if (uni && b < 0x20)
            {
                o.AddRange(Encoding.ASCII.GetBytes("\\u" + ((int)b).ToString("x4", CultureInfo.InvariantCulture)));
            }
            else
            {
                o.Add(b);
            }
        }

        o.Add((byte)'\'');
        if (uni)
        {
            o.Add((byte)')');
        }

        return [.. o];
    }

    // ------------------------------------------------------------------ buffered (aligned) modes

    private static string[] Lines(ReadOnlySpan<byte> v, out int maxWidth)
    {
        string text = Encoding.UTF8.GetString(v).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] parts = text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = ExpandTabs(parts[i]);
        }

        maxWidth = 0;
        foreach (string p in parts)
        {
            maxWidth = Math.Max(maxWidth, SqlText.DisplayWidth(SqlText.Utf8(p)));
        }

        return parts;
    }

    /// <summary>Tabs in aligned output become spaces up to the next multiple of 8 display columns.</summary>
    private static string ExpandTabs(string s)
    {
        if (!s.Contains('\t', StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new StringBuilder();
        int col = 0;
        foreach (Rune r in s.EnumerateRunes())
        {
            if (r.Value == 9)
            {
                int n = 8 - col % 8;
                sb.Append(' ', n);
                col += n;
            }
            else
            {
                sb.Append(r.ToString());
                col += SqlText.DisplayWidth(SqlText.Utf8(r.ToString()));
            }
        }

        return sb.ToString();
    }

    private void Buffered()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        int nc = _names.Length;
        bool qbox = _st.Mode == OutputMode.Qbox;
        string[][][] lines = new string[_rows.Count][][];
        bool[][] right = new bool[_rows.Count][];
        int[] width = new int[nc];
        bool multi = false;
        string[][] headLines = new string[nc][];
        for (int c = 0; c < nc; c++)
        {
            headLines[c] = Lines(SqlText.Utf8(_names[c]), out int hw);
            width[c] = _st.Headers ? hw : 0;
        }

        for (int r = 0; r < _rows.Count; r++)
        {
            lines[r] = new string[nc][];
            right[r] = new bool[nc];
            for (int c = 0; c < nc; c++)
            {
                Cell cell = _rows[r][c];
                byte[] shown = qbox ? QuoteCellQbox(cell) : cell.Data is null ? SqlText.Utf8(_st.NullValue) : Escape(UpToNul(cell.Data));
                lines[r][c] = Lines(shown, out int w);
                right[r][c] = cell.Data is not null && cell.Type is ColumnType.Integral or ColumnType.Real;
                width[c] = Math.Max(width[c], w);
                multi |= lines[r][c].Length > 1;
            }
        }

        if (_st.Mode == OutputMode.Column)
        {
            for (int c = 0; c < nc; c++)
            {
                if (c < _st.Widths.Count && _st.Widths[c] != 0)
                {
                    width[c] = Math.Abs(_st.Widths[c]);
                }
            }
        }

        switch (_st.Mode)
        {
            case OutputMode.Column:
                ColumnOut(lines, right, width, headLines, multi);
                break;
            case OutputMode.Markdown:
                MarkdownOut(lines, right, width, headLines);
                break;
            default:
                BoxOut(lines, right, width, headLines, multi);
                break;
        }
    }

    private byte[] QuoteCellQbox(Cell c) => QuoteCell(c, _st.NullValue);

    private static string Pad(string s, int w, bool alignRight, bool center)
    {
        int sw = SqlText.DisplayWidth(SqlText.Utf8(s));
        int pad = Math.Max(0, w - sw);
        if (center)
        {
            int l = pad / 2;
            return new string(' ', l) + s + new string(' ', pad - l);
        }

        return alignRight ? new string(' ', pad) + s : s + new string(' ', pad);
    }

    private static string FitTo(string s, int w)
    {
        // An explicit column width shorter than the text cuts the text off at that many display columns.
        if (SqlText.DisplayWidth(SqlText.Utf8(s)) <= w)
        {
            return s;
        }

        var sb = new StringBuilder();
        int used = 0;
        foreach (Rune r in s.EnumerateRunes())
        {
            int cw = SqlText.DisplayWidth(SqlText.Utf8(r.ToString()));
            if (used + cw > w)
            {
                break;
            }

            sb.Append(r.ToString());
            used += cw;
        }

        return sb.ToString();
    }

    private void ColumnOut(string[][][] lines, bool[][] right, int[] width, string[][] headLines, bool multi)
    {
        int nc = width.Length;
        if (_st.Headers)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < nc; c++)
            {
                if (c > 0)
                {
                    sb.Append("  ");
                }

                sb.Append(Pad(FitTo(headLines[c][0], width[c]), width[c], false, true));
            }

            _out.Write(sb.ToString().TrimEnd(' ') + "\n");
            sb.Clear();
            for (int c = 0; c < nc; c++)
            {
                if (c > 0)
                {
                    sb.Append("  ");
                }

                sb.Append('-', width[c]);
            }

            _out.Write(sb.ToString() + "\n");
        }

        for (int r = 0; r < lines.Length; r++)
        {
            int depth = 1;
            for (int c = 0; c < nc; c++)
            {
                depth = Math.Max(depth, lines[r][c].Length);
            }

            for (int d = 0; d < depth; d++)
            {
                var sb = new StringBuilder();
                for (int c = 0; c < nc; c++)
                {
                    if (c > 0)
                    {
                        sb.Append("  ");
                    }

                    string t = d < lines[r][c].Length ? FitTo(lines[r][c][d], width[c]) : string.Empty;
                    bool rt = right[r][c] || (c < _st.Widths.Count && _st.Widths[c] < 0);
                    sb.Append(Pad(t, width[c], rt, false));
                }

                _out.Write(sb.ToString().TrimEnd(' ') + "\n");
            }

            if (multi && r < lines.Length - 1)
            {
                _out.Write("\n");
            }
        }
    }

    private void MarkdownOut(string[][][] lines, bool[][] right, int[] width, string[][] headLines)
    {
        int nc = width.Length;
        if (_st.Headers)
        {
            var sb = new StringBuilder("|");
            for (int c = 0; c < nc; c++)
            {
                sb.Append(' ').Append(Pad(headLines[c][0], width[c], false, true)).Append(" |");
            }

            _out.Write(sb + "\n");
            sb.Clear().Append('|');
            for (int c = 0; c < nc; c++)
            {
                sb.Append('-', width[c] + 2).Append('|');
            }

            _out.Write(sb + "\n");
        }

        for (int r = 0; r < lines.Length; r++)
        {
            int depth = 1;
            for (int c = 0; c < nc; c++)
            {
                depth = Math.Max(depth, lines[r][c].Length);
            }

            for (int d = 0; d < depth; d++)
            {
                var sb = new StringBuilder("|");
                for (int c = 0; c < nc; c++)
                {
                    string t = d < lines[r][c].Length ? lines[r][c][d] : string.Empty;
                    sb.Append(' ').Append(Pad(t, width[c], right[r][c], false)).Append(" |");
                }

                _out.Write(sb + "\n");
            }
        }
    }

    private void BoxOut(string[][][] lines, bool[][] right, int[] width, string[][] headLines, bool multi)
    {
        bool box = _st.Mode != OutputMode.Table;
        int nc = width.Length;
        string h = box ? "─" : "-";
        string Rule(string left, string mid, string rightEnd, string fill)
        {
            var sb = new StringBuilder(left);
            for (int c = 0; c < nc; c++)
            {
                if (c > 0)
                {
                    sb.Append(mid);
                }

                for (int k = 0; k < width[c] + 2; k++)
                {
                    sb.Append(fill);
                }
            }

            return sb.Append(rightEnd).Append('\n').ToString();
        }

        string vbar = box ? "│" : "|";
        string top = box ? Rule("╭", "┬", "╮", h) : Rule("+", "+", "+", h);
        string mid2 = box ? Rule("├", "┼", "┤", h) : Rule("+", "+", "+", h);
        string head = box ? Rule("╞", "╪", "╡", "═") : Rule("+", "+", "+", h);
        string bottom = box ? Rule("╰", "┴", "╯", h) : Rule("+", "+", "+", h);
        _out.Write(top);
        if (_st.Headers)
        {
            var sb = new StringBuilder(vbar);
            for (int c = 0; c < nc; c++)
            {
                sb.Append(' ').Append(Pad(headLines[c][0], width[c], false, true)).Append(' ').Append(vbar);
            }

            _out.Write(sb + "\n");
            _out.Write(head);
        }

        for (int r = 0; r < lines.Length; r++)
        {
            int depth = 1;
            for (int c = 0; c < nc; c++)
            {
                depth = Math.Max(depth, lines[r][c].Length);
            }

            for (int d = 0; d < depth; d++)
            {
                var sb = new StringBuilder(vbar);
                for (int c = 0; c < nc; c++)
                {
                    string t = d < lines[r][c].Length ? lines[r][c][d] : string.Empty;
                    sb.Append(' ').Append(Pad(t, width[c], right[r][c], false)).Append(' ').Append(vbar);
                }

                _out.Write(sb + "\n");
            }

            if (multi && r < lines.Length - 1)
            {
                _out.Write(mid2);
            }
        }

        _out.Write(bottom);
    }
}

/// <summary>The settings <c>.mode</c>, <c>.headers</c>, <c>.separator</c> and friends change.</summary>
internal sealed class Settings
{
    public EscapeMode Escape = EscapeMode.Ascii;
    public OutputMode Mode = OutputMode.List;
    public bool Headers;
    public string ColSep = "|";
    public string RowSep = "\n";
    public string NullValue = string.Empty;
    public string InsertTable = "tab";
    public List<int> Widths = [];
}
