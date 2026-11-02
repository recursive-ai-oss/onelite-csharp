using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// A transient table of records: the rows of a materialized subquery or CTE, of a compound select's intermediate result, or the
/// queue of a recursive CTE (lang_with "Recursive Common Table Expressions"). Rows are read back in insertion order.
/// </summary>
internal sealed class EphTable
{
    internal List<byte[]?> Rows = [];
    internal int Pos;
    internal bool NullRow;
    internal bool Queue;

    internal void Clear()
    {
        Rows = [];
        Pos = 0;
        NullRow = false;
    }
}

/// <summary>What a set compares: one collation per column. NULLs compare equal to each other here (DISTINCT, UNION, GROUP BY), unlike in <c>=</c>.</summary>
internal sealed class SetSpec
{
    internal CollKind[] Collations = [];
}

/// <summary>
/// An ordered set of records under per-column collations. DISTINCT, UNION, INTERSECT, EXCEPT and IN (SELECT ...) use it; a compound
/// select reads a set back in key order, which is the order the reference returns UNION results in.
/// </summary>
internal sealed class SetStore
{
    private readonly RecordView _a = new(), _b = new();
    private readonly CollKind[] _colls;
    private readonly TextEncoding _enc;
    internal SortedSet<byte[]> Items;
    internal bool HasNull;
    internal bool HasNonNull;
    private readonly Mem[] _probe;

    internal SetStore(SetSpec spec, TextEncoding enc)
    {
        _colls = spec.Collations;
        _enc = enc;
        _probe = new Mem[Math.Max(_colls.Length, 1)];
        Items = new SortedSet<byte[]>(Comparer<byte[]>.Create(Compare));
    }

    private int Compare(byte[]? x, byte[]? y)
    {
        _a.Parse(x!, 0, x!.Length);
        _b.Parse(y!, 0, y!.Length);
        for (int i = 0; i < _colls.Length; i++)
        {
            Mem ma = default, mb = default;
            _a.Load(i, ref ma);
            _b.Load(i, ref mb);
            int c = Values.Compare(ma, mb, _colls[i], _enc);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    internal CollKind Collation(int i) => _colls[i];

    internal bool Add(ReadOnlySpan<Mem> row, bool replace = false)
    {
        if (row.Length > 0 && row[0].Kind == StorageClass.Null)
        {
            HasNull = true;
        }
        else if (row.Length > 0)
        {
            HasNonNull = true;
        }

        byte[] rec = RecordWriter.ToArray(row);
        if (Items.Add(rec))
        {
            if (replace)
            {
                (_armRows ??= new SortedSet<byte[]>(Items.Comparer)).Add(rec);
            }

            return true;
        }

        // Observed: a row of the right-hand select replaces an equal row of the left side (UNION keeps the later one), but among the rows of
        // that one select the first of equal rows stays.
        if (replace && !(_armRows?.Contains(rec) ?? false) && Items.TryGetValue(rec, out byte[]? existing))
        {
            Items.Remove(existing);
            Items.Add(rec);
            (_armRows ??= new SortedSet<byte[]>(Items.Comparer)).Add(rec);
        }

        return false;
    }

    private SortedSet<byte[]>? _armRows;

    internal bool Contains(ReadOnlySpan<Mem> row) => Items.Contains(RecordWriter.ToArray(row));
}
