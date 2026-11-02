namespace RecursiveAI.OneLite.Vm;

/// <summary>A growable byte buffer the JSON functions build JSONB and text into. Instances are reused, so a cached statement allocates only its result.</summary>
internal sealed class JsonBuf
{
    internal byte[] A = new byte[256];
    internal int Len;

    internal ReadOnlySpan<byte> Span => new(A, 0, Len);

    internal void Clear() => Len = 0;

    private void Grow(int extra)
    {
        long need = (long)Len + extra;
        if (need > SerialLimit)
        {
            throw new OneLiteException(ResultCode.TooBig, "string or blob too big");
        }

        if (need > A.Length)
        {
            Array.Resize(ref A, (int)Math.Min(Math.Max(need, (long)A.Length * 2), SerialLimit));
        }
    }

    private const int SerialLimit = 1_000_000_000;

    internal void Put(byte b)
    {
        if (Len == A.Length)
        {
            Grow(1);
        }

        A[Len++] = b;
    }

    internal void Put(ReadOnlySpan<byte> s)
    {
        if (Len + s.Length > A.Length)
        {
            Grow(s.Length);
        }

        s.CopyTo(A.AsSpan(Len));
        Len += s.Length;
    }

    internal void Put(string ascii)
    {
        foreach (char c in ascii)
        {
            Put((byte)c);
        }
    }

    /// <summary>Opens a gap of <paramref name="n"/> bytes at <paramref name="at"/> by moving the tail up.</summary>
    internal void Open(int at, int n)
    {
        Grow(n);
        Array.Copy(A, at, A, at + n, Len - at);
        Len += n;
    }

    internal void PutUtf8(int cp)
    {
        if (cp < 0x80)
        {
            Put((byte)cp);
        }
        else if (cp < 0x800)
        {
            Put((byte)(0xC0 | (cp >> 6)));
            Put((byte)(0x80 | (cp & 0x3F)));
        }
        else if (cp < 0x10000)
        {
            Put((byte)(0xE0 | (cp >> 12)));
            Put((byte)(0x80 | ((cp >> 6) & 0x3F)));
            Put((byte)(0x80 | (cp & 0x3F)));
        }
        else
        {
            Put((byte)(0xF0 | (cp >> 18)));
            Put((byte)(0x80 | ((cp >> 12) & 0x3F)));
            Put((byte)(0x80 | ((cp >> 6) & 0x3F)));
            Put((byte)(0x80 | (cp & 0x3F)));
        }
    }
}

/// <summary>Per-thread scratch buffers. A JSON function uses them from entry to return, never across a call into another function.</summary>
internal static class JsonScratch
{
    [ThreadStatic] private static JsonBuf[]? t_bufs;

    internal static JsonBuf Get(int i)
    {
        t_bufs ??= [new JsonBuf(), new JsonBuf(), new JsonBuf(), new JsonBuf(), new JsonBuf(), new JsonBuf(), new JsonBuf(), new JsonBuf()];
        JsonBuf b = t_bufs[i];
        b.Len = 0;
        return b;
    }
}
