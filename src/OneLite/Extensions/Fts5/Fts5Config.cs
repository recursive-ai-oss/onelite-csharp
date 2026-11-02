using System.Linq;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Internal.Text;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>Where the text of the rows lives (fts5 "External Content and Contentless Tables").</summary>
internal enum ContentMode
{
    /// <summary>In the table's own %_content shadow table.</summary>
    Normal,

    /// <summary><c>content=''</c>: not stored.</summary>
    None,

    /// <summary><c>content='tbl'</c>: read from another table.</summary>
    External,
}

/// <summary>How much of each token occurrence the index keeps (fts5 "The Detail Option").</summary>
internal enum DetailMode
{
    Full = 0,
    Column = 1,
    None = 2,
}

/// <summary>The settings of one fts5 table: from <c>CREATE VIRTUAL TABLE</c> and the %_config shadow table.</summary>
internal sealed class Fts5Config
{
    internal string Schema = "main";
    internal string Table = "";
    internal List<string> Columns = [];
    internal List<bool> Unindexed = [];
    internal List<int> Prefixes = [];
    internal string[] TokenizerWords = [];
    internal FtsTokenizer Tokenizer = new Unicode61Tokenizer([]);
    internal ContentMode Content = ContentMode.Normal;
    internal string? ContentTable;
    internal string ContentRowid = "rowid";
    internal bool ColumnSize = true;
    internal DetailMode Detail = DetailMode.Full;
    internal bool TokenData;
    internal bool Locale;
    internal bool ContentlessDelete;
    internal bool ContentlessUnindexed;

    // persistent options (%_config)
    internal int Pgsz = 4000;
    internal int Automerge = 4;
    internal int Crisismerge = 16;
    internal int Usermerge = 4;
    internal int Deletemerge = 10;
    internal string Rank = "bm25()";
    internal bool SecureDelete;

    internal int ColumnCount => Columns.Count;

    /// <summary>Shadow table names for a table called <paramref name="name"/>.</summary>
    internal static string[] ShadowNames(string name, bool hasContent, bool hasDocSize) =>
        [name + "_data", name + "_idx", .. (hasContent ? new[] { name + "_content" } : []), .. (hasDocSize ? new[] { name + "_docsize" } : []), name + "_config"];

    internal bool HasContentTable => Content == ContentMode.Normal || (Content == ContentMode.None && ContentlessUnindexed);

    /// <summary>True when %_docsize exists: not with <c>columnsize=0</c>, except that a contentless-delete table always has it.</summary>
    internal bool HasDocSize => ColumnSize || ContentlessDelete;

    internal static OneLiteException Err(string msg) => new(ResultCode.Error, msg);

    /// <summary>Splits a name that may be quoted with ', ", ` or [ ].</summary>
    internal static string Unquote(string s)
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

    /// <summary>Splits an option value such as <c>porter unicode61 tokenchars '-_'</c> into words, honouring quotes.</summary>
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
                for (; ; )
                {
                    if (i >= s.Length)
                    {
                        throw Err("parse error in \"" + s + "\"");
                    }

                    if (s[i] == q)
                    {
                        if (i + 1 < s.Length && s[i + 1] == q)
                        {
                            sb.Append(q);
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    sb.Append(s[i++]);
                }

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

    /// <summary>Parses the arguments of <c>CREATE VIRTUAL TABLE name USING fts5(...)</c>.</summary>
    internal static Fts5Config Parse(string schema, string table, ImmutableArray<string> args)
    {
        var c = new Fts5Config { Schema = schema, Table = table };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in args)
        {
            string a = raw.Trim();
            int eq = IndexOfEquals(a);
            if (eq >= 0)
            {
                string key = Unquote(a[..eq]).ToLowerInvariant();
                string val = Unquote(a[(eq + 1)..]);
                c.SetOption(key, val, a[..eq].Trim());
                continue;
            }

            // a column: name [UNINDEXED]
            string[] parts = SplitColumn(a);
            string name = Unquote(parts[0]);
            bool unindexed = false;
            if (parts.Length == 2 && parts[1].Equals("UNINDEXED", StringComparison.OrdinalIgnoreCase))
            {
                unindexed = true;
            }
            else if (parts.Length != 1)
            {
                throw Err("parse error in \"" + a + "\"");
            }

            if (name.Length == 0 || name[0] == '_' && false)
            {
                throw Err("parse error in \"" + a + "\"");
            }

            if (name.Equals("rank", StringComparison.OrdinalIgnoreCase) || name.Equals("rowid", StringComparison.OrdinalIgnoreCase))
            {
                throw Err("reserved fts5 column name: " + name);
            }

            if (name.Equals(table, StringComparison.OrdinalIgnoreCase))
            {
                throw Err("reserved fts5 column name: " + name);
            }

            if (!seen.Add(name))
            {
                throw Err("duplicate column name: " + name);
            }

            c.Columns.Add(name);
            c.Unindexed.Add(unindexed);
        }

        if (c.Columns.Count == 0)
        {
            throw Err("fts5: no columns specified");
        }

        if (c.Content == ContentMode.Normal && c.ContentlessDelete)
        {
            throw Err("contentless_delete=1 requires a contentless table");
        }

        if (c.ContentlessUnindexed && c.Content != ContentMode.None)
        {
            throw Err("contentless_unindexed=1 requires a contentless table");
        }

        if (c.Detail != DetailMode.Full && false)
        {
            throw Err("fts5: detail");
        }

        return c;
    }

    private static int IndexOfEquals(string a)
    {
        char q = '\0';
        for (int i = 0; i < a.Length; i++)
        {
            char ch = a[i];
            if (q != '\0')
            {
                if (ch == q)
                {
                    q = '\0';
                }

                continue;
            }

            if (ch is '\'' or '"' or '`')
            {
                q = ch;
            }
            else if (ch == '[')
            {
                q = ']';
            }
            else if (ch == '=')
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] SplitColumn(string a)
    {
        var l = new List<string>();
        int i = 0;
        while (i < a.Length)
        {
            if (char.IsWhiteSpace(a[i]))
            {
                i++;
                continue;
            }

            int st = i;
            if (a[i] is '\'' or '"' or '`' or '[')
            {
                char e = a[i] == '[' ? ']' : a[i];
                i++;
                while (i < a.Length && a[i] != e)
                {
                    i++;
                }

                i = Math.Min(a.Length, i + 1);
            }
            else
            {
                while (i < a.Length && !char.IsWhiteSpace(a[i]))
                {
                    i++;
                }
            }

            l.Add(a[st..i]);
        }

        return [.. l];
    }

    private void SetOption(string key, string val, string rawKey)
    {
        switch (key)
        {
            case "prefix":
                Prefixes.Clear();
                foreach (string w in val.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0 || n >= 1000)
                    {
                        throw Err("malformed prefix=... directive");
                    }

                    Prefixes.Add(n);
                }

                if (Prefixes.Count > 31)
                {
                    throw Err("too many prefix indexes (max 31)");
                }

                break;
            case "tokenize":
            {
                List<string> w = Words(val);
                TokenizerWords = [.. w];
                try
                {
                    Tokenizer = FtsTokenizer.Create(w);
                }
                catch (FormatException e)
                {
                    throw Err(e.Message);
                }

                break;
            }

            case "content":
                if (val.Length == 0)
                {
                    Content = ContentMode.None;
                }
                else
                {
                    Content = ContentMode.External;
                    ContentTable = val;
                }

                break;
            case "content_rowid":
                ContentRowid = val;
                break;
            case "columnsize":
                ColumnSize = Bool(val, "columnsize");
                break;
            case "detail":
                Detail = val.ToLowerInvariant() switch
                {
                    "full" => DetailMode.Full,
                    "column" => DetailMode.Column,
                    "none" => DetailMode.None,
                    _ => throw Err("malformed detail=... directive"),
                };
                break;
            case "tokendata":
                TokenData = Bool(val, "tokendata");
                break;
            case "locale":
                Locale = Bool(val, "locale");
                break;
            case "contentless_delete":
                ContentlessDelete = Bool(val, "contentless_delete");
                break;
            case "contentless_unindexed":
                ContentlessUnindexed = Bool(val, "contentless_unindexed");
                break;
            default:
                throw Err("unrecognized option: \"" + rawKey + "\"");
        }
    }

    private static bool Bool(string v, string name)
    {
        if (v is "0" or "1")
        {
            return v == "1";
        }

        throw Err("malformed " + name + "=... directive");
    }
}
