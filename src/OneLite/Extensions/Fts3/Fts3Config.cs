using System.Linq;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Internal.Text;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>Where the text of the rows lives (sqlite.org/fts3.html, "The content= option").</summary>
internal enum Fts3Content
{
    /// <summary>In the table's own %_content shadow table.</summary>
    Normal,

    /// <summary><c>content=""</c>: the text is not kept.</summary>
    None,

    /// <summary><c>content=name</c>: read from another table.</summary>
    External,
}

/// <summary>The settings of one fts3 or fts4 table, taken from the arguments of <c>CREATE VIRTUAL TABLE</c>.</summary>
internal sealed class Fts3Config
{
    /// <summary>The tokenizer's name and arguments (fts3 "Tokenizers"): <c>simple</c>, <c>porter</c> or <c>unicode61</c>.</summary>
    internal sealed class TokenizerSpec
    {
        internal string Name = "simple";
        internal List<string> Args = [];
    }

    internal string Schema = "main";
    internal string Table = "";
    internal bool IsFts4;
    internal List<string> Columns = [];
    internal bool[] NotIndexed = [];
    internal List<int> Prefixes = [];
    internal TokenizerSpec Tokenizer = new();
    internal Fts3Content Content = Fts3Content.Normal;
    internal string? ContentTable;
    internal string? Compress;
    internal string? Uncompress;
    internal string? LangidColumn;
    internal bool MatchinfoFts3;
    internal bool Descending;

    internal int ColumnCount => Columns.Count;

    /// <summary>The number of indexes: the full-term index and one per <c>prefix=</c> length.</summary>
    internal int IndexCount => 1 + Prefixes.Count;

    internal bool HasContentTable => Content == Fts3Content.Normal;

    internal bool HasDocSize => IsFts4 && !MatchinfoFts3;

    internal bool HasStat => IsFts4;

    internal static OneLiteException Err(string msg) => new(ResultCode.Error, msg);

    /// <summary>A constructor failure the reference reports without a message of its own.</summary>
    internal static OneLiteException CtorFail(string table) => new(ResultCode.Error, "vtable constructor failed: " + table);

    private static string Q(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>The <c>CREATE TABLE</c> statements of the shadow tables, in creation order.</summary>
    internal List<(string Name, string Sql)> ShadowSql()
    {
        var l = new List<(string, string)>();
        if (HasContentTable)
        {
            var sb = new StringBuilder("CREATE TABLE ").Append(Q(Table + "_content")).Append("(docid INTEGER PRIMARY KEY");
            for (int i = 0; i < Columns.Count; i++)
            {
                sb.Append(", ").Append(Q("c" + i.ToString(CultureInfo.InvariantCulture) + Columns[i]));
            }

            if (LangidColumn is not null)
            {
                sb.Append(", langid");
            }

            l.Add((Table + "_content", sb.Append(')').ToString()));
        }

        l.Add((Table + "_segments", "CREATE TABLE " + Q(Table + "_segments") + "(blockid INTEGER PRIMARY KEY, block BLOB)"));
        l.Add((Table + "_segdir", "CREATE TABLE " + Q(Table + "_segdir") + "(level INTEGER,idx INTEGER,start_block INTEGER,leaves_end_block INTEGER,end_block INTEGER,root BLOB,PRIMARY KEY(level, idx))"));
        if (HasDocSize)
        {
            l.Add((Table + "_docsize", "CREATE TABLE " + Q(Table + "_docsize") + "(docid INTEGER PRIMARY KEY, size BLOB)"));
        }

        if (HasStat)
        {
            l.Add((Table + "_stat", "CREATE TABLE " + Q(Table + "_stat") + "(id INTEGER PRIMARY KEY, value BLOB)"));
        }

        return l;
    }

    internal static string[] ShadowNames(Fts3Config c)
    {
        var l = new List<string>();
        foreach ((string n, _) in c.ShadowSql())
        {
            l.Add(n);
        }

        return [.. l];
    }

    private static string Dequote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2)
        {
            char q = s[0];
            char e = q == '[' ? ']' : q;
            if ((q is '\'' or '"' or '`' or '[') && s[^1] == e)
            {
                string inner = s[1..^1];
                return q == '[' ? inner : inner.Replace(new string(q, 2), new string(q, 1), StringComparison.Ordinal);
            }
        }

        return s;
    }

    // The end of the leading identifier of an argument (quoted or bare), or -1.
    private static int IdentEnd(string a)
    {
        if (a.Length == 0)
        {
            return -1;
        }

        char c = a[0];
        if (c is '\'' or '"' or '`' or '[')
        {
            char e = c == '[' ? ']' : c;
            int i = 1;
            while (i < a.Length)
            {
                if (a[i] == e)
                {
                    if (e != ']' && i + 1 < a.Length && a[i + 1] == e)
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                i++;
            }

            return -1;
        }

        int j = 0;
        while (j < a.Length && !char.IsWhiteSpace(a[j]) && a[j] != '=')
        {
            j++;
        }

        return j;
    }

    /// <summary>Parses the arguments of <c>CREATE VIRTUAL TABLE name USING fts3(...)</c> or <c>fts4(...)</c>.</summary>
    internal static Fts3Config Parse(string schema, string table, bool fts4, ImmutableArray<string> args)
    {
        var c = new Fts3Config { Schema = schema, Table = table, IsFts4 = fts4 };
        string? notIndexed = null;
        var niList = new List<string>();
        bool badColumn = false;
        foreach (string raw in args)
        {
            string a = raw.Trim();
            int ie = IdentEnd(a);
            int p = ie;
            while (p >= 0 && p < a.Length && char.IsWhiteSpace(a[p]))
            {
                p++;
            }

            if (ie > 0 && p < a.Length && a[p] == '=')
            {
                string key = Dequote(a[..ie]).ToLowerInvariant();
                string rest = a[(p + 1)..].Trim();
                string val = rest.Length > 0 && rest[0] is '\'' or '"' or '`' or '[' ? Dequote(rest) : rest;
                switch (key)
                {
                    case "matchinfo":
                        if (!val.Equals("fts3", StringComparison.OrdinalIgnoreCase))
                        {
                            throw Err("unrecognized matchinfo: " + val);
                        }

                        c.MatchinfoFts3 = true;
                        break;
                    case "order":
                        if (val.Equals("desc", StringComparison.OrdinalIgnoreCase))
                        {
                            c.Descending = true;
                        }
                        else if (val.Equals("asc", StringComparison.OrdinalIgnoreCase))
                        {
                            c.Descending = false;
                        }
                        else
                        {
                            throw Err("unrecognized order: " + val);
                        }

                        break;
                    case "prefix":
                        c.Prefixes.Clear();
                        if (val.Length > 0)
                        {
                            foreach (string w in val.Split(','))
                            {
                                if (w.Length == 0 || !w.All(ch => ch is >= '0' and <= '9') || !int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                                {
                                    throw Err("error parsing prefix parameter: " + val);
                                }

                                if (n > 0)
                                {
                                    c.Prefixes.Add(n);
                                }
                            }
                        }

                        break;
                    case "notindexed":
                        niList.Add(val);
                        notIndexed = val;
                        break;
                    case "content":
                        if (val.Length == 0)
                        {
                            c.Content = Fts3Content.None;
                        }
                        else
                        {
                            c.Content = Fts3Content.External;
                            c.ContentTable = val;
                        }

                        break;
                    case "compress":
                        c.Compress = val;
                        break;
                    case "uncompress":
                        c.Uncompress = val;
                        break;
                    case "languageid":
                        c.LangidColumn = val;
                        break;
                    case "tokenize":
                    {
                        List<string> w = Words(val);
                        if (w.Count > 0)
                        {
                            c.Tokenizer = new TokenizerSpec { Name = w[0], Args = [.. w.Skip(1)] };
                        }

                        break;
                    }

                    default:
                        throw Err("unrecognized parameter: " + a);
                }

                continue;
            }

            if (ie <= 0)
            {
                badColumn = true;
                continue;
            }

            string name = Dequote(a[..ie]);
            if (c.Columns.Contains(name, StringComparer.OrdinalIgnoreCase) || name.Equals("docid", StringComparison.OrdinalIgnoreCase))
            {
                badColumn = true;
            }

            c.Columns.Add(name);
        }

        if (badColumn)
        {
            throw CtorFail(table);
        }

        if (c.Columns.Count == 0)
        {
            c.Columns.Add("content");
        }

        if (c.Compress is not null && c.Uncompress is null)
        {
            throw Err("missing uncompress parameter in fts4 constructor");
        }

        if (c.Uncompress is not null && c.Compress is null)
        {
            throw Err("missing compress parameter in fts4 constructor");
        }

        c.NotIndexed = new bool[c.Columns.Count];
        foreach (string list in niList)
        {
            string col = list;
            int idx = c.Columns.FindIndex(x => x.Equals(col, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                throw Err("no such column: " + col);
            }

            c.NotIndexed[idx] = true;
        }

        _ = notIndexed;
        if (c.LangidColumn is not null && c.Columns.Contains(c.LangidColumn, StringComparer.OrdinalIgnoreCase))
        {
            throw CtorFail(table);
        }

        // The tokenizer is built last so its errors come after the argument errors above.
        c.BuildTokenizer();
        return c;
    }

    /// <summary>The tokenizer built from <see cref="Tokenizer"/>.</summary>
    internal FtsTokenizer Impl = null!;

    private void BuildTokenizer()
    {
        string name = Tokenizer.Name.ToLowerInvariant();
        switch (name)
        {
            case "simple":
                Impl = new AsciiTokenizer([]);
                break;
            case "porter":
                Impl = new Fts3PorterTokenizer();
                break;
            case "unicode61":
            {
                var opts = new List<string>();
                foreach (string a in Tokenizer.Args)
                {
                    int eq = a.IndexOf('=', StringComparison.Ordinal);
                    string k = eq < 0 ? a : a[..eq];
                    if (!k.Equals("remove_diacritics", StringComparison.OrdinalIgnoreCase) && !k.Equals("tokenchars", StringComparison.OrdinalIgnoreCase)
                        && !k.Equals("separators", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Err("unknown tokenizer");
                    }

                    // the shared tokenizer takes its options as separate words: remove_diacritics 0
                    opts.Add(k);
                    if (eq >= 0)
                    {
                        opts.Add(a[(eq + 1)..]);
                    }
                }

                try
                {
                    Impl = new Unicode61Tokenizer([.. opts]);
                }
                catch (FormatException)
                {
                    throw Err("unknown tokenizer");
                }

                break;
            }

            default:
                throw Err("unknown tokenizer: " + Tokenizer.Name);
        }
    }

    /// <summary>Splits an option value such as <c>unicode61 "tokenchars=-_"</c> into words, honouring quotes.</summary>
    internal static List<string> Words(string s)
    {
        var l = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
                continue;
            }

            if (s[i] is '\'' or '"')
            {
                char q = s[i++];
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    if (s[i] == q)
                    {
                        if (i + 1 < s.Length && s[i + 1] == q)
                        {
                            sb.Append(q);
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    sb.Append(s[i++]);
                }

                i++;
                l.Add(sb.ToString());
            }
            else
            {
                int st = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                l.Add(s[st..i]);
            }
        }

        return l;
    }
}
