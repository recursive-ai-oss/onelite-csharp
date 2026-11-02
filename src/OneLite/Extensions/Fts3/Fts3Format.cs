using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>
/// The varints, segment nodes and doclists of the fts3 index (sqlite.org/fts3.html, "Data Structures"). The varint here is the fts3 one:
/// seven bits per byte, least significant first, high bit set on all but the last byte; negative numbers take ten bytes.
/// </summary>
internal static class F3
{
    /// <summary>Segment b-tree nodes are filled up to about this many bytes before the next one is started.</summary>
    internal const int NodeSize = 4000;

    /// <summary>A level holds up to this many segments; the next insert merges them into one at the next level.</summary>
    internal const int MergeCount = 16;

    /// <summary>Levels of one index are numbered below this; each (language, index) pair has its own range.</summary>
    internal const int LevelsPerIndex = 1024;

    internal static OneLiteException Corrupt(string what) => new(ResultCode.CorruptVtab, "database disk image is malformed", what);

    internal static void PutVarint(List<byte> dst, long v)
    {
        ulong u = (ulong)v;
        do
        {
            byte b = (byte)(u & 0x7f);
            u >>= 7;
            dst.Add(u != 0 ? (byte)(b | 0x80) : b);
        }
        while (u != 0);
    }

    internal static int VarintLength(long v)
    {
        ulong u = (ulong)v;
        int n = 1;
        while ((u >>= 7) != 0)
        {
            n++;
        }

        return n;
    }

    /// <summary>Reads a varint at <paramref name="pos"/>; fails with corruption when it runs past the end or is longer than ten bytes.</summary>
    internal static long GetVarint(ReadOnlySpan<byte> s, ref int pos)
    {
        ulong v = 0;
        int shift = 0;
        for (int i = 0; i < 10; i++)
        {
            if (pos >= s.Length)
            {
                throw Corrupt("fts3: varint runs past the end of its node");
            }

            byte b = s[pos++];
            v |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return (long)v;
            }

            shift += 7;
        }

        throw Corrupt("fts3: varint longer than ten bytes");
    }

    private static int Cmp(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);

    internal static int CompareTerms(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Cmp(a, b);

    // ---------------------------------------------------------------- poslists

    /// <summary>
    /// Reads one position list starting at <paramref name="pos"/> and returns where it ends (after the terminating 0).
    /// A list is the position deltas (each plus 2) of column 0, then for every further column a 1, the column number and its deltas.
    /// </summary>
    internal static int SkipPoslist(ReadOnlySpan<byte> s, int pos)
    {
        for (; ; )
        {
            long v = GetVarint(s, ref pos);
            if (v == 0)
            {
                return pos;
            }

            if (v == 1)
            {
                GetVarint(s, ref pos);
            }
        }
    }

    /// <summary>Appends the (column, position) pairs of the position list at <paramref name="pos"/> to <paramref name="into"/>.</summary>
    internal static void DecodePoslist(ReadOnlySpan<byte> s, int pos, List<(int Col, int Pos)> into)
    {
        int col = 0;
        long p = 0;
        for (; ; )
        {
            long v = GetVarint(s, ref pos);
            if (v == 0)
            {
                return;
            }

            if (v == 1)
            {
                long c = GetVarint(s, ref pos);
                if (c < 0 || c > int.MaxValue)
                {
                    throw Corrupt("fts3: bad column number in a position list");
                }

                col = (int)c;
                p = 0;
                continue;
            }

            p += v - 2;
            if (p < 0 || p > int.MaxValue)
            {
                throw Corrupt("fts3: bad position in a position list");
            }

            into.Add((col, (int)p));
        }
    }

    /// <summary>Encodes sorted (column, position) pairs as a position list including its terminating 0. An empty input encodes the bare 0.</summary>
    internal static void EncodePoslist(List<byte> dst, IReadOnlyList<(int Col, int Pos)> items)
    {
        int col = 0;
        long prev = 0;
        foreach ((int c, int p) in items)
        {
            if (c != col)
            {
                PutVarint(dst, 1);
                PutVarint(dst, c);
                col = c;
                prev = 0;
            }

            PutVarint(dst, p - prev + 2);
            prev = p;
        }

        dst.Add(0);
    }

    /// <summary>One entry of a doclist: a docid and the raw position list (with its terminator); an entry with only the terminator marks a deleted document.</summary>
    internal readonly record struct Entry(long Docid, byte[] Poslist)
    {
        internal bool IsDelete => Poslist.Length == 1;
    }

    /// <summary>Splits a doclist into entries. <paramref name="descending"/> is the table's <c>order=desc</c>: docid deltas are then subtracted.</summary>
    internal static List<Entry> ParseDoclist(ReadOnlySpan<byte> s, bool descending)
    {
        var l = new List<Entry>();
        int pos = 0;
        long docid = 0;
        bool first = true;
        while (pos < s.Length)
        {
            long d = GetVarint(s, ref pos);
            if (first)
            {
                docid = d;
                first = false;
            }
            else
            {
                docid = descending ? docid - d : docid + d;
            }

            int end = SkipPoslist(s, pos);
            l.Add(new Entry(docid, s[pos..end].ToArray()));
            pos = end;
        }

        return l;
    }

    internal static byte[] BuildDoclist(IReadOnlyList<Entry> entries, bool descending)
    {
        var b = new List<byte>();
        long prev = 0;
        bool first = true;
        foreach (Entry e in entries)
        {
            PutVarint(b, first ? e.Docid : descending ? prev - e.Docid : e.Docid - prev);
            first = false;
            prev = e.Docid;
            b.AddRange(e.Poslist);
        }

        return [.. b];
    }

    // ---------------------------------------------------------------- nodes

    /// <summary>A parsed leaf node: its terms with their doclists.</summary>
    internal static List<(byte[] Term, byte[] Doclist)> ParseLeaf(byte[] node)
    {
        var l = new List<(byte[], byte[])>();
        ReadOnlySpan<byte> s = node;
        int pos = 0;
        long height = GetVarint(s, ref pos);
        if (height != 0)
        {
            throw Corrupt("fts3: expected a leaf node");
        }

        byte[] term = [];
        bool first = true;
        while (pos < s.Length)
        {
            long nPrefix = 0;
            if (!first)
            {
                nPrefix = GetVarint(s, ref pos);
            }

            long nSuffix = GetVarint(s, ref pos);
            if (nPrefix < 0 || nSuffix < 0 || nPrefix > term.Length || nSuffix > s.Length - pos)
            {
                throw Corrupt("fts3: bad term length in a leaf node");
            }

            byte[] t = new byte[nPrefix + nSuffix];
            term.AsSpan(0, (int)nPrefix).CopyTo(t);
            s.Slice(pos, (int)nSuffix).CopyTo(t.AsSpan((int)nPrefix));
            pos += (int)nSuffix;
            long nDoc = GetVarint(s, ref pos);
            if (nDoc < 0 || nDoc > s.Length - pos)
            {
                throw Corrupt("fts3: bad doclist length in a leaf node");
            }

            l.Add((t, s.Slice(pos, (int)nDoc).ToArray()));
            pos += (int)nDoc;
            term = t;
            first = false;
        }

        return l;
    }

    /// <summary>A parsed interior node: height, leftmost child and the key that starts each further child.</summary>
    internal static (int Height, long Left, List<byte[]> Keys) ParseInterior(byte[] node)
    {
        ReadOnlySpan<byte> s = node;
        int pos = 0;
        long h = GetVarint(s, ref pos);
        if (h < 1 || h > 64)
        {
            throw Corrupt("fts3: bad height in an interior node");
        }

        long left = GetVarint(s, ref pos);
        var keys = new List<byte[]>();
        byte[] term = [];
        bool first = true;
        while (pos < s.Length)
        {
            long nPrefix = 0;
            if (!first)
            {
                nPrefix = GetVarint(s, ref pos);
            }

            long nSuffix = GetVarint(s, ref pos);
            if (nPrefix < 0 || nSuffix < 0 || nPrefix > term.Length || nSuffix > s.Length - pos)
            {
                throw Corrupt("fts3: bad key length in an interior node");
            }

            byte[] t = new byte[nPrefix + nSuffix];
            term.AsSpan(0, (int)nPrefix).CopyTo(t);
            s.Slice(pos, (int)nSuffix).CopyTo(t.AsSpan((int)nPrefix));
            pos += (int)nSuffix;
            keys.Add(t);
            term = t;
            first = false;
        }

        return ((int)h, left, keys);
    }

    internal static int CommonPrefix(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    internal static long ReadInt64(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadInt64LittleEndian(s);
}
