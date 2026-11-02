using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite.Internal.Text;

/// <summary>Why a tokenizer is being run (sqlite.org/fts5.html, "Custom Tokenizers").</summary>
[Flags]
internal enum TokenizeReason
{
    /// <summary>Indexing a document value.</summary>
    Document = 1,

    /// <summary>Tokenizing a MATCH query string.</summary>
    Query = 2,

    /// <summary>Tokenizing a query term that carries a prefix <c>*</c>.</summary>
    Prefix = 4,

    /// <summary>Tokenizing text for an auxiliary function such as highlight().</summary>
    Aux = 8,
}

/// <summary>Receives the tokens of one tokenizer run.</summary>
internal abstract class TokenSink
{
    /// <summary>One token. <paramref name="start"/> and <paramref name="end"/> are byte offsets of the token in the input text.</summary>
    internal abstract void Token(ReadOnlySpan<byte> token, int start, int end);
}

/// <summary>A full-text tokenizer. Engine-independent: text goes in as UTF-8, tokens come out as UTF-8.</summary>
internal abstract class FtsTokenizer
{
    /// <summary>Splits <paramref name="text"/> into tokens, calling the sink in text order.</summary>
    internal abstract void Tokenize(ReadOnlySpan<byte> text, TokenizeReason reason, TokenSink sink);

    /// <summary>
    /// Builds a tokenizer from a name and its option words, as written in <c>tokenize = '...'</c>; throws <see cref="FormatException"/> with
    /// a short reason for unknown tokenizers or options.
    /// </summary>
    internal static FtsTokenizer Create(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return new Unicode61Tokenizer([]);
        }

        string name = words[0].ToLowerInvariant();
        string[] rest = [.. words.Skip(1)];
        switch (name)
        {
            case "unicode61":
                return new Unicode61Tokenizer(rest);
            case "ascii":
                return new AsciiTokenizer(rest);
            case "trigram":
                return new TrigramTokenizer(rest);
            case "porter":
                return new PorterTokenizer(rest.Length == 0 ? new Unicode61Tokenizer([]) : Create(rest));
            default:
                throw new FormatException("no such tokenizer: " + words[0]);
        }
    }
}

/// <summary>UTF-8 reading and the Unicode 6.1 character tables the tokenizers use.</summary>
internal static class Utf8Text
{
    /// <summary>Decodes one code point at <paramref name="i"/>; malformed sequences read as U+FFFD, one byte at a time.</summary>
    internal static int Next(ReadOnlySpan<byte> s, ref int i)
    {
        int b = s[i];
        if (b < 0x80)
        {
            i++;
            return b;
        }

        int need = b >= 0xF0 ? 3 : b >= 0xE0 ? 2 : b >= 0xC2 ? 1 : -1;
        if (need < 0 || b > 0xF4 || i + need >= s.Length)
        {
            i++;
            return 0xFFFD;
        }

        int cp = need == 1 ? b & 0x1F : need == 2 ? b & 0x0F : b & 0x07;
        for (int k = 1; k <= need; k++)
        {
            int c = s[i + k];
            if ((c & 0xC0) != 0x80)
            {
                i++;
                return 0xFFFD;
            }

            cp = (cp << 6) | (c & 0x3F);
        }

        if ((need == 2 && cp < 0x800) || (need == 3 && (cp < 0x10000 || cp > 0x10FFFF)) || (cp >= 0xD800 && cp < 0xE000))
        {
            i++;
            return 0xFFFD;
        }

        i += need + 1;
        return cp;
    }

    /// <summary>Appends a code point as UTF-8.</summary>
    internal static void Append(List<byte> dst, int cp)
    {
        if (cp < 0x80)
        {
            dst.Add((byte)cp);
        }
        else if (cp < 0x800)
        {
            dst.Add((byte)(0xC0 | (cp >> 6)));
            dst.Add((byte)(0x80 | (cp & 0x3F)));
        }
        else if (cp < 0x10000)
        {
            dst.Add((byte)(0xE0 | (cp >> 12)));
            dst.Add((byte)(0x80 | ((cp >> 6) & 0x3F)));
            dst.Add((byte)(0x80 | (cp & 0x3F)));
        }
        else
        {
            dst.Add((byte)(0xF0 | (cp >> 18)));
            dst.Add((byte)(0x80 | ((cp >> 12) & 0x3F)));
            dst.Add((byte)(0x80 | ((cp >> 6) & 0x3F)));
            dst.Add((byte)(0x80 | (cp & 0x3F)));
        }
    }

    /// <summary>The general category code (an index into <see cref="UnicodeTables.CategoryNames"/>) of a code point in Unicode 6.1; 0 is unassigned.</summary>
    internal static int Category(int cp)
    {
        int k = Array.BinarySearch(UnicodeTables.CatStart, cp);
        if (k < 0)
        {
            k = ~k - 1;
        }

        return UnicodeTables.CatCode[k];
    }

    /// <summary>The simple case folding of a code point (CaseFolding.txt statuses C and S).</summary>
    internal static int Fold(int cp)
    {
        if (cp < 0x80)
        {
            return cp >= 'A' && cp <= 'Z' ? cp + 32 : cp;
        }

        int k = Array.BinarySearch(UnicodeTables.FoldFrom, cp);
        return k >= 0 ? UnicodeTables.FoldTo[k] : cp;
    }

    /// <summary>True for the combining marks the tokenizer removes as diacritics.</summary>
    internal static bool IsDiacriticMark(int cp)
    {
        if (cp < 0x300 || cp > 0x331)
        {
            return false;
        }

        return cp <= 0x304 || (cp >= 0x306 && cp <= 0x30C) || cp == 0x30F || cp == 0x311 || cp == 0x31B || (cp >= 0x323 && cp <= 0x328) || cp == 0x32D || cp == 0x32E || cp >= 0x330;
    }

    /// <summary>The letter with its removable marks stripped, for <paramref name="mode"/> 1 (only letters with a single mark) or 2 (all).</summary>
    internal static int StripDiacritics(int cp, int mode)
    {
        if (cp < 0xC0)
        {
            return cp;
        }

        int k = Array.BinarySearch(UnicodeTables.DiaFrom, cp);
        if (k < 0)
        {
            return cp;
        }

        return mode == 2 || UnicodeTables.DiaMarks[k] == 1 ? UnicodeTables.DiaBase[k] : cp;
    }

    /// <summary>Parses the option words of tokenizers that take <c>name value</c> pairs.</summary>
    internal static Dictionary<string, string> Options(string[] words, params string[] known)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (words.Length % 2 != 0)
        {
            throw new FormatException("missing value for option: " + words[^1]);
        }

        for (int i = 0; i < words.Length; i += 2)
        {
            if (Array.IndexOf(known, words[i].ToLowerInvariant()) < 0)
            {
                throw new FormatException("unrecognized option: " + words[i]);
            }

            d[words[i].ToLowerInvariant()] = words[i + 1];
        }

        return d;
    }

    /// <summary>The code points of a UTF-16 option string.</summary>
    internal static int[] CodePoints(string s)
    {
        var l = new List<int>();
        for (int i = 0; i < s.Length; i++)
        {
            int c = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                c = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }

            l.Add(c);
        }

        return [.. l];
    }
}

/// <summary>The <c>unicode61</c> tokenizer: Unicode 6.1 token characters, simple case folding and optional diacritic removal.</summary>
internal sealed class Unicode61Tokenizer : FtsTokenizer
{
    private readonly int _removeDiacritics = 1;
    private readonly bool[] _cat = new bool[UnicodeTables.CategoryNames.Length];
    private readonly HashSet<int> _tokenChars = [];
    private readonly HashSet<int> _separators = [];

    internal Unicode61Tokenizer(string[] options)
    {
        Dictionary<string, string> o = Utf8Text.Options(options, "remove_diacritics", "categories", "tokenchars", "separators");
        if (o.TryGetValue("remove_diacritics", out string? rd))
        {
            if (rd.Length != 1 || rd[0] < '0' || rd[0] > '2')
            {
                throw new FormatException("invalid value for remove_diacritics: " + rd);
            }

            _removeDiacritics = rd[0] - '0';
        }

        foreach (string c in (o.TryGetValue("categories", out string? cs) ? cs : "L* N* Co").Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (c.Length != 2)
            {
                throw new FormatException("invalid categories: " + c);
            }

            bool any = false;
            for (int k = 0; k < _cat.Length; k++)
            {
                string n = UnicodeTables.CategoryNames[k];
                if (n[0] == c[0] && (c[1] == '*' || n[1] == c[1]))
                {
                    _cat[k] = true;
                    any = true;
                }
            }

            if (!any)
            {
                throw new FormatException("invalid categories: " + c);
            }
        }

        if (o.TryGetValue("tokenchars", out string? tc))
        {
            _tokenChars.UnionWith(Utf8Text.CodePoints(tc));
        }

        if (o.TryGetValue("separators", out string? sp))
        {
            _separators.UnionWith(Utf8Text.CodePoints(sp));
        }
    }

    // A combining mark continues a token but never starts one (observed: a mark after a separator is skipped with the separator).
    private bool IsToken(int cp, bool inToken)
    {
        if (_tokenChars.Contains(cp))
        {
            return true;
        }

        if (_separators.Contains(cp))
        {
            return false;
        }

        // The tables are Unicode 6.1: anything unassigned there counts as a token character whatever the category list says
        // (observed), except the two noncharacters that end the basic plane.
        int c = Utf8Text.Category(cp);
        if (c == 0)
        {
            return cp != 0xFFFE && cp != 0xFFFF;
        }

        return _cat[c] || (inToken && Utf8Text.IsDiacriticMark(cp));
    }

    internal override void Tokenize(ReadOnlySpan<byte> text, TokenizeReason reason, TokenSink sink)
    {
        var buf = new List<byte>(32);
        int i = 0;
        while (i < text.Length)
        {
            int p = i;
            int cp = Utf8Text.Next(text, ref i);
            if (!IsToken(cp, false))
            {
                continue;
            }

            buf.Clear();
            int start = p;
            int end = i;
            for (; ; )
            {
                Add(buf, cp);
                end = i;
                if (i >= text.Length)
                {
                    break;
                }

                int save = i;
                cp = Utf8Text.Next(text, ref i);
                if (!IsToken(cp, true))
                {
                    i = save;
                    break;
                }
            }

            if (buf.Count > 0)
            {
                sink.Token(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf), start, end);
            }
        }
    }

    private void Add(List<byte> buf, int cp)
    {
        if (_removeDiacritics > 0)
        {
            if (Utf8Text.IsDiacriticMark(cp))
            {
                return;
            }

            cp = Utf8Text.StripDiacritics(cp, _removeDiacritics);
        }

        Utf8Text.Append(buf, Utf8Text.Fold(cp));
    }
}

/// <summary>The <c>ascii</c> tokenizer: ASCII letters and digits and every non-ASCII character are token characters; only ASCII is folded.</summary>
internal sealed class AsciiTokenizer : FtsTokenizer
{
    private readonly bool[] _token = new bool[128];

    internal AsciiTokenizer(string[] options)
    {
        Dictionary<string, string> o = Utf8Text.Options(options, "tokenchars", "separators");
        for (int c = 0; c < 128; c++)
        {
            _token[c] = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        if (o.TryGetValue("tokenchars", out string? tc))
        {
            foreach (char c in tc)
            {
                if (c < 128)
                {
                    _token[c] = true;
                }
            }
        }

        if (o.TryGetValue("separators", out string? sp))
        {
            foreach (char c in sp)
            {
                if (c < 128)
                {
                    _token[c] = false;
                }
            }
        }
    }

    internal override void Tokenize(ReadOnlySpan<byte> text, TokenizeReason reason, TokenSink sink)
    {
        var buf = new List<byte>(32);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] < 128 && !_token[text[i]])
            {
                i++;
                continue;
            }

            int start = i;
            buf.Clear();
            while (i < text.Length && (text[i] >= 128 || _token[text[i]]))
            {
                byte b = text[i];
                buf.Add(b >= 'A' && b <= 'Z' ? (byte)(b + 32) : b);
                i++;
            }

            sink.Token(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf), start, i);
        }
    }
}

/// <summary>The <c>trigram</c> tokenizer: every run of three characters is a token.</summary>
internal sealed class TrigramTokenizer : FtsTokenizer
{
    private readonly bool _caseSensitive;
    private readonly int _removeDiacritics;

    internal TrigramTokenizer(string[] options)
    {
        Dictionary<string, string> o = Utf8Text.Options(options, "case_sensitive", "remove_diacritics");
        if (o.TryGetValue("case_sensitive", out string? cs))
        {
            if (cs is not ("0" or "1"))
            {
                throw new FormatException("invalid value for case_sensitive: " + cs);
            }

            _caseSensitive = cs == "1";
        }

        if (o.TryGetValue("remove_diacritics", out string? rd))
        {
            if (rd is not ("0" or "1"))
            {
                throw new FormatException("invalid value for remove_diacritics: " + rd);
            }

            _removeDiacritics = rd == "1" ? 1 : 0;
        }
    }

    /// <summary>True when the table folds case or marks (the query side then folds the same way).</summary>
    internal bool FoldsCase => !_caseSensitive;

    internal override void Tokenize(ReadOnlySpan<byte> text, TokenizeReason reason, TokenSink sink)
    {
        // Each character: its folded code point and its byte span in the input.
        var cps = new List<int>();
        var starts = new List<int>();
        int i = 0;
        while (i < text.Length)
        {
            int p = i;
            int cp = Utf8Text.Next(text, ref i);
            if (_removeDiacritics > 0)
            {
                if (Utf8Text.IsDiacriticMark(cp))
                {
                    continue;
                }

                cp = Utf8Text.StripDiacritics(cp, 1);
            }

            cps.Add(_caseSensitive ? cp : Utf8Text.Fold(cp));
            starts.Add(p);
        }

        starts.Add(text.Length);
        var buf = new List<byte>(12);
        for (int k = 0; k + 3 <= cps.Count; k++)
        {
            buf.Clear();
            Utf8Text.Append(buf, cps[k]);
            Utf8Text.Append(buf, cps[k + 1]);
            Utf8Text.Append(buf, cps[k + 2]);
            sink.Token(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf), starts[k], starts[k + 3]);
        }
    }
}

/// <summary>The <c>porter</c> tokenizer: the Porter stemmer applied to the tokens of another tokenizer.</summary>
internal sealed class PorterTokenizer : FtsTokenizer
{
    private readonly FtsTokenizer _inner;

    internal PorterTokenizer(FtsTokenizer inner) => _inner = inner;

    internal override void Tokenize(ReadOnlySpan<byte> text, TokenizeReason reason, TokenSink sink) => _inner.Tokenize(text, reason, new Stemming(sink));

    private sealed class Stemming(TokenSink next) : TokenSink
    {
        internal override void Token(ReadOnlySpan<byte> token, int start, int end)
        {
            if (token.Length > 64 || token.Length < 3)
            {
                next.Token(token, start, end);
                return;
            }

            byte[] stem = Porter.Stem(token);
            next.Token(stem, start, end);
        }
    }
}

/// <summary>The Porter stemming algorithm (M. F. Porter, "An algorithm for suffix stripping", Program 14(3), 1980).</summary>
internal static class Porter
{
    /// <summary>
    /// Stems a token byte by byte, as the reference does: bytes that are not the letters a-z (digits and every byte of a multi-byte
    /// character) count as consonants.
    /// </summary>
    internal static byte[] Stem(ReadOnlySpan<byte> token)
    {
        var w = new StringBuilder(Encoding.Latin1.GetString(token));
        Step1ab(w);
        if (w.Length > 0)
        {
            Step1c(w);
            Step2(w);
            Step3(w);
            Step4(w);
            Step5(w);
        }

        return Encoding.Latin1.GetBytes(w.ToString());
    }

    private static bool IsCons(StringBuilder w, int i)
    {
        switch (w[i])
        {
            case 'a':
            case 'e':
            case 'i':
            case 'o':
            case 'u':
                return false;
            case 'y':
                return i == 0 || !IsCons(w, i - 1);
            default:
                return true;
        }
    }

    /// <summary>The measure m of the stem w[0..len): the number of VC sequences.</summary>
    private static int Measure(StringBuilder w, int len)
    {
        int n = 0;
        int i = 0;
        while (i < len && IsCons(w, i))
        {
            i++;
        }

        for (; ; )
        {
            while (i < len && !IsCons(w, i))
            {
                i++;
            }

            if (i >= len)
            {
                return n;
            }

            n++;
            while (i < len && IsCons(w, i))
            {
                i++;
            }

            if (i >= len)
            {
                return n;
            }
        }
    }

    private static bool HasVowel(StringBuilder w, int len)
    {
        for (int i = 0; i < len; i++)
        {
            if (!IsCons(w, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DoubleCons(StringBuilder w, int len) => len >= 2 && w[len - 1] == w[len - 2] && IsCons(w, len - 1);

    /// <summary>*o: the stem ends consonant-vowel-consonant where the last consonant is not w, x or y.</summary>
    private static bool Cvc(StringBuilder w, int len)
    {
        if (len < 3 || !IsCons(w, len - 1) || IsCons(w, len - 2) || !IsCons(w, len - 3))
        {
            return false;
        }

        char c = w[len - 1];
        return c != 'w' && c != 'x' && c != 'y';
    }

    private static bool Ends(StringBuilder w, string s) => w.Length >= s.Length && w.ToString(w.Length - s.Length, s.Length) == s;

    private static void SetTo(StringBuilder w, int cut, string s)
    {
        w.Length -= cut;
        w.Append(s);
    }

    private static void Step1ab(StringBuilder w)
    {
        if (w[^1] == 's')
        {
            if (Ends(w, "sses"))
            {
                w.Length -= 2;
            }
            else if (Ends(w, "ies"))
            {
                SetTo(w, 2, "");
            }
            else if (w.Length >= 2 && w[^2] != 's')
            {
                w.Length -= 1;
            }
        }

        if (Ends(w, "eed"))
        {
            if (Measure(w, w.Length - 3) > 0)
            {
                w.Length -= 1;
            }
        }
        else if ((Ends(w, "ed") && HasVowel(w, w.Length - 2)) || (Ends(w, "ing") && HasVowel(w, w.Length - 3)))
        {
            w.Length -= Ends(w, "ed") ? 2 : 3;
            if (Ends(w, "at") || Ends(w, "bl") || Ends(w, "iz"))
            {
                w.Append('e');
            }
            else if (DoubleCons(w, w.Length))
            {
                char c = w[^1];
                if (c != 'l' && c != 's' && c != 'z')
                {
                    w.Length -= 1;
                }
            }
            else if (Measure(w, w.Length) == 1 && Cvc(w, w.Length))
            {
                w.Append('e');
            }
        }
    }

    private static void Step1c(StringBuilder w)
    {
        if (w[^1] == 'y' && HasVowel(w, w.Length - 1))
        {
            w[^1] = 'i';
        }
    }

    private static readonly (string, string)[] s_step2 =
    [
        ("ational", "ate"), ("tional", "tion"), ("enci", "ence"), ("anci", "ance"), ("izer", "ize"), ("bli", "ble"), ("alli", "al"),
        ("entli", "ent"), ("eli", "e"), ("ousli", "ous"), ("ization", "ize"), ("ation", "ate"), ("ator", "ate"), ("alism", "al"),
        ("iveness", "ive"), ("fulness", "ful"), ("ousness", "ous"), ("aliti", "al"), ("iviti", "ive"), ("biliti", "ble"), ("logi", "log"),
    ];

    private static readonly (string, string)[] s_step3 =
    [
        ("icate", "ic"), ("ative", ""), ("alize", "al"), ("iciti", "ic"), ("ical", "ic"), ("ful", ""), ("ness", ""),
    ];

    private static void Replace(StringBuilder w, (string, string)[] rules)
    {
        foreach ((string suf, string rep) in rules)
        {
            if (Ends(w, suf))
            {
                if (Measure(w, w.Length - suf.Length) > 0)
                {
                    SetTo(w, suf.Length, rep);
                }

                return;
            }
        }
    }

    private static void Step2(StringBuilder w) => Replace(w, s_step2);

    private static void Step3(StringBuilder w) => Replace(w, s_step3);

    private static readonly string[] s_step4 =
    [
        "al", "ance", "ence", "er", "ic", "able", "ible", "ant", "ement", "ment", "ent", "ion", "ou", "ism", "ate", "iti", "ous", "ive", "ize",
    ];

    private static void Step4(StringBuilder w)
    {
        foreach (string suf in s_step4)
        {
            if (Ends(w, suf))
            {
                int stem = w.Length - suf.Length;
                if (Measure(w, stem) > 1 && (suf != "ion" || (stem > 0 && (w[stem - 1] == 's' || w[stem - 1] == 't'))))
                {
                    w.Length = stem;
                }

                return;
            }
        }
    }

    private static void Step5(StringBuilder w)
    {
        if (w[^1] == 'e')
        {
            int m = Measure(w, w.Length - 1);
            if (m > 1 || (m == 1 && !Cvc(w, w.Length - 1)))
            {
                w.Length -= 1;
            }
        }

        if (w.Length > 1 && w[^1] == 'l' && DoubleCons(w, w.Length) && Measure(w, w.Length) > 1)
        {
            w.Length -= 1;
        }
    }
}
