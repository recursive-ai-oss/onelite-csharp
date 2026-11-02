using RecursiveAI.OneLite.Storage;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// A growable output buffer that builds a function result directly in the destination register's own byte array,
/// so a function that has run once on a register allocates nothing the next time.
/// </summary>
internal struct Sink
{
    private byte[] _b;
    private int _n;

    internal Sink(ref byte[]? own, int capacity)
    {
        _b = own != null && own.Length >= capacity ? own : new byte[Math.Max(capacity, 64)];
        _n = 0;
    }

    internal readonly int Length => _n;

    internal readonly Span<byte> Written => _b.AsSpan(0, _n);

    private void Grow(int extra)
    {
        long need = (long)_n + extra;
        if (need > Values.MaxLength)
        {
            throw new OneLiteException(ResultCode.TooBig, "string or blob too big");
        }

        Array.Resize(ref _b, (int)Math.Max(need, Math.Min((long)_b.Length * 2, Values.MaxLength)));
    }

    internal void Put(byte c)
    {
        if (_n == _b.Length)
        {
            Grow(1);
        }

        _b[_n++] = c;
    }

    internal void Put(ReadOnlySpan<byte> s)
    {
        if ((long)_n + s.Length > _b.Length)
        {
            Grow(s.Length);
        }

        s.CopyTo(_b.AsSpan(_n));
        _n += s.Length;
    }

    internal void Put(byte c, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if ((long)_n + count > _b.Length)
        {
            Grow(count);
        }

        _b.AsSpan(_n, count).Fill(c);
        _n += count;
    }

    internal void PutAscii(string s)
    {
        foreach (char c in s)
        {
            Put((byte)c);
        }
    }

    /// <summary>Appends a code point as UTF-8 the way the reference writes it (surrogate code points are written as three bytes, not replaced).</summary>
    internal void PutCodePoint(int c)
    {
        if (c < 0x80)
        {
            Put((byte)c);
        }
        else if (c < 0x800)
        {
            Put((byte)(0xC0 | (c >> 6)));
            Put((byte)(0x80 | (c & 0x3F)));
        }
        else if (c < 0x10000)
        {
            Put((byte)(0xE0 | (c >> 12)));
            Put((byte)(0x80 | ((c >> 6) & 0x3F)));
            Put((byte)(0x80 | (c & 0x3F)));
        }
        else
        {
            Put((byte)(0xF0 | (c >> 18)));
            Put((byte)(0x80 | ((c >> 12) & 0x3F)));
            Put((byte)(0x80 | ((c >> 6) & 0x3F)));
            Put((byte)(0x80 | (c & 0x3F)));
        }
    }

    internal void Finish(ref Mem dst, ref byte[]? own, StorageClass kind)
    {
        own = _b;
        dst.Kind = kind;
        dst.Subtype = 0;
        dst.Buffer = _b;
        dst.Offset = 0;
        dst.Length = _n;
    }
}

internal static partial class Scalar
{
    /// <summary>The connection of the statement that is running, for changes(), total_changes() and last_insert_rowid().</summary>
    [ThreadStatic]
    internal static RecursiveAI.OneLite.Connection? Connection;

    // The options of the pinned build, in the order of PRAGMA compile_options (lang_corefunc sqlite_compileoption_get).
    internal static IReadOnlyList<string> CompileOptions => s_compileOptions;

    private static readonly string[] s_compileOptions =
    [
        "ATOMIC_INTRINSICS=1", "COMPILER=gcc-13.3.0", "DEFAULT_AUTOVACUUM", "DEFAULT_CACHE_SIZE=-2000", "DEFAULT_FILE_FORMAT=4", "DEFAULT_JOURNAL_SIZE_LIMIT=-1",
        "DEFAULT_MMAP_SIZE=0", "DEFAULT_PAGE_SIZE=4096", "DEFAULT_PCACHE_INITSZ=20", "DEFAULT_RECURSIVE_TRIGGERS", "DEFAULT_SECTOR_SIZE=4096",
        "DEFAULT_SYNCHRONOUS=2", "DEFAULT_WAL_AUTOCHECKPOINT=1000", "DEFAULT_WAL_SYNCHRONOUS=2", "DEFAULT_WORKER_THREADS=0", "DIRECT_OVERFLOW_READ", "DQS=0",
        "ENABLE_BYTECODE_VTAB", "ENABLE_CARRAY", "ENABLE_DBPAGE_VTAB", "ENABLE_DBSTAT_VTAB", "ENABLE_EXPLAIN_COMMENTS", "ENABLE_FTS3", "ENABLE_FTS4",
        "ENABLE_FTS5", "ENABLE_GEOPOLY", "ENABLE_MATH_FUNCTIONS", "ENABLE_OFFSET_SQL_FUNC", "ENABLE_PERCENTILE", "ENABLE_PREUPDATE_HOOK", "ENABLE_RTREE",
        "ENABLE_SESSION", "ENABLE_STMTVTAB", "ENABLE_STMT_SCANSTATUS", "ENABLE_UNKNOWN_SQL_FUNCTION", "HAVE_ISNAN", "MALLOC_SOFT_LIMIT=1024",
        "MAX_ATTACHED=10", "MAX_COLUMN=2000", "MAX_COMPOUND_SELECT=500", "MAX_DEFAULT_PAGE_SIZE=8192", "MAX_EXPR_DEPTH=1000", "MAX_FUNCTION_ARG=1000",
        "MAX_LENGTH=1000000000", "MAX_LIKE_PATTERN_LENGTH=50000", "MAX_MMAP_SIZE=0x7fff0000", "MAX_PAGE_COUNT=0xfffffffe", "MAX_PAGE_SIZE=65536",
        "MAX_SQL_LENGTH=1000000000", "MAX_TRIGGER_DEPTH=1000", "MAX_VARIABLE_NUMBER=32766", "MAX_VDBE_OP=250000000", "MAX_WORKER_THREADS=8",
        "MUTEX_PTHREADS", "STRICT_SUBTYPE", "SYSTEM_MALLOC", "TEMP_STORE=1", "THREADSAFE=1",
    ];

    /// <summary>The version of the pinned release this engine reproduces.</summary>
    internal const string SqliteVersion = "3.53.4";

    internal const string SqliteSourceId = "2026-07-24 19:02:57 bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc";

    private static OneLiteException TooBig() => new(ResultCode.TooBig, "string or blob too big");

    /// <summary>The code point at <paramref name="i"/> and its length, decoding UTF-8 as leniently as the reference: a malformed lead or short sequence reads as U+FFFD.</summary>
    internal static int ReadCodePoint(ReadOnlySpan<byte> s, int i, out int len)
    {
        int c = s[i];
        len = 1;
        if (c < 0xC0)
        {
            return c;
        }

        int n = c >= 0xF8 ? 0 : c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : 1;
        c = c >= 0xF8 ? c & 0x03 : c >= 0xF0 ? c & 0x07 : c >= 0xE0 ? c & 0x0F : c & 0x1F;
        int j = i + 1;
        while (j < s.Length && (s[j] & 0xC0) == 0x80)
        {
            c = (c << 6) + (s[j] & 0x3F);
            j++;
        }

        len = j - i;
        if (c < 0x80 || (c & unchecked((int)0xFFFFF800)) == 0xD800 || (c & unchecked((int)0xFFFFFFFE)) == 0xFFFE || n == 0)
        {
            c = 0xFFFD;
        }

        return c;
    }

    /// <summary>Numeric view of an argument as the math functions and sign() take it: numbers as they are, text only if it reads wholly as a number, anything else none.</summary>
    internal static StorageClass NumericArg(in Mem m, out long iv, out double dv)
    {
        iv = 0;
        dv = 0;
        switch (m.Kind)
        {
            case StorageClass.Integral:
                iv = m.IntValue;
                return StorageClass.Integral;
            case StorageClass.Real:
                dv = m.RealValue;
                return StorageClass.Real;
            case StorageClass.Text:
            {
                StorageClass k = Values.ParseNumber(m.Bytes, out iv, out dv, out bool whole, out bool nulNext);
                if (k is not (StorageClass.Integral or StorageClass.Real) || !(whole || nulNext))
                {
                    return StorageClass.Null;
                }

                // Observed: a number followed by a NUL byte counts as a whole number (the text is read as a C string). It is read
                // through a real: an integer outside -2^51 <= x < 2^51 comes back as the nearest real (floor of '...993' || NUL is ...992.0).
                if (k == StorageClass.Integral && nulNext && !whole && (iv < -2251799813685248L || iv >= 2251799813685248L))
                {
                    dv = iv;
                    return StorageClass.Real;
                }

                return k;
            }

            default:
                return StorageClass.Null;
        }
    }

    private static void CopyValue(in Mem src, ref Mem dst, ref byte[]? own)
    {
        switch (src.Kind)
        {
            case StorageClass.Text:
            case StorageClass.Blob:
                Values.SetBytes(ref dst, ref own, src.Kind, src.Bytes);
                break;
            case StorageClass.Integral:
                Values.SetInt(ref dst, src.IntValue);
                break;
            case StorageClass.Real:
                Values.SetReal(ref dst, src.RealValue);
                break;
            default:
                Values.SetNull(ref dst);
                break;
        }

        dst.Subtype = src.Subtype;
    }

    private static bool AnyNull(ReadOnlySpan<Mem> args)
    {
        foreach (Mem a in args)
        {
            if (a.Kind == StorageClass.Null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Functions added after the first slice. <paramref name="flags"/> is the instruction's P5: the collation of a comparing function.</summary>
    internal static void CallExtra(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own, TextEncoding dbEnc, int flags)
    {
        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        switch (id)
        {
            case FuncId.Char:
            {
                var sk = new Sink(ref own, args.Length * 2);
                foreach (Mem a in args)
                {
                    long v = Values.ToInt(a);
                    sk.PutCodePoint(v < 0 || v > 0x10FFFF ? 0xFFFD : (int)v);
                }

                sk.Finish(ref dst, ref own, StorageClass.Text);
                return;
            }

            case FuncId.Concat:
            {
                var sk = new Sink(ref own, 32);
                foreach (Mem a in args)
                {
                    if (a.Kind != StorageClass.Null)
                    {
                        sk.Put(Values.AsText(a, tmp));
                    }
                }

                sk.Finish(ref dst, ref own, StorageClass.Text);
                return;
            }

            case FuncId.ConcatWs:
            {
                if (args[0].Kind == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                Span<byte> tsep = stackalloc byte[Values.MaxRealText];
                ReadOnlySpan<byte> sep = Values.AsText(args[0], tsep);
                var sk = new Sink(ref own, 32);
                bool first = true;
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i].Kind == StorageClass.Null)
                    {
                        continue;
                    }

                    if (!first)
                    {
                        sk.Put(sep);
                    }

                    first = false;
                    sk.Put(Values.AsText(args[i], tmp));
                }

                sk.Finish(ref dst, ref own, StorageClass.Text);
                return;
            }

            case FuncId.Instr:
                Instr(args, ref dst);
                return;
            case FuncId.Changes:
                Values.SetInt(ref dst, Connection?.Changes ?? 0);
                return;
            case FuncId.TotalChanges:
                Values.SetInt(ref dst, Connection?.TotalChanges ?? 0);
                return;
            case FuncId.LastInsertRowid:
                Values.SetInt(ref dst, Connection?.LastInsertRowid ?? 0);
                return;
            case FuncId.Likelihood:
            case FuncId.Likely:
            case FuncId.Unlikely:
                CopyValue(args[0], ref dst, ref own);
                return;
            case FuncId.Ltrim:
            case FuncId.Rtrim:
            case FuncId.Trim:
                Trim(id, args, ref dst, ref own);
                return;
            case FuncId.MaxN:
            case FuncId.MinN:
            {
                if (AnyNull(args))
                {
                    Values.SetNull(ref dst);
                    return;
                }

                // Among equal values max keeps the first and min takes the last (observed: min(0, 0.0) is 0.0).
                int best = 0;
                for (int i = 1; i < args.Length; i++)
                {
                    int c = Values.Compare(args[best], args[i], (CollKind)flags, dbEnc);
                    if (id == FuncId.MaxN ? c < 0 : c >= 0)
                    {
                        best = i;
                    }
                }

                CopyValue(args[best], ref dst, ref own);
                return;
            }

            case FuncId.Nullif:
                if (Values.Compare(args[0], args[1], (CollKind)flags, dbEnc) == 0)
                {
                    Values.SetNull(ref dst);
                }
                else
                {
                    CopyValue(args[0], ref dst, ref own);
                }

                return;
            case FuncId.OctetLength:
            {
                ref readonly Mem a = ref args[0];
                if (a.Kind == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                if (a.Kind == StorageClass.Blob || dbEnc == TextEncoding.Utf8)
                {
                    Values.SetInt(ref dst, a.Kind == StorageClass.Blob ? a.Length : Values.AsText(a, tmp).Length);
                    return;
                }

                // Text is counted in the database encoding (lang_corefunc octet_length).
                ReadOnlySpan<byte> t = Values.AsText(a, tmp);
                long units = 0;
                for (int i = 0; i < t.Length;)
                {
                    int cp = ReadCodePoint(t, i, out int l);
                    i += l;
                    units += cp >= 0x10000 ? 2 : 1;
                }

                Values.SetInt(ref dst, units * 2);
                return;
            }

            case FuncId.Random:
                Values.SetInt(ref dst, RandomInt64());
                return;
            case FuncId.Randomblob:
            {
                if (args[0].Kind == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                long n = Values.ToInt(args[0]);
                if (n < 1)
                {
                    n = 1;
                }

                if (n > Values.MaxLength)
                {
                    throw TooBig();
                }

                var sk = new Sink(ref own, (int)n);
                sk.Put(0, (int)n);
                RandomNumberGenerator.Fill(sk.Written);
                sk.Finish(ref dst, ref own, StorageClass.Blob);
                return;
            }

            case FuncId.Replace:
                Replace(args, ref dst, ref own);
                return;
            case FuncId.Round:
                Round(args, ref dst);
                return;
            case FuncId.Sign:
            {
                StorageClass k = NumericArg(args[0], out long iv, out double dv);
                if (k == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                }
                else if (k == StorageClass.Integral)
                {
                    Values.SetInt(ref dst, iv < 0 ? -1 : iv > 0 ? 1 : 0);
                }
                else
                {
                    Values.SetInt(ref dst, dv < 0 ? -1 : dv > 0 ? 1 : 0);
                }

                return;
            }

            case FuncId.CompileOptionGet:
            {
                long n = Values.ToInt(args[0]);
                if (args[0].Kind == StorageClass.Null || n < 0 || n >= s_compileOptions.Length)
                {
                    Values.SetNull(ref dst);
                }
                else
                {
                    Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.ASCII.GetBytes(s_compileOptions[n]));
                }

                return;
            }

            case FuncId.CompileOptionUsed:
            {
                if (args[0].Kind == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                string name = Encoding.UTF8.GetString(Values.AsText(args[0], tmp));
                if (name.StartsWith("SQLITE_", StringComparison.OrdinalIgnoreCase))
                {
                    name = name[7..];
                }

                bool used = false;
                foreach (string o in s_compileOptions)
                {
                    if (string.Equals(o, name, StringComparison.OrdinalIgnoreCase)
                        || (!name.Contains('=', StringComparison.Ordinal) && o.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)))
                    {
                        used = true;
                        break;
                    }
                }

                Values.SetInt(ref dst, used ? 1 : 0);
                return;
            }

            case FuncId.SourceId:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.ASCII.GetBytes(SqliteSourceId));
                return;
            case FuncId.RtreeCheck:
            case FuncId.RtreeDepth:
            case FuncId.RtreeNode:
                Extensions.RTree.RTreeFunctions.Call(id, args, ref dst, ref own, Connection);
                return;
            case FuncId.Subtype:
                Values.SetInt(ref dst, args[0].Kind == StorageClass.Null ? 0 : args[0].Subtype);
                return;
            case >= FuncId.VecF32 and <= FuncId.VecDebug:
                Extensions.Vec.VecFunctions.Call(id, args, ref dst, ref own);
                return;
            case FuncId.MatchContext:
                throw new OneLiteException(ResultCode.Error, "unable to use function MATCH in the requested context");
            case FuncId.Fts3Offsets:
            case FuncId.Fts3Matchinfo:
            case FuncId.Fts3Optimize:
                Extensions.Fts3.Fts3Aux.Call(id, args, ref dst, ref own);
                return;
            case FuncId.Fts5Snippet when Extensions.Fts3.Fts3Aux.Handles(args):
                Extensions.Fts3.Fts3Aux.Call(id, args, ref dst, ref own);
                return;
            case FuncId.Fts5Snippet when args.Length != 6:
                throw new OneLiteException(ResultCode.Error, "wrong number of arguments to function snippet()");
            case FuncId.Fts5Bm25:
            case FuncId.Fts5Highlight:
            case FuncId.Fts5Snippet:
            case FuncId.Fts5SourceId:
                Extensions.Fts5.Fts5Aux.Call(id, args, ref dst, ref own);
                return;
            case FuncId.Version:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.ASCII.GetBytes(SqliteVersion));
                return;
            case FuncId.Unhex:
                Unhex(args, ref dst, ref own);
                return;
            case FuncId.Unicode:
            {
                ref readonly Mem a = ref args[0];
                if (a.Kind == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                ReadOnlySpan<byte> t = Values.AsText(a, tmp);
                if (t.Length == 0 || t[0] == 0) // the reference reads the text as a C string, so a leading NUL is an empty string
                {
                    Values.SetNull(ref dst);
                    return;
                }

                Values.SetInt(ref dst, ReadCodePoint(t, 0, out _));
                return;
            }

            case FuncId.Unistr:
                Unistr(args[0], ref dst, ref own, false);
                return;
            case FuncId.UnistrQuote:
                Unistr(args[0], ref dst, ref own, true);
                return;
            case FuncId.Zeroblob:
            {
                long n = Values.ToInt(args[0]);
                if (n < 0 || args[0].Kind == StorageClass.Null)
                {
                    n = 0;
                }

                if (n > Values.MaxLength)
                {
                    throw TooBig();
                }

                var sk = new Sink(ref own, (int)n);
                sk.Put(0, (int)n);
                sk.Finish(ref dst, ref own, StorageClass.Blob);
                return;
            }

            case FuncId.LoadExtension:
                throw new OneLiteException(ResultCode.Error, "not authorized");
            case FuncId.Format:
                Printf.Format(args, ref dst, ref own);
                return;
            case >= FuncId.Acos and <= FuncId.Trunc:
                MathFunc(id, args, ref dst);
                return;
            case >= FuncId.Json and <= FuncId.JsonValid:
                JsonFunctions.Call(id, args, ref dst, ref own);
                return;
            case >= FuncId.Date and <= FuncId.TimeDiff:
                DateFuncs.Call(id, args, ref dst, ref own);
                return;
            default:
                throw new InvalidOperationException("function is compiled to jumps or operators");
        }
    }

    private static long RandomInt64()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        long v = BitConverter.ToInt64(b);
        return v == long.MinValue ? long.MaxValue : v;
    }

    // ------------------------------------------------------------------ text functions

    private static void Instr(ReadOnlySpan<Mem> args, ref Mem dst)
    {
        if (args[0].Kind == StorageClass.Null || args[1].Kind == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        Span<byte> t1 = stackalloc byte[Values.MaxRealText];
        Span<byte> t2 = stackalloc byte[Values.MaxRealText];
        bool blobs = args[0].Kind == StorageClass.Blob && args[1].Kind == StorageClass.Blob;
        ReadOnlySpan<byte> hay = blobs ? args[0].Bytes : Values.AsText(args[0], t1);
        ReadOnlySpan<byte> needle = blobs ? args[1].Bytes : Values.AsText(args[1], t2);
        long pos = 1;
        if (needle.Length > 0)
        {
            int at = hay.IndexOf(needle);
            if (at < 0)
            {
                pos = 0;
            }
            else if (blobs)
            {
                pos = at + 1;
            }
            else
            {
                pos = 1;
                for (int i = 0; i < at; i++)
                {
                    if ((hay[i] & 0xC0) != 0x80)
                    {
                        pos++;
                    }
                }
            }
        }

        Values.SetInt(ref dst, pos);
    }

    private static void Trim(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        if (args[0].Kind == StorageClass.Null || (args.Length > 1 && args[1].Kind == StorageClass.Null))
        {
            Values.SetNull(ref dst);
            return;
        }

        Span<byte> t1 = stackalloc byte[Values.MaxRealText];
        Span<byte> t2 = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> x = Values.AsText(args[0], t1);
        int start = 0, end = x.Length;
        if (args.Length == 1)
        {
            if (id != FuncId.Rtrim)
            {
                while (start < end && x[start] == (byte)' ')
                {
                    start++;
                }
            }

            if (id != FuncId.Ltrim)
            {
                while (end > start && x[end - 1] == (byte)' ')
                {
                    end--;
                }
            }
        }
        else
        {
            ReadOnlySpan<byte> set = Values.AsText(args[1], t2);
            // Observed: the set of characters ends at its first NUL byte (ltrim('abc', 'ab' || char(0) || 'cd') is 'c').
            int nul = set.IndexOf((byte)0);
            if (nul >= 0)
            {
                set = set[..nul];
            }

            if (set.Length > 0)
            {
                if (id != FuncId.Rtrim)
                {
                    while (start < end)
                    {
                        int l = CharLenAt(x, start);
                        if (!InSet(set, x.Slice(start, Math.Min(l, end - start))))
                        {
                            break;
                        }

                        start += l;
                    }
                }

                if (id != FuncId.Ltrim)
                {
                    while (end > start)
                    {
                        int b = end - 1;
                        while (b > start && (x[b] & 0xC0) == 0x80)
                        {
                            b--;
                        }

                        if (!InSet(set, x[b..end]))
                        {
                            break;
                        }

                        end = b;
                    }
                }
            }
        }

        Values.SetBytes(ref dst, ref own, StorageClass.Text, x[start..Math.Max(start, end)]);
    }

    private static int CharLenAt(ReadOnlySpan<byte> s, int i)
    {
        int j = i + 1;
        if (s[i] >= 0xC0)
        {
            while (j < s.Length && (s[j] & 0xC0) == 0x80)
            {
                j++;
            }
        }

        return j - i;
    }

    private static bool InSet(ReadOnlySpan<byte> set, ReadOnlySpan<byte> ch)
    {
        for (int i = 0; i < set.Length;)
        {
            int l = CharLenAt(set, i);
            if (l == ch.Length && set.Slice(i, l).SequenceEqual(ch))
            {
                return true;
            }

            i += l;
        }

        return false;
    }

    private static void Replace(ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        if (args[0].Kind == StorageClass.Null || args[1].Kind == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        Span<byte> t1 = stackalloc byte[Values.MaxRealText];
        Span<byte> t2 = stackalloc byte[Values.MaxRealText];
        Span<byte> t3 = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> x = Values.AsText(args[0], t1);
        ReadOnlySpan<byte> pat = Values.AsText(args[1], t2);
        // Observed: a pattern that starts with a NUL byte (a blob x'00..') reads as empty, before the replacement is looked at.
        if (pat.Length == 0 || pat[0] == 0)
        {
            // Observed: the pattern is empty when its first byte is NUL, so a blob x'00' leaves the string alone, ahead of the NULL replacement check.
            Values.SetBytes(ref dst, ref own, StorageClass.Text, x);
            return;
        }

        if (args[2].Kind == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        ReadOnlySpan<byte> rep = Values.AsText(args[2], t3);
        var sk = new Sink(ref own, x.Length);
        int i = 0;
        while (i < x.Length)
        {
            int at = x[i..].IndexOf(pat);
            if (at < 0)
            {
                break;
            }

            sk.Put(x.Slice(i, at));
            sk.Put(rep);
            i += at + pat.Length;
        }

        sk.Put(x[i..]);
        sk.Finish(ref dst, ref own, StorageClass.Text);
    }

    private static void Round(ReadOnlySpan<Mem> args, ref Mem dst)
    {
        if (args[0].Kind == StorageClass.Null || (args.Length > 1 && args[1].Kind == StorageClass.Null))
        {
            Values.SetNull(ref dst);
            return;
        }

        long n = 0;
        if (args.Length > 1)
        {
            n = Values.ToInt(args[1]);
            n = n > 30 ? 30 : n < 0 ? 0 : n;
        }

        double r = Values.ToReal(args[0]);
        // Numbers this large have no fraction; the reference returns them untouched.
        if (double.IsNaN(r) || double.IsInfinity(r) || Math.Abs(r) >= 4503599627370496.0)
        {
            Values.SetReal(ref dst, r);
            return;
        }

        // lang_corefunc round(): the value is printed with n decimals, rounding the exact decimal expansion half up (away from zero),
        // and the text is read back. The BCL's own fixed format breaks exact ties to even, so the rounding is done here on the digits.
        string full = Math.Abs(r).ToString("F" + (n + 25), System.Globalization.CultureInfo.InvariantCulture);
        int dot = full.IndexOf('.', StringComparison.Ordinal);
        char[] kept = full.AsSpan(0, dot + 1 + (int)n).ToString().ToCharArray();
        if (full[dot + 1 + (int)n] >= '5')
        {
            int i = kept.Length - 1;
            for (; i >= 0; i--)
            {
                if (kept[i] == '.') continue;
                if (kept[i] == '9') { kept[i] = '0'; continue; }
                kept[i]++;
                break;
            }

            string t = i < 0 ? "1" + new string(kept) : new string(kept);
            kept = t.ToCharArray();
        }

        double.TryParse(kept, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double res);
        // Observed: with no decimals the result passes through an integer, so a negative value that rounds to zero is +0.0; with decimals it is -0.0.
        Values.SetReal(ref dst, r < 0 && !(n == 0 && res == 0) ? -res : res);
    }

    private static int SoundexCode(byte c)
    {
        switch ((char)(c | 0x20))
        {
            case 'b': case 'f': case 'p': case 'v': return 1;
            case 'c': case 'g': case 'j': case 'k': case 'q': case 's': case 'x': case 'z': return 2;
            case 'd': case 't': return 3;
            case 'l': return 4;
            case 'm': case 'n': return 5;
            case 'r': return 6;
            default: return 0;
        }
    }

    private static void Soundex(in Mem a, ref Mem dst, ref byte[]? own)
    {
        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> s = a.Kind == StorageClass.Null ? default : Values.AsText(a, tmp);
        int i = 0;
        while (i < s.Length && !((s[i] | 0x20) is >= (byte)'a' and <= (byte)'z'))
        {
            i++;
        }

        if (i >= s.Length)
        {
            Values.SetBytes(ref dst, ref own, StorageClass.Text, "?000"u8);
            return;
        }

        Span<byte> o = stackalloc byte[4];
        int prev = SoundexCode(s[i]);
        o[0] = (byte)(s[i] & ~0x20);
        int j = 1;
        for (; j < 4 && i < s.Length; i++)
        {
            int code = (s[i] | 0x20) is >= (byte)'a' and <= (byte)'z' ? SoundexCode(s[i]) : 0;
            if (code > 0)
            {
                if (code != prev)
                {
                    prev = code;
                    o[j++] = (byte)('0' + code);
                }
            }
            else
            {
                prev = 0;
            }
        }

        while (j < 4)
        {
            o[j++] = (byte)'0';
        }

        Values.SetBytes(ref dst, ref own, StorageClass.Text, o);
    }

    private static int HexVal(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    private static void Unhex(ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        if (AnyNull(args))
        {
            Values.SetNull(ref dst);
            return;
        }

        Span<byte> t1 = stackalloc byte[Values.MaxRealText];
        Span<byte> t2 = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> x = Values.AsText(args[0], t1);
        ReadOnlySpan<byte> skip = args.Length > 1 ? Values.AsText(args[1], t2) : default;
        // unhex reads both arguments as C strings: a NUL byte ends them (observed with blob arguments).
        int nul = x.IndexOf((byte)0);
        if (nul >= 0)
        {
            x = x[..nul];
        }

        nul = skip.IndexOf((byte)0);
        if (nul >= 0)
        {
            skip = skip[..nul];
        }

        var sk = new Sink(ref own, x.Length / 2);
        int i = 0;
        while (i < x.Length)
        {
            // Characters of the second argument may stand before, after and between pairs, never inside one.
            int l = CharLenAt(x, i);
            if (HexVal(x[i]) < 0)
            {
                if (InSet(skip, x.Slice(i, l)))
                {
                    i += l;
                    continue;
                }

                Values.SetNull(ref dst);
                return;
            }

            if (i + 1 >= x.Length || HexVal(x[i + 1]) < 0)
            {
                Values.SetNull(ref dst);
                return;
            }

            sk.Put((byte)((HexVal(x[i]) << 4) | HexVal(x[i + 1])));
            i += 2;
        }

        sk.Finish(ref dst, ref own, StorageClass.Blob);
    }

    private static void Unistr(in Mem a, ref Mem dst, ref byte[]? own, bool quote)
    {
        if (a.Kind == StorageClass.Null)
        {
            if (quote)
            {
                Values.SetBytes(ref dst, ref own, StorageClass.Text, "NULL"u8);
            }
            else
            {
                Values.SetNull(ref dst);
            }

            return;
        }

        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> x = Values.AsText(a, tmp);
        if (quote)
        {
            // unistr_quote(X): the SQL literal for X, with control characters and backslashes written as escapes.
            var q = new Sink(ref own, x.Length + 2);
            bool esc = false;
            foreach (byte c in x)
            {
                if (c < 0x20 || c == (byte)'\\' || c == 0x7F)
                {
                    esc = true;
                }
            }

            if (esc)
            {
                q.PutAscii("unistr(");
            }

            q.Put((byte)'\'');
            for (int i = 0; i < x.Length; i++)
            {
                byte c = x[i];
                if (c == (byte)'\'')
                {
                    q.Put(c);
                    q.Put(c);
                }
                else if (c == (byte)'\\')
                {
                    q.PutAscii("\\\\");
                }
                else if (c < 0x20 || c == 0x7F)
                {
                    q.PutAscii("\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    q.Put(c);
                }
            }

            q.Put((byte)'\'');
            if (esc)
            {
                q.Put((byte)')');
            }

            q.Finish(ref dst, ref own, StorageClass.Text);
            return;
        }

        var sk = new Sink(ref own, x.Length);
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] != (byte)'\\')
            {
                sk.Put(x[i]);
                continue;
            }

            if (i + 1 < x.Length && x[i + 1] == (byte)'\\')
            {
                sk.Put((byte)'\\');
                i++;
                continue;
            }

            int digits;
            int skipAhead;
            if (i + 1 < x.Length && x[i + 1] == (byte)'u')
            {
                digits = 4;
                skipAhead = 2;
            }
            else if (i + 1 < x.Length && x[i + 1] == (byte)'U')
            {
                digits = 8;
                skipAhead = 2;
            }
            else if (i + 1 < x.Length && x[i + 1] == (byte)'+')
            {
                digits = 6;
                skipAhead = 2;
            }
            else
            {
                digits = 4;
                skipAhead = 1;
            }

            long cp = 0;
            if (i + skipAhead + digits > x.Length)
            {
                throw new OneLiteException(ResultCode.Error, "invalid Unicode escape");
            }

            for (int k = 0; k < digits; k++)
            {
                int h = HexVal(x[i + skipAhead + k]);
                if (h < 0)
                {
                    throw new OneLiteException(ResultCode.Error, "invalid Unicode escape");
                }

                cp = (cp << 4) | (uint)h;
            }

            if (cp > 0x10FFFF)
            {
                throw new OneLiteException(ResultCode.Error, "invalid Unicode escape");
            }

            sk.PutCodePoint((int)cp);
            i += skipAhead + digits - 1;
        }

        sk.Finish(ref dst, ref own, StorageClass.Text);
    }
}
