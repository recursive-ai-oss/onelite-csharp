using RecursiveAI.OneLite.Storage;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

/// <summary>
/// Turns a parsed SELECT into a <see cref="VmProgram"/>: name resolution against the catalog, expression code generation with
/// SQLite's affinity and collation rules (datatype3 §4, §7), a small planner, and the loop skeleton. Anything outside the supported
/// subset is refused with an "unsupported" error at this point, never at run time.
/// </summary>
internal sealed partial class Compiler
{
    private readonly Catalog _cat;
    private string _sql;
    private readonly TextEncoding _enc;
    private readonly List<Instr> _code = [];
    private readonly List<int> _labels = [];
    private readonly List<CursorSpec> _cursors = [];
    private List<string?> _params = [];
    private Dictionary<int, int>? _anonPos;
    private int _nReg;

    private bool _columnsAllowed;

    /// <summary>The connection whose user-defined aggregates the compile in progress on this thread may call.</summary>
    [ThreadStatic]
    private static Connection? t_owner;

    internal Compiler(Catalog cat, string sql, TextEncoding enc)
    {
        t_owner = cat.Owner;
        _cat = cat;
        _sql = sql;
        _enc = enc;
    }

    /// <summary>A compiler for a nested statement of the same SQL text: it numbers bind parameters in the same list as <paramref name="outer"/>.</summary>
    internal Compiler(Compiler outer)
        : this(outer._cat, outer._sql, outer._enc)
    {
        _params = outer._params;
        _anonPos = outer._anonPos;
        _plan = outer._plan;
    }

    private static OneLiteException Err(string message) => new(ResultCode.Error, message);

    private static OneLiteException Unsupported(string what) => new(ResultCode.Error, "unsupported: " + what);

    // ------------------------------------------------------------------ emission

    private int Emit(Op op, int p1 = 0, int p2 = 0, int p3 = 0, object? p4 = null, int p5 = 0)
    {
        _code.Add(new Instr { Op = op, P1 = p1, P2 = p2, P3 = p3, P4 = p4, P5 = p5 });
        return _code.Count - 1;
    }

    private int NewLabel()
    {
        _labels.Add(-1);
        return -_labels.Count;
    }

    private void Mark(int label) => _labels[-label - 1] = _code.Count;

    private int NewReg() => ++_nReg;

    private int NewRegs(int n)
    {
        int first = _nReg + 1;
        _nReg += n;
        return first;
    }

    private static bool IsJump(Op op) => op is Op.Goto or Op.Rewind or Op.Next or Op.Last or Op.Prev or Op.SeekRowid or Op.SeekGE or Op.SeekGT
        or Op.IdxGT or Op.IdxGE or Op.If or Op.IfNot or Op.IfNull or Op.NotNull or Op.IfPos or Op.DecrJumpZero
        or Op.SorterSort or Op.SorterNext or Op.Gosub or Op.Once or Op.ERewind or Op.ENext or Op.SetFound or Op.WinOut
        or Op.IdxProbe or Op.RowSetRead or Op.RetRow or Op.SubNext or Op.TrigRun or Op.RecTrig or Op.VFilter or Op.VtFilter or Op.VNext or Op.PragmaIntegrity;

    private Instr[] Finish()
    {
        for (int i = 0; i < _code.Count; i++)
        {
            Instr ins = _code[i];
            if (IsJump(ins.Op) && ins.P2 < 0)
            {
                ins.P2 = _labels[-ins.P2 - 1];
                _code[i] = ins;
            }
        }

        return [.. _code];
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => n.ToString(CultureInfo.InvariantCulture) + "th",
    };

    /// <summary>
    /// ORDER BY term resolution (lang_select): an integer literal is a result column position; a bare identifier that names a result
    /// column alias is that column; anything else is an expression over the FROM table. Returns the result column index or -1.
    /// </summary>
    private int ResolveOrderTerm(Expr e, List<(Expr Expr, string Name)> cols, int termIndex)
    {
        // lang_select: an ORDER BY term that is an integer position picks that result column. Black-box (the shell): unary plus,
        // parentheses and COLLATE do not hide the literal; literals above 32 bits are constants; "<literal> ISNULL" counts as 0,
        // "<literal> NOTNULL" as 1, and an AND with a 0 operand as 0 (all raise the range error or pick column 1).
        bool collated = false;
        long? position = ConstPosition(e, ref collated);

        if (position is long n)
        {
            if (n < 1 || n > cols.Count)
            {
                throw Err($"{Ordinal(termIndex + 1)} ORDER BY term out of range - should be between 1 and {cols.Count}");
            }

            return (int)n - 1;
        }

        // A COLLATE (parentheses are invisible) over an alias still names the result column; the collation then comes from the term.
        Expr bare = e;
        while (bare is ParenExpr or CollateExpr)
        {
            bare = bare is ParenExpr pe ? pe.Inner : ((CollateExpr)bare).Operand;
        }

        if (bare is ColumnRef { Schema: null, Table: null } cr)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                if (_aliases.TryGetValue(i, out string? alias) && AsciiComparer.Instance.Equals(alias, cr.Column.Text))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool SignedLiteral(Expr e, out long value)
    {
        bool negative = false;
        while (e is UnaryExpr { Op: UnaryOp.Minus or UnaryOp.Plus } or ParenExpr)
        {
            // Observed: parentheses are invisible, so `-(-2)` folds to the position 2 and `-(+2)` to -2.
            if (e is ParenExpr pe)
            {
                e = pe.Inner;
                continue;
            }

            var u = (UnaryExpr)e;
            negative ^= u.Op == UnaryOp.Minus;
            e = u.Operand;
        }

        value = 0;
        if (e is not LiteralExpr { Kind: LiteralKind.Integer } lit)
        {
            return false;
        }

        Values.ParseIntegerLiteral(lit.Text, out Mem m, out _);
        if (m.Kind != StorageClass.Integral || m.IntValue is < 0 or > int.MaxValue)
        {
            return false;
        }

        value = negative ? -m.IntValue : m.IntValue;
        return true;
    }

    private static long? ConstPosition(Expr e, ref bool collated)
    {
        while (true)
        {
            switch (e)
            {
                case ParenExpr p:
                    e = p.Inner;
                    continue;
                case UnaryExpr { Op: UnaryOp.Plus } up:
                    e = up.Operand;
                    continue;
                case CollateExpr ce:
                    collated = true;
                    e = ce.Operand;
                    continue;
            }

            break;
        }

        switch (e)
        {
            case LiteralExpr { Kind: LiteralKind.Integer } lit:
            {
                Values.ParseIntegerLiteral(lit.Text, out Mem m, out _);
                return m.Kind == StorageClass.Integral && m.IntValue is >= 0 and <= int.MaxValue ? m.IntValue : null;
            }

            case UnaryExpr { Op: UnaryOp.Minus or UnaryOp.Plus } chain when SignedLiteral(chain, out long signed):
                // Observed: signs in front of an integer literal fold into it, parentheses or not (`- -1` and `-(-1)` are the position 1).
                return signed;
            case UnaryExpr { Op: UnaryOp.Minus, Operand: LiteralExpr { Kind: LiteralKind.Integer } nl }:
            {
                bool c = false;
                return ConstPosition(nl, ref c) is long v ? -v - 1 : null;
            }

            case UnaryExpr { Op: UnaryOp.Minus } um when StripSigns(um.Operand) is NullTestExpr or BinaryExpr { Op: BinaryOp.Is or BinaryOp.IsNot }:
            {
                // Observed: -(x ISNULL) of a folded test is folded too (0 or -1), so as a GROUP BY term it is out of range.
                bool c = false;
                return ConstPosition(StripSigns(um.Operand), ref c) is long v ? -v : null;
            }

            case NullTestExpr nt when IsFoldedNonNull(nt.Operand):
                return nt.Test == NullTest.IsNull ? 0 : 1;
            case BinaryExpr { Op: BinaryOp.Is or BinaryOp.IsNot } bi when StripSigns(bi.Right) is LiteralExpr { Kind: LiteralKind.Null } && IsFoldedNonNull(bi.Left):
                return bi.Op == BinaryOp.Is ? 0 : 1;
            case BinaryExpr { Op: BinaryOp.And } ba:
            {
                // An empty "IN ()" is a constant false here too, but only as an operand of AND.
                // Observed: a function call anywhere in the AND (even abs(1)) keeps it from counting as a constant.
                return (IsConstFalse(ba.Left) || IsConstFalse(ba.Right)) && !HasCall(ba.Left) && !HasCall(ba.Right) ? 0 : null;
            }

            default:
                return null;
        }
    }

    // Observed: the reference folds a NULL test of a non-NULL literal, and of another folded test or AND (so (1 ISNULL) IS NULL is the
    // constant 0 and an ORDER BY term), but not of an arithmetic or function expression.
    private static bool IsFoldedNonNull(Expr e)
    {
        Expr s = StripSigns(e);
        if (s is LiteralExpr { Kind: not LiteralKind.Null })
        {
            return true;
        }

        if (s is NullTestExpr or BinaryExpr { Op: BinaryOp.Is or BinaryOp.IsNot or BinaryOp.And })
        {
            bool c = false;
            return ConstPosition(s, ref c) is not null;
        }

        return false;
    }

    internal static bool HasCall(Expr e) => e switch
    {
        FunctionCall => true,
        ParenExpr p => HasCall(p.Inner),
        UnaryExpr u => HasCall(u.Operand),
        BinaryExpr b => HasCall(b.Left) || HasCall(b.Right),
        CollateExpr c => HasCall(c.Operand),
        CastExpr c => HasCall(c.Operand),
        NullTestExpr n => HasCall(n.Operand),
        BetweenExpr b => HasCall(b.Operand) || HasCall(b.Low) || HasCall(b.High),
        InListExpr il => HasCall(il.Operand) || il.Items.Any(HasCall),
        CaseExpr ce => (ce.Operand is not null && HasCall(ce.Operand)) || ce.Whens.Any(w => HasCall(w.When) || HasCall(w.Then)) || (ce.Else is not null && HasCall(ce.Else)),
        LikeExpr => true,   // LIKE and GLOB are calls of like() and glob()
        _ => false,
    };

    private static bool IsConstFalse(Expr e)
    {
        // Observed: as an operand of AND, a sign or a COLLATE in front of the 0 keeps it from counting (NULL AND (+0) is not a constant, NULL AND (0) is).
        Expr core = e;
        while (core is ParenExpr pp)
        {
            core = pp.Inner;
        }

        if (core is UnaryExpr { Op: UnaryOp.Plus or UnaryOp.Minus } or CollateExpr)
        {
            return false;
        }

        bool c = false;
        if (ConstPosition(e, ref c) == 0)
        {
            return true;
        }

        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        return e is InListExpr { Negated: false, Items.IsDefaultOrEmpty: true };
    }

    private static Expr StripSigns(Expr e)
    {
        while (true)
        {
            switch (e)
            {
                case ParenExpr p:
                    e = p.Inner;
                    break;
                case UnaryExpr { Op: UnaryOp.Plus or UnaryOp.Minus } u:
                    e = u.Operand;
                    break;
                default:
                    return e;
            }
        }
    }

    private Dictionary<int, string> _aliases = [];

    /// <summary>Greater than zero while code generation only checks names and types (the code is thrown away).</summary>
    private int _dryDepth;

    /// <summary>Runs code generation and then discards its code, labels, registers and parameter numbers; only the errors it raises matter.</summary>
    private void DryRun(Action gen)
    {
        int code = _code.Count, labels = _labels.Count, regs = _nReg, pars = _params.Count;
        bool col = _columnsAllowed;
        PlanRecorder? savedPlan = _plan;
        _plan = null; // the code of a dry run is thrown away, so it has no place in the plan
        _dryDepth++;
        try
        {
            gen();
        }
        finally
        {
            _plan = savedPlan;
            _dryDepth--;
            _code.RemoveRange(code, _code.Count - code);
            _labels.RemoveRange(labels, _labels.Count - labels);
            _nReg = regs;
            _params.RemoveRange(pars, _params.Count - pars);
            foreach (VariableExpr stale in _anonymous.Where(kv => kv.Value > pars).Select(kv => kv.Key).ToList())
            {
                _anonymous.Remove(stale);
            }

            _columnsAllowed = col;
        }
    }

    private bool _namesForTable;

    private string ResultName(ExprColumn ec)
    {
        if (ec.Alias is { } a)
        {
            return a.Text;
        }

        Expr named = ec.Expr;
        while (named is ParenExpr || (_namesForTable && named is CollateExpr))
        {
            // The columns of a view, CTE or subquery in FROM are named skipping a COLLATE (a query's own result columns are not).
            named = named is ParenExpr np ? np.Inner : ((CollateExpr)named).Operand;
        }

        // The short column name of a bare column reference, parentheses ignored; rowid names the alias column if there is one.
        if (named is ColumnRef cr && TryResolveColumn(cr, out Source src, out int col))
        {
            if (col < 0)
            {
                return src.Table.RowidAlias >= 0 ? src.Table.Columns[src.Table.RowidAlias].Name : cr.Column.Text;
            }

            return src.Table.Columns[col].Name;
        }

        SourceSpan sp = ec.Expr.Span;
        if (sp.End > sp.Start && sp.End <= _sql.Length)
        {
            return _sql.Substring(sp.Start, sp.End - sp.Start).Trim();
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ affinity and collation

    private Affinity AffOf(Expr e)
    {
        switch (e)
        {
            case ParenExpr p:
                return AffOf(p.Inner);
            case CollateExpr c:
                return AffOf(c.Operand);
            case ColumnRef cr:
                if (!TryResolveColumn(cr, out Source rs, out int col))
                {
                    return Affinity.None;
                }

                return col < 0 ? Affinity.Integer : rs.Table.Columns[col].Affinity;
            case CastExpr ce:
                return Values.CastAffinity(string.Join(' ', ce.Type.Words));
            default:
                return Affinity.None;
        }
    }

    /// <summary>The affinity of an operand as comparisons see it: like <see cref="AffOf"/>, but an untyped or BLOB column is told apart from an expression with no affinity.</summary>
    private Affinity CmpAffOf(Expr e)
    {
        Affinity a = AffOf(e);
        if (a != Affinity.None)
        {
            return a;
        }

        for (;;)
        {
            switch (e)
            {
                case ParenExpr p:
                    e = p.Inner;
                    continue;
                case CollateExpr c:
                    e = c.Operand;
                    continue;
                case ColumnRef cr when TryResolveColumn(cr, out Source rs, out int col) && col >= 0 && rs.Kind == SourceKind.Table:
                    return Affinity.Blob;
                default:
                    return Affinity.None;
            }
        }
    }

    /// <summary>The table column a result expression is a plain reference to, for sqlite3_column_database_name, _table_name, _origin_name and _decltype.</summary>
    private ColumnOrigin? OriginOf(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        if (e is not ColumnRef cr || !TryResolveColumn(cr, out Source rs, out int col) || rs.Kind != SourceKind.Table)
        {
            return null;
        }

        string db = rs.Table.Db switch { 0 => "main", 1 => "temp", var slot => _cat.Owner?.AttachedBySlot(slot)?.Name ?? "main" };
        return col < 0
            ? new ColumnOrigin(db, rs.Table.Name, "rowid", "INTEGER")
            : new ColumnOrigin(db, rs.Table.Name, rs.Table.Columns[col].Name, rs.Table.Columns[col].DeclaredType);
    }

    /// <summary>
    /// The type text PRAGMA table_info shows for a view's result column: the declared type of a plain column reference, else the name of
    /// the expression's affinity (INT, TEXT, REAL, NUM, BLOB), else empty.
    /// </summary>
    private string DeclTypeOf(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        if (e is ColumnRef cr && TryResolveColumn(cr, out Source rs, out int col))
        {
            if (col < 0)
            {
                return "INTEGER";
            }

            // Observed: a column with no declared type, or ANY (STRICT), is shown by its affinity's name, BLOB.
            string declared = rs.Table.Columns[col].TypeText;
            if (declared.Length > 0 && !AsciiComparer.Instance.Equals(declared, "ANY"))
            {
                return declared;
            }
        }

        Expr inner = e;
        while (inner is ParenExpr or CollateExpr)
        {
            inner = inner is ParenExpr pp ? pp.Inner : ((CollateExpr)inner).Operand;
        }

        // CAST(x AS BLOB) is the one expression whose affinity is BLOB (the none-affinity of everything else prints as empty).
        if (inner is CastExpr)
        {
            return CmpAffOf(e) switch { Affinity.Text => "TEXT", Affinity.Numeric => "NUM", Affinity.Integer => "INT", Affinity.Real => "REAL", _ => "BLOB" };
        }

        return CmpAffOf(e) switch
        {
            Affinity.Text => "TEXT",
            Affinity.Numeric => "NUM",
            Affinity.Integer => "INT",
            Affinity.Real => "REAL",
            Affinity.Blob => "BLOB",
            _ => "",
        };
    }

    internal static CollKind ParseColl(string name)
    {
        if (AsciiComparer.Instance.Equals(name, "BINARY"))
        {
            return CollKind.Binary;
        }

        if (AsciiComparer.Instance.Equals(name, "NOCASE"))
        {
            return CollKind.NoCase;
        }

        if (AsciiComparer.Instance.Equals(name, "RTRIM"))
        {
            return CollKind.RTrim;
        }

        if (t_owner?.UserCollationSlot(name) is >= 3 and var slot)
        {
            return (CollKind)slot;
        }

        throw new OneLiteException(ResultCode.ErrorMissingCollSeq, "no such collation sequence: " + name);
    }

    /// <summary>The collating sequence of an expression: an explicit COLLATE, else the column's (datatype3 §7.1). <paramref name="explicitColl"/> tells which.</summary>
    private CollKind CollOf(Expr e, out bool explicitColl, out bool any)
    {
        explicitColl = false;
        any = false;
        switch (e)
        {
            case ParenExpr p:
                return CollOf(p.Inner, out explicitColl, out any);
            case CollateExpr c:
                explicitColl = true;
                any = true;
                return ParseColl(c.Collation.Text);
            case UnaryExpr { Op: UnaryOp.Plus } u:
                return CollOf(u.Operand, out explicitColl, out any);
            case CastExpr ce:
                return CollOf(ce.Operand, out explicitColl, out any);
            case ColumnRef cr:
                if (!TryResolveColumn(cr, out Source cs, out int col))
                {
                    return CollKind.Binary;
                }

                // Observed: the rowid, and the column that aliases it, have no collation of their own, so a compound SELECT or a
                // comparison takes the other side's.
                if (col < 0 || col == cs.Table.RowidAlias)
                {
                    return CollKind.Binary;
                }

                any = true;
                return ParseColl(cs.Table.Columns[col].CollationName ?? "BINARY");
            default:
                // An explicit COLLATE shows through the operators and functions around it: the first operand or argument that has one gives the
                // expression its collation. A column's own collation does not travel this way.
                foreach (Expr child in CollationChildren(e))
                {
                    CollKind ck = CollOf(child, out bool ce, out _);
                    if (ce)
                    {
                        explicitColl = true;
                        any = true;
                        return ck;
                    }
                }

                return CollKind.Binary;
        }
    }

    private static IEnumerable<Expr> CollationChildren(Expr e)
    {
        switch (e)
        {
            case FunctionCall f when !f.Args.IsDefaultOrEmpty && f.Over is null:
                return f.Args;
            case UnaryExpr u:
                return [u.Operand];
            case BinaryExpr b:
                return [b.Left, b.Right];
            case BetweenExpr bt:
                return [bt.Operand, bt.Low, bt.High];
            case InListExpr il:
                return [il.Operand, .. il.Items];
            case NullTestExpr nt:
                return [nt.Operand];
            case LikeExpr l:
                return l.Escape is null ? [l.Left, l.Pattern] : [l.Left, l.Pattern, l.Escape];
            case CaseExpr c:
                var list = new List<Expr>();
                if (c.Operand is not null)
                {
                    list.Add(c.Operand);
                }

                foreach (var w in c.Whens)
                {
                    list.Add(w.When);
                    list.Add(w.Then);
                }

                if (c.Else is not null)
                {
                    list.Add(c.Else);
                }

                return list;
            default:
                return [];
        }
    }

    private CollKind CollOf(Expr e, out bool any) => CollOf(e, out _, out any);

    /// <summary>The collation of a comparison: the left operand's explicit COLLATE, then the right's, then the left's column, then the right's (datatype3 §7.1).</summary>
    private CollKind PairColl(Expr l, Expr r)
    {
        CollKind lc = CollOf(l, out bool le, out bool la);
        if (le)
        {
            return lc;
        }

        CollKind rc = CollOf(r, out bool re, out bool ra);
        if (re)
        {
            return rc;
        }

        if (la)
        {
            return lc;
        }

        return ra ? rc : CollKind.Binary;
    }

    private static int CmpFlags(Affinity a, CollKind c) => (int)a | ((int)c << 4);

    // ------------------------------------------------------------------ expressions

    private static bool ContainsAggregate(Expr e) => FindAggregate(e) is not null;

    /// <summary>The kind of aggregate a call is, or null for a scalar function (lang_aggfunc). min and max with several arguments are scalar.</summary>
    private static AggKind? AggregateKind(FunctionCall f)
    {
        if (f.Over is not null)
        {
            return null;
        }

        string n = f.Name.Text;
        int args = f.Args.IsDefaultOrEmpty ? 0 : f.Args.Length;
        if (t_owner?.FindUserAggregate(n, args) is not null)
        {
            return AggKind.User;
        }

        if (AsciiComparer.Instance.Equals(n, "count"))
        {
            return f.Star || args == 0 ? AggKind.CountStar : AggKind.Count;
        }

        if (f.Star)
        {
            return null;
        }

        if (AsciiComparer.Instance.Equals(n, "sum"))
        {
            return AggKind.Sum;
        }

        if (AsciiComparer.Instance.Equals(n, "total"))
        {
            return AggKind.Total;
        }

        if (AsciiComparer.Instance.Equals(n, "avg"))
        {
            return AggKind.Avg;
        }

        if (AsciiComparer.Instance.Equals(n, "min"))
        {
            return args == 1 ? AggKind.Min : null;
        }

        if (AsciiComparer.Instance.Equals(n, "max"))
        {
            return args == 1 ? AggKind.Max : null;
        }

        if (AsciiComparer.Instance.Equals(n, "group_concat") || AsciiComparer.Instance.Equals(n, "string_agg"))
        {
            return AggKind.GroupConcat;
        }

        if (AsciiComparer.Instance.Equals(n, "json_group_array"))
        {
            return AggKind.JsonGroupArray;
        }

        if (AsciiComparer.Instance.Equals(n, "jsonb_group_array"))
        {
            return AggKind.JsonbGroupArray;
        }

        if (AsciiComparer.Instance.Equals(n, "json_group_object"))
        {
            return AggKind.JsonGroupObject;
        }

        return AsciiComparer.Instance.Equals(n, "jsonb_group_object") ? AggKind.JsonbGroupObject : null;
    }

    /// <summary>The first aggregate call in an expression (not looking into subqueries), or null.</summary>
    private static FunctionCall? FindAggregate(Expr e)
    {
        switch (e)
        {
            case FunctionCall f:
                if (AggregateKind(f) is not null)
                {
                    return f;
                }

                foreach (Expr a in f.Args)
                {
                    if (FindAggregate(a) is { } fa)
                    {
                        return fa;
                    }
                }

                return null;
            case ParenExpr p:
                return FindAggregate(p.Inner);
            case UnaryExpr u:
                return FindAggregate(u.Operand);
            case BinaryExpr b:
                return FindAggregate(b.Left) ?? FindAggregate(b.Right);
            case CollateExpr c:
                return FindAggregate(c.Operand);
            case CastExpr ce:
                return FindAggregate(ce.Operand);
            case BetweenExpr bt:
                return FindAggregate(bt.Operand) ?? FindAggregate(bt.Low) ?? FindAggregate(bt.High);
            case InListExpr il:
                return FindAggregate(il.Operand) ?? il.Items.Select(FindAggregate).FirstOrDefault(x => x is not null);
            case InSelectExpr isl:
                return FindAggregate(isl.Operand);
            case NullTestExpr nt:
                return FindAggregate(nt.Operand);
            case LikeExpr l:
                return FindAggregate(l.Left) ?? FindAggregate(l.Pattern) ?? (l.Escape is null ? null : FindAggregate(l.Escape));
            case RowValueExpr rv:
                return rv.Items.Select(FindAggregate).FirstOrDefault(x => x is not null);
            case CaseExpr ce:
                return (ce.Operand is null ? null : FindAggregate(ce.Operand))
                    ?? ce.Whens.Select(w => FindAggregate(w.When) ?? FindAggregate(w.Then)).FirstOrDefault(x => x is not null)
                    ?? (ce.Else is null ? null : FindAggregate(ce.Else));
            default:
                return null;
        }
    }

    private int Compile(Expr e)
    {
        switch (e)
        {
            case ParenExpr p:
                return Compile(p.Inner);
            case CollateExpr c:
                ParseColl(c.Collation.Text);
                return Compile(c.Operand);
            case UnaryExpr { Op: UnaryOp.Plus } u:
                return Compile(u.Operand);
        }

        int r = NewReg();
        CompileInto(e, r);
        return r;
    }

    /// <summary>Two expressions written the same way (ignoring blanks, and case outside quotes), which the reference treats as the same expression.</summary>
    private bool SameText(Expr a, Expr b)
    {
        SourceSpan x = a.Span, y = b.Span;
        if (x.End <= x.Start || y.End <= y.Start || x.End > _sql.Length || y.End > _sql.Length || x.Length > 400)
        {
            return false;
        }

        if (a is ColumnRef || b is ColumnRef)
        {
            // a plain column counts only when both name the same column of the same table
            return a is ColumnRef ra && b is ColumnRef rb && TryResolveColumn(ra, out Source sa, out int colA) && TryResolveColumn(rb, out Source sb, out int colB) && sa == sb && colA == colB;
        }

        string p = Normalize(_sql.AsSpan(x.Start, x.Length)), q = Normalize(_sql.AsSpan(y.Start, y.Length));
        return string.Equals(p, q, StringComparison.Ordinal);

        static string Normalize(ReadOnlySpan<char> text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool quoted = false;
            char quote = '\0';
            foreach (char c in text)
            {
                if (quoted)
                {
                    quoted = c != quote;
                    sb.Append(c);
                }
                else if (c is '\'' or '"' or '`' or '[')
                {
                    quoted = true;
                    quote = c == '[' ? ']' : c;
                    sb.Append(c);
                }
                else if (!char.IsWhiteSpace(c))
                {
                    sb.Append(char.ToUpperInvariant(c));
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>While a group's output row is built: the GROUP BY expressions and the registers that hold their value from the group's first row.</summary>
    private List<(Expr Expr, int Reg)>? _groupKeyRegs;

    /// <summary>Generates code that leaves the value of <paramref name="e"/> in register <paramref name="dest"/>.</summary>
    private void CompileIntoCore(Expr e, int dest)
    {
        if (_groupKeyRegs is not null)
        {
            foreach ((Expr ge, int reg) in _groupKeyRegs)
            {
                if (ReferenceEquals(ge, e) || SameText(ge, e))
                {
                    Emit(Op.Copy, reg, dest);
                    return;
                }
            }
        }

        switch (e)
        {
            case ParenExpr p:
                CompileInto(p.Inner, dest);
                return;
            case CollateExpr c:
                ParseColl(c.Collation.Text);
                CompileInto(c.Operand, dest);
                return;
            case LiteralExpr lit:
                CompileLiteral(lit, dest);
                return;
            case RegExpr re:
                Emit(Op.Copy, re.Reg, dest);
                return;
            case VariableExpr v:
                Emit(Op.Variable, ParamIndex(v), dest);
                return;
            case ColumnRef cr:
                CompileColumn(cr, dest);
                return;
            case UnaryExpr u:
                CompileUnary(u, dest);
                return;
            case BinaryExpr b:
                CompileBinary(b, dest);
                return;
            case CastExpr ce:
            {
                CompileInto(ce.Operand, dest);
                Emit(Op.Cast, dest, 0, 0, null, (int)Values.CastAffinity(string.Join(' ', ce.Type.Words)));
                return;
            }

            case BetweenExpr bt:
            {
                int x = Compile(bt.Operand);
                int lo = Compile(bt.Low);
                int hi = Compile(bt.High);
                int t1 = NewReg(), t2 = NewReg();
                Emit(Op.Ge, x, lo, t1, null, CmpFlags(Values.CompareAffinity(CmpAffOf(bt.Operand), CmpAffOf(bt.Low)), PairColl(bt.Operand, bt.Low)));
                Emit(Op.Le, x, hi, t2, null, CmpFlags(Values.CompareAffinity(CmpAffOf(bt.Operand), CmpAffOf(bt.High)), PairColl(bt.Operand, bt.High)));
                Emit(Op.And, t1, t2, dest);
                if (bt.Negated)
                {
                    Emit(Op.Not, dest, dest);
                }

                return;
            }

            case InListExpr il:
                CompileIn(il, dest);
                return;
            case NullTestExpr nt:
                Emit(nt.Test == NullTest.IsNull ? Op.IsNullV : Op.NotNullV, Compile(nt.Operand), dest);
                return;
            case LikeExpr l:
                CompileLike(l, dest);
                return;
            case CaseExpr ce:
                CompileCase(ce, dest);
                return;
            case FunctionCall f:
                if (string.Equals(f.Name.Text, "rtreecheck", StringComparison.OrdinalIgnoreCase))
                {
                    // It reads the database, so the statement needs a read transaction even with no table in FROM.
                    _touchesFile = true;
                }

                CompileCall(f, dest);
                return;
            case RaiseExpr rz:
                CompileRaise(rz);
                return;
            case RowValueExpr:
                throw Err("row value misused");
            case SubqueryExpr sq:
                CompileScalarSubquery(sq.Select, dest);
                return;
            case ExistsExpr ex:
                CompileExists(ex.Select, dest);
                return;
            case InSelectExpr isel:
                CompileInSelect(isel.Operand, isel.Select, isel.Negated, dest);
                return;
            case InTableExpr { IsFunction: false } itab:
                CompileInSelect(itab.Operand, SelectAllFrom(itab.Table), itab.Negated, dest);
                return;
            case InTableExpr:
                throw Unsupported("table-valued functions");
            default:
                throw Unsupported(e.GetType().Name);
        }
    }

    // An anonymous "?" gets its number once, however many times its expression is compiled (the plan and the WHERE filter both
    // compile the same node).
    private readonly Dictionary<VariableExpr, int> _anonymous = new(ReferenceEqualityComparer.Instance);

    private int ParamIndex(VariableExpr v)
    {
        string text = v.Text;

        // lang_expr "Parameters": ? takes the next number, ?NNN that number, a name the next number unless seen before.
        if (text == "?")
        {
            if (_anonPos != null && _anonPos.TryGetValue(v.Span.Start, out int byPos))
            {
                return byPos;
            }

            if (_anonymous.TryGetValue(v, out int known))
            {
                return known;
            }

            _params.Add(null);
            _anonymous[v] = _params.Count;
            return _params.Count;
        }

        if (text.Length > 1 && text[0] == '?')
        {
            int n = int.Parse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
            if (n < 1 || n > 32766)
            {
                throw Err("variable number must be between ?1 and ?32766");
            }

            while (_params.Count < n)
            {
                _params.Add(null);
            }

            return n;
        }

        int ix = _params.IndexOf(text);
        if (ix >= 0)
        {
            return ix + 1;
        }

        _params.Add(text);
        return _params.Count;
    }

    private void CompileLiteral(LiteralExpr lit, int dest)
    {
        switch (lit.Kind)
        {
            case LiteralKind.Null:
                Emit(Op.Null, 0, dest);
                return;
            case LiteralKind.Integer:
                if (!Values.ParseIntegerLiteral(lit.Text, out Mem m, out _))
                {
                    throw Err("hex literal too big: " + lit.Text);
                }

                if (m.Kind == StorageClass.Integral)
                {
                    Emit(Op.Integer, 0, dest, 0, m.IntValue);
                }
                else
                {
                    Emit(Op.Real, 0, dest, 0, m.RealValue);
                }

                return;
            case LiteralKind.Float:
                Emit(Op.Real, 0, dest, 0, Values.ParseRealLiteral(lit.Text));
                return;
            case LiteralKind.String:
                Emit(Op.String8, 0, dest, 0, Encoding.UTF8.GetBytes(lit.Text));
                return;
            case LiteralKind.Blob:
                Emit(Op.Blob, 0, dest, 0, Convert.FromHexString(lit.Text));
                return;
            case LiteralKind.Identifier:
                // only a DEFAULT clause produces this: a bare name there is the text of the name, and TRUE and FALSE are 1 and 0 (lang_createtable)
                if (lit.Text.Equals("true", StringComparison.OrdinalIgnoreCase) || lit.Text.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    Emit(Op.Integer, 0, dest, 0, lit.Text.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1L : 0L);
                }
                else
                {
                    Emit(Op.String8, 0, dest, 0, Encoding.UTF8.GetBytes(lit.Text));
                }

                return;
            default:
            {
                // lang_datefunc: CURRENT_TIME, CURRENT_DATE and CURRENT_TIMESTAMP are time('now'), date('now') and datetime('now').
                int now = NewReg();
                Emit(Op.String8, 0, now, 0, "now"u8.ToArray());
                string fn = lit.Kind == LiteralKind.CurrentTime ? "time" : lit.Kind == LiteralKind.CurrentDate ? "date" : "datetime";
                Emit(Op.Function, now, 1, dest, FunctionTable.Find(fn));
                return;
            }
        }
    }

    private void CompileColumn(ColumnRef cr, int dest)
    {
        // lang_expr: TRUE and FALSE are names for 1 and 0 unless a column of that name exists.
        if (cr is { Schema: null, Table: null } && (AsciiComparer.Instance.Equals(cr.Column.Text, "true") || AsciiComparer.Instance.Equals(cr.Column.Text, "false"))
            && !(TryResolveColumn(cr, out _, out _)))
        {
            Emit(Op.Integer, 0, dest, 0, AsciiComparer.Instance.Equals(cr.Column.Text, "true") ? 1L : 0L);
            return;
        }

        if (_excluded != null && cr.Table is { } xt && AsciiComparer.Instance.Equals(xt.Text, "excluded"))
        {
            int xc = _table!.FindColumn(cr.Column.Text);
            if (xc < 0 && !_table.WithoutRowid && IsRowidName(cr.Column.Text))
            {
                Emit(Op.Copy, _excludedRowid, dest);
                return;
            }

            if (xc < 0)
            {
                throw NoSuchColumn(cr);
            }

            Emit(Op.Copy, _excluded[xc], dest);
            return;
        }

        if (!Resolve(cr, out Source? src, out int col, out Expr? alias, true))
        {
            throw NoSuchColumn(FirstMissingColumn(cr));
        }

        if (alias is not null)
        {
            if (!_aliasesActive.Add(alias))
            {
                throw NoSuchColumn(cr);
            }

            try
            {
                CompileInto(alias, dest);
            }
            finally
            {
                _aliasesActive.Remove(alias);
            }

            return;
        }

        EmitSourceColumn(src!, col, dest, cr.Table is null);
    }

    private readonly HashSet<Expr> _aliasesActive = new(ReferenceEqualityComparer.Instance);

    /// <summary>Reads a column of a FROM item into a register: from the row the loops stand on, or from a register that holds it (after grouping).</summary>
    private void EmitSourceColumn(Source src, int col, int dest, bool coalesce = true)
    {
        if (coalesce && src.Coalesce.TryGetValue(col, out (Source Other, int Col) co))
        {
            // A merged column of a RIGHT or FULL join: the left value, or the right one where the left row is missing.
            EmitSourceColumnOnly(src, col, dest);
            int done = NewLabel();
            Emit(Op.NotNull, dest, done);
            EmitSourceColumnOnly(co.Other, co.Col, dest);
            Mark(done);
            return;
        }

        EmitSourceColumnOnly(src, col, dest);
    }

    private void EmitSourceColumnOnly(Source src, int col, int dest)
    {
        if (_colRegs is not null && ReferenceEquals(src, _dmlSource))
        {
            // While a row is being written its columns live in registers, not in a cursor.
            Emit(Op.Copy, col < 0 || col == src.Table.RowidAlias ? _rowidReg : _colRegs[col], dest);
            return;
        }

        for (int k = _aggSaves.Count - 1; k >= 0; k--)
        {
            // A column of a grouped query seen from a subquery in one of its expressions.
            AggSave outer = _aggSaves[k];
            if (!outer.Sources.Contains(src) || (outer.RecordCols is null && outer.ColMap is null))
            {
                continue;
            }

            if (outer.RecordCols is not null)
            {
                outer.RecordCols.TryAdd((src, col), outer.RecordCols.Count);
                Emit(Op.Null, 0, dest);
                return;
            }

            if (!outer.ColMap!.TryGetValue((src, col), out int outerReg))
            {
                throw new InvalidOperationException("a column was not planned for the grouped row");
            }

            Emit(Op.Copy, outerReg, dest);
            return;
        }

        // Window queries carry only the columns of their own FROM items; a subquery's tables are read where they are.
        bool carried = _carryScope is null || _carryScope.Sources.Contains(src);
        if (_recordCols is not null && carried)
        {
            _recordCols.TryAdd((src, col), _recordCols.Count);
            Emit(Op.Null, 0, dest);
            return;
        }

        if (_colMap is not null && carried)
        {
            if (!_colMap.TryGetValue((src, col), out int reg))
            {
                throw new InvalidOperationException("a column was not planned for the grouped row");
            }

            Emit(Op.Copy, reg, dest);
            return;
        }

        if (src.Kind is SourceKind.Module or SourceKind.VTab)
        {
            Emit(col < 0 ? Op.VRowid : Op.VColumn, src.Cursor, col < 0 ? dest : col, dest);
            return;
        }

        if (src.Kind != SourceKind.Table)
        {
            Emit(Op.EColumn, src.Cursor, col, dest);
            return;
        }

        if (src.CoverCursor >= 0)
        {
            EmitCoveredColumn(src, col, dest);
            return;
        }

        if (col < 0)
        {
            Emit(Op.Rowid, src.Cursor, dest);
            return;
        }

        CompileTableColumn(src, col, dest);
    }

    /// <summary>While planning a grouped query: collects the columns the grouped rows need instead of generating code for them.</summary>
    private Dictionary<(Source, int), int>? _recordCols;

    /// <summary>While generating code that runs after grouping: the register that holds each column.</summary>
    private Dictionary<(Source, int), int>? _colMap;

    /// <summary>
    /// Reads a column from the index entry a covered loop stands on. The planner decided from the query's text that the index holds every column the
    /// query reads; if code generation asks for another one, <see cref="CoverMissException"/> makes the statement compile again without covering.
    /// </summary>
    private void EmitCoveredColumn(Source src, int col, int dest)
    {
        TableInfo table = src.Table;
        if (col < 0 || col == table.RowidAlias)
        {
            Emit(Op.IdxRowid, src.CoverCursor, dest);
            return;
        }

        int at = Array.IndexOf(src.CoverIndex!.Columns, col);
        ColumnInfo c = table.Columns[col];
        if (at < 0 || c.Generated != GeneratedKind.None)
        {
            throw new CoverMissException();
        }

        Emit(Op.Column, src.CoverCursor, at, dest, null, c.Affinity == Affinity.Real ? 1 : 0);
    }

    /// <summary>Set when a statement is compiled again after a covered index loop was asked for a column its index lacks.</summary>
    internal bool NoCover { get; init; }

    private void CompileTableColumn(Source src, int col, int dest)
    {
        TableInfo table = src.Table;
        int cursor = src.Cursor;
        ColumnInfo c = table.Columns[col];
        if (col == table.RowidAlias)
        {
            Emit(Op.Rowid, cursor, dest);
            return;
        }

        if (c.Generated == GeneratedKind.Virtual)
        {
            if (++_genDepth > 20)
            {
                throw Err("generated column loop on \"" + c.Name + "\"");
            }

            // A virtual column is computed from its expression each time it is read, then given the column's affinity (lang_createtable "Generated columns").
            bool saved = _inGenerated;
            _inGenerated = true;
            Scope? savedScope = _scope;
            _scope = new Scope { Parent = null, Depth = (savedScope?.Depth ?? -1) + 1 };
            _scope.Sources.Add(src);
            try
            {
                CompileInto(c.GeneratedExpr!, dest);
            }
            finally
            {
                _scope = savedScope;
            }

            _inGenerated = saved;
            _genDepth--;
            if (c.Affinity != Affinity.None)
            {
                Emit(Op.Affinity, dest, 0, 0, null, (int)c.Affinity);
            }

            return;
        }

        object? dflt = null;
        if (c.Default is not null && col >= 0)
        {
            dflt = DefaultValue(c);
        }

        Emit(Op.Column, cursor, c.RecordIndex, dest, dflt, c.Affinity == Affinity.Real ? 1 : 0);
    }

    private bool _inGenerated;
    private int _genDepth;

    /// <summary>The value stored columns read as when a record is shorter than the table (ALTER TABLE ADD COLUMN): the column's default with its affinity applied.</summary>
    private static DefaultMem DefaultValue(ColumnInfo c) => DefaultValueOf(c.Default!, c.Affinity);

    private static DefaultMem DefaultValueOf(Expr e, Affinity affinity)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        Mem m;
        string? realSource = null; // the written form of a real default, which a TEXT column keeps as written
        switch (e)
        {
            case LiteralExpr { Kind: LiteralKind.Null }:
                return DefaultMem.Null;
            case LiteralExpr { Kind: LiteralKind.Integer } li:
                Values.ParseIntegerLiteral(li.Text, out m, out _);
                realSource = li.Text;
                break;
            case LiteralExpr { Kind: LiteralKind.Float } lf:
                m = Mem.FromReal(Values.ParseRealLiteral(lf.Text));
                realSource = lf.Text;
                break;
            case LiteralExpr { Kind: LiteralKind.Identifier } li2 when li2.Text.Equals("true", StringComparison.OrdinalIgnoreCase) || li2.Text.Equals("false", StringComparison.OrdinalIgnoreCase):
                m = Mem.FromInt(li2.Text.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0); // lang_createtable: DEFAULT true is 1, as in an expression
                break;
            case LiteralExpr { Kind: LiteralKind.String or LiteralKind.Identifier } ls:
                m = Mem.FromTextBytes(Encoding.UTF8.GetBytes(ls.Text)); // a bare name as a default is the text of the name
                break;
            case CastExpr ce:
            {
                m = DefaultValueOf(ce.Operand, Affinity.None).Value;
                byte[]? castOwn = null;
                Values.Cast(ref m, ref castOwn, Values.CastAffinity(string.Join(' ', ce.Type.Words)));
                break;
            }
            case LiteralExpr { Kind: LiteralKind.Blob } lb:
                m = Mem.FromBlob(Convert.FromHexString(lb.Text));
                break;
            case UnaryExpr { Op: UnaryOp.Minus or UnaryOp.Plus } u when u.Operand is LiteralExpr { Kind: LiteralKind.Integer or LiteralKind.Float } ln:
            {
                realSource = (u.Op == UnaryOp.Minus ? "-" : string.Empty) + ln.Text;
                if (ln.Kind == LiteralKind.Integer)
                {
                    Values.ParseIntegerLiteral(ln.Text, out m, out _);
                }
                else
                {
                    m = Mem.FromReal(Values.ParseRealLiteral(ln.Text));
                }

                if (u.Op == UnaryOp.Minus && ln.Text == "9223372036854775808")
                {
                    m = Mem.FromInt(long.MinValue); // the one literal whose digits alone do not fit, as in an expression
                }
                else if (u.Op == UnaryOp.Minus)
                {
                    if (ln.Kind == LiteralKind.Float)
                    {
                        // Observed: a negated REAL literal is negated outright, so -0.0 is negative zero (a run-time 0 - x gives +0.0).
                        m = Mem.FromReal(-m.RealValue);
                    }
                    else
                    {
                        Mem zero = Mem.FromInt(0), r = default;
                        Values.Sub(zero, m, ref r);
                        m = r;
                    }
                }

                break;
            }

            case ColumnRef { Schema: null, Table: null } cr when string.Equals(cr.Column.Text, "true", StringComparison.OrdinalIgnoreCase):
                m = Mem.FromInt(1);
                break;
            case ColumnRef { Schema: null, Table: null } cf when string.Equals(cf.Column.Text, "false", StringComparison.OrdinalIgnoreCase):
                m = Mem.FromInt(0);
                break;
            default:
                // Only a constant default can be missing from a short record: ALTER TABLE ADD COLUMN refuses the others on a table with rows
                // (lang_altertable). Rows written in full carry their own values, so this value is never read.
                return DefaultMem.Null;
        }

        // Observed: a TEXT column's default that is a real (or an integer too large for 64 bits) reads back as the literal was written
        // ('1e300', '5.0', '-9223372036854775809'), not as the real's own text form.
        if (affinity == Affinity.Text && m.Kind == StorageClass.Real && realSource is not null)
        {
            return new DefaultMem(Mem.FromTextBytes(Encoding.UTF8.GetBytes(realSource)));
        }

        byte[]? own = null;
        Values.ApplyStored(ref m, ref own, affinity);
        return new DefaultMem(m);
    }

    private void CompileUnary(UnaryExpr u, int dest)
    {
        switch (u.Op)
        {
            case UnaryOp.Plus:
                CompileInto(u.Operand, dest);
                return;
            case UnaryOp.Minus:
            {
                // -9223372036854775808 is one literal: the digits alone do not fit a 64-bit integer (lang_expr).
                Expr minusOperand = u.Operand;
                // Observed: unary plus and parentheses between the minus and the literal don't matter.
                while (minusOperand is ParenExpr or UnaryExpr { Op: UnaryOp.Plus })
                {
                    minusOperand = minusOperand is ParenExpr pe ? pe.Inner : ((UnaryExpr)minusOperand).Operand;
                }

                if (minusOperand is LiteralExpr { Kind: LiteralKind.Integer or LiteralKind.Float } lit && lit.Text == "9223372036854775808")
                {
                    Emit(Op.Integer, 0, dest, 0, long.MinValue);
                    return;
                }

                if (minusOperand is LiteralExpr { Kind: LiteralKind.Float } fl)
                {
                    Emit(Op.Real, 0, dest, 0, -Values.ParseRealLiteral(fl.Text));
                    return;
                }

                Emit(Op.Negate, Compile(u.Operand), dest);
                return;
            }

            case UnaryOp.BitNot:
                Emit(Op.BitNot, Compile(u.Operand), dest);
                return;
            default:
                Emit(Op.Not, Compile(u.Operand), dest);
                return;
        }
    }

    private void CompileBinary(BinaryExpr b, int dest)
    {
        if (b.Op is BinaryOp.Arrow or BinaryOp.Arrow2)
        {
            // -> and ->> are the json functions of the same names (lang_expr "JSON operators").
            int arrow = NewRegs(2);
            CompileInto(b.Left, arrow);
            CompileInto(b.Right, arrow + 1);
            Emit(Op.Function, arrow, 2, dest, FunctionTable.Find(b.Op == BinaryOp.Arrow ? "->" : "->>"));
            return;
        }

        if (IsRow(b.Left) || IsRow(b.Right))
        {
            if (b.Op is BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Is or BinaryOp.IsNot or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge)
            {
                CompileRowCompare(b.Op, b.Left, b.Right, dest);
                return;
            }

            throw Err("row value misused");
        }

        Op op;
        switch (b.Op)
        {
            case BinaryOp.And:
                op = Op.And;
                break;
            case BinaryOp.Or:
                op = Op.Or;
                break;
            case BinaryOp.Add:
                op = Op.Add;
                break;
            case BinaryOp.Sub:
                op = Op.Subtract;
                break;
            case BinaryOp.Mul:
                op = Op.Multiply;
                break;
            case BinaryOp.Div:
                op = Op.Divide;
                break;
            case BinaryOp.Rem:
                op = Op.Remainder;
                break;
            case BinaryOp.Concat:
                op = Op.Concat;
                break;
            case BinaryOp.BitAnd:
                op = Op.BitAnd;
                break;
            case BinaryOp.BitOr:
                op = Op.BitOr;
                break;
            case BinaryOp.Lshift:
                op = Op.ShiftLeft;
                break;
            case BinaryOp.Rshift:
                op = Op.ShiftRight;
                break;
            case BinaryOp.Eq:
                op = Op.Eq;
                break;
            case BinaryOp.Ne:
                op = Op.Ne;
                break;
            case BinaryOp.Lt:
                op = Op.Lt;
                break;
            case BinaryOp.Le:
                op = Op.Le;
                break;
            case BinaryOp.Gt:
                op = Op.Gt;
                break;
            case BinaryOp.Ge:
                op = Op.Ge;
                break;
            case BinaryOp.Is:
                op = Op.Is;
                break;
            case BinaryOp.IsNot:
                op = Op.IsNot;
                break;
            default:
                throw Unsupported("the -> and ->> operators");
        }

        if (b.Op is BinaryOp.And or BinaryOp.Or)
        {
            // Observed: in a value context the reference evaluates both operands (so an error in the right one is raised even when the
            // left decides the result) unless one side is a literal that decides it (0 AND x, x OR 1), which drops the other side.
            // Conditions (WHERE, CASE WHEN, iif) jump instead: see CompileCondition.
            if (ConstTruth(b) is { } decided)
            {
                int skip = NewLabel();
                Emit(Op.Goto, 0, skip);
                Compile(b.Left);
                Compile(b.Right);
                Mark(skip);
                Emit(Op.Integer, 0, dest, 0, decided ? 1L : 0L);
                return;
            }

            int el = Compile(b.Left);
            int er = Compile(b.Right);
            Emit(op, el, er, dest);
            return;
        }

        // Observed: IS, IS NOT and IS [NOT] DISTINCT FROM resolve the right operand's names before the left's, so a missing column is
        // reported from the right one first.
        int l, r;
        if (b.Op is BinaryOp.Is or BinaryOp.IsNot)
        {
            r = Compile(b.Right);
            l = Compile(b.Left);
        }
        else
        {
            l = Compile(b.Left);
            r = Compile(b.Right);
        }

        int flags = op is >= Op.Eq and <= Op.IsNot ? CmpFlags(Values.CompareAffinity(CmpAffOf(b.Left), CmpAffOf(b.Right)), PairColl(b.Left, b.Right)) : 0;
        Emit(op, l, r, dest, null, flags);
    }

    private void CompileIn(InListExpr il, int dest)
    {
        if (IsRow(il.Operand))
        {
            CompileInRows(il, dest);
            return;
        }

        if (il.Items.Any(IsRow))
        {
            throw Err("row value misused");
        }

        if (il.Items.IsDefaultOrEmpty)
        {
            // x IN () is false and x NOT IN () is true, even for a NULL x (lang_expr "The IN and NOT IN operators"). An operand with no function
            // call is dropped before names are resolved, so a bad column in it is not reported; one with a call is still resolved, and jumped
            // over rather than run: the reference never evaluates it, so abs(minimum integer) IN () raises nothing.
            if (HasCall(il.Operand))
            {
                int skip = NewLabel();
                Emit(Op.Goto, 0, skip);
                Compile(il.Operand);
                Mark(skip);
            }

            Emit(Op.Integer, 0, dest, 0, il.Negated ? 1L : 0L);
            return;
        }

        int x = Compile(il.Operand);
        int sawNull = NewReg();
        Emit(Op.Integer, 0, sawNull, 0, 0L);
        int found = NewLabel(), isNull = NewLabel(), done = NewLabel();
        Emit(Op.IfNull, x, isNull);
        foreach (Expr item in il.Items)
        {
            int v = Compile(item);
            int t = NewReg();
            // "x IN (y, z)" means x = +y OR x = +z (lang_expr): the list items carry no affinity of their own.
            Emit(Op.Eq, x, v, t, null, CmpFlags(Values.CompareAffinity(CmpAffOf(il.Operand), Affinity.None), PairColl(il.Operand, item)));
            Emit(Op.If, t, found, 0);
            int tn = NewReg();
            Emit(Op.IsNullV, t, tn);
            Emit(Op.Or, sawNull, tn, sawNull);
        }

        int setNull = NewLabel();
        Emit(Op.If, sawNull, setNull, 0);
        Emit(Op.Integer, 0, dest, 0, il.Negated ? 1L : 0L);
        Emit(Op.Goto, 0, done);
        Mark(setNull);
        Emit(Op.Null, 0, dest);
        Emit(Op.Goto, 0, done);
        Mark(found);
        Emit(Op.Integer, 0, dest, 0, il.Negated ? 0L : 1L);
        Emit(Op.Goto, 0, done);
        Mark(isNull);
        foreach (Expr item in il.Items)
        {
            // Observed: a NULL operand still evaluates the calls in the list, so an error in one is raised.
            if (HasCall(item))
            {
                Compile(item);
            }
        }

        Emit(Op.Null, 0, dest);
        Mark(done);
    }

    private void CompileLike(LikeExpr l, int dest)
    {
        if (l.Op == LikeOp.Match)
        {
            // A MATCH a virtual table does not take is an error when it runs; the operands still have to resolve.
            int mb = NewRegs(2);
            CompileInto(l.Left, mb);
            CompileInto(l.Pattern, mb + 1);
            Emit(Op.Function, mb, 2, dest, FunctionTable.Find("match"));
            if (l.Negated)
            {
                Emit(Op.Not, dest, dest);
            }

            return;
        }

        if (l.Op == LikeOp.Regexp)
        {
            throw Unsupported("REGEXP");
        }

        // LIKE and GLOB are calls of like(pattern, text): the pattern's names are resolved first.
        int pat = Compile(l.Pattern);
        int text = Compile(l.Left);
        int esc = 0;
        if (l.Escape is not null)
        {
            esc = Compile(l.Escape);
        }

        Emit(l.Op == LikeOp.Like ? Op.Like : Op.Glob, text, pat, dest, null, esc);
        if (l.Negated)
        {
            Emit(Op.Not, dest, dest);
        }
    }

    private void CompileCase(CaseExpr ce, int dest)
    {
        int end = NewLabel();
        int x = ce.Operand is null ? 0 : Compile(ce.Operand);
        foreach (WhenClause w in ce.Whens)
        {
            int next = NewLabel();
            int wr = ce.Operand is null ? CompileCondition(w.When) : Compile(w.When);
            int cond = wr;
            if (ce.Operand is not null)
            {
                cond = NewReg();
                Emit(Op.Eq, x, wr, cond, null, CmpFlags(Values.CompareAffinity(CmpAffOf(ce.Operand), CmpAffOf(w.When)), PairColl(ce.Operand, w.When)));
            }

            Emit(Op.IfNot, cond, next, 1);
            CompileInto(w.Then, dest);
            Emit(Op.Goto, 0, end);
            Mark(next);
        }

        if (ce.Else is not null)
        {
            CompileInto(ce.Else, dest);
        }
        else
        {
            Emit(Op.Null, 0, dest);
        }

        Mark(end);
    }

    /// <summary>likelihood(X, Y) wants Y to be a floating-point literal in [0.0, 1.0] (lang_corefunc; an integer literal is refused).</summary>
    private static bool IsConstantProbability(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        if (e is LiteralExpr { Kind: LiteralKind.Float } lit)
        {
            double d = Values.ParseRealLiteral(lit.Text);
            return d >= 0.0 && d <= 1.0;
        }

        return false;
    }

    /// <summary>iif(c1, v1, c2, v2, ..., else): the first true condition's value, short-circuited like CASE.</summary>
    private void CompileIif(FunctionCall f, int dest)
    {
        int end = NewLabel();
        int n = f.Args.Length;
        int i = 0;
        for (; i + 1 < n; i += 2)
        {
            int next = NewLabel();
            int c = CompileCondition(f.Args[i]);
            Emit(Op.IfNot, c, next, 1);
            CompileInto(f.Args[i + 1], dest);
            Emit(Op.Goto, 0, end);
            Mark(next);
        }

        if (i < n)
        {
            CompileInto(f.Args[i], dest);
        }
        else
        {
            Emit(Op.Null, 0, dest);
        }

        Mark(end);
    }

    private void CompileCall(FunctionCall f, int dest)
    {
        string name = f.Name.Text;
        if (f.Over is not null)
        {
            CompileWindowCall(f, dest);
            return;
        }

        if (IsWindowOnly(name, out _, out _, out _))
        {
            throw Err("misuse of window function " + name + "()");
        }

        if (AggregateKind(f) is { } akind)
        {
            CompileAggregateRef(f, akind, dest);
            return;
        }

        if (f.Star)
        {
            throw Unsupported("function " + name + "(*)");
        }

        if (f.Filter is not null || f.Quantifier != CallQuantifier.None || !f.OrderBy.IsDefaultOrEmpty)
        {
            throw Unsupported("aggregate syntax in " + name + "()");
        }

        FuncDef? def = _cat.Owner?.FindUserFunction(name, f.Args.IsDefaultOrEmpty ? 0 : f.Args.Length) ?? FunctionTable.Find(name);
        if (def is null)
        {
            if (FunctionTable.IsKnownUnsupported(name))
            {
                throw Unsupported("function " + name + "()");
            }

            throw Err("no such function: " + name);
        }

        int n = f.Args.Length;
        if (def.Id is FuncId.MaxN or FuncId.MinN && n == 1)
        {
            throw Unsupported("aggregate " + name.ToLowerInvariant() + "()");
        }

        if (n < def.MinArgs || n > def.MaxArgs)
        {
            throw Err("wrong number of arguments to function " + name + "()");
        }

        int flags = 0;
        bool badLikelihood = false;
        switch (def.Id)
        {
            case FuncId.Iif:
                CompileIif(f, dest);
                return;
            case FuncId.Like:
            case FuncId.Glob:
            {
                // like(Y, X [, Z]) is X LIKE Y [ESCAPE Z]; glob(Y, X) is X GLOB Y (lang_corefunc).
                int pat = Compile(f.Args[0]);
                int text = Compile(f.Args[1]);
                int esc = n > 2 ? Compile(f.Args[2]) : 0;
                Emit(def.Id == FuncId.Like ? Op.Like : Op.Glob, text, pat, dest, null, esc);
                return;
            }

            case FuncId.Likelihood:
                // Checked once the arguments are resolved, so an error inside them ("no such function") is reported first.
                badLikelihood = !IsConstantProbability(f.Args[1]);
                break;
            case FuncId.MaxN:
            case FuncId.MinN:
            case FuncId.Nullif:
                // The collating sequence of the first argument that has one decides string comparisons.
                foreach (Expr a in f.Args)
                {
                    CollKind ck = CollOf(a, out bool anyColl);
                    if (anyColl)
                    {
                        flags = (int)ck;
                        break;
                    }
                }

                break;
        }

        if (def.Id is FuncId.Coalesce or FuncId.Ifnull)
        {
            int end = NewLabel();
            for (int i = 0; i < n - 1; i++)
            {
                CompileInto(f.Args[i], dest);
                Emit(Op.NotNull, dest, end);
            }

            CompileInto(f.Args[n - 1], dest);
            Mark(end);
            return;
        }

        int bas = NewRegs(n);
        for (int i = 0; i < n; i++)
        {
            CompileInto(f.Args[i], bas + i);
        }

        if (badLikelihood)
        {
            throw Err("second argument to likelihood() must be a constant between 0.0 and 1.0");
        }

        Emit(Op.Function, bas, n, dest, def, flags);
    }

    /// <summary>The truth value of an operand the reference treats as a constant in AND/OR: an integer literal, TRUE or FALSE, or an AND/OR
    /// one of whose sides decides it (0 AND x is 0, x OR 1 is 1). Null when it is not constant.</summary>
    private bool? ConstTruth(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        switch (e)
        {
            case LiteralExpr { Kind: LiteralKind.Integer } lit:
                return Values.ParseIntegerLiteral(lit.Text, out Mem m, out _) && m.Kind == StorageClass.Integral ? m.IntValue != 0 : null;
            case ColumnRef { Schema: null, Table: null } cr when (AsciiComparer.Instance.Equals(cr.Column.Text, "true") || AsciiComparer.Instance.Equals(cr.Column.Text, "false"))
                && !(_scope is not null && TryResolveColumn(cr, out _, out _)):
                return AsciiComparer.Instance.Equals(cr.Column.Text, "true");
            case InListExpr { Negated: false, Items.IsDefaultOrEmpty: true }:
                return false;
            case BinaryExpr { Op: BinaryOp.And } ba:
                return ConstTruth(ba.Left) == false || ConstTruth(ba.Right) == false ? false : null;
            case BinaryExpr { Op: BinaryOp.Or } bo:
                return ConstTruth(bo.Left) == true || ConstTruth(bo.Right) == true ? true : null;
            default:
                return null;
        }
    }

    /// <summary>Compiles a condition (WHERE, CASE WHEN, iif): AND/OR jump past the right operand once the left decides the result, as the
    /// reference does there, so an error in the skipped operand is never raised. NOT and parentheses pass the jumping through.</summary>
    private int CompileCondition(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        if (e is UnaryExpr { Op: UnaryOp.Not } n)
        {
            int inner = CompileCondition(n.Operand);
            int notReg = NewReg();
            Emit(Op.Not, inner, notReg);
            return notReg;
        }

        if (e is BetweenExpr bw && !HasCall(bw.Operand))
        {
            // Observed: x BETWEEN a AND b is x >= a AND x <= b and x NOT BETWEEN a AND b is x < a OR x > b, and in a condition the second
            // half is skipped once the first decides, so an error in it is never raised.
            BinaryExpr first = new(bw.Negated ? BinaryOp.Lt : BinaryOp.Ge, bw.Operand, bw.Low) { Span = bw.Span };
            BinaryExpr second = new(bw.Negated ? BinaryOp.Gt : BinaryOp.Le, bw.Operand, bw.High) { Span = bw.Span };
            e = new BinaryExpr(bw.Negated ? BinaryOp.Or : BinaryOp.And, first, second) { Span = bw.Span };
        }

        if (e is BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } b && ConstTruth(b) is null)
        {
            int dest = NewReg();
            int l0 = CompileCondition(b.Left);
            int decided = NewLabel(), end = NewLabel();
            Emit(b.Op == BinaryOp.And ? Op.IfNot : Op.If, l0, decided, 0);
            int r0 = CompileCondition(b.Right);
            Emit(b.Op == BinaryOp.And ? Op.And : Op.Or, l0, r0, dest);
            Emit(Op.Goto, 0, end);
            Mark(decided);
            Emit(Op.Integer, 0, dest, 0, b.Op == BinaryOp.And ? 0L : 1L);
            Mark(end);
            return dest;
        }

        return Compile(e);
    }

    /// <summary>Emits "jump to <paramref name="skip"/> unless the condition is true", splitting a top-level AND so that later terms are not evaluated for rows an earlier term rejects.</summary>
    private void CompileFilter(Expr where, int skip)
    {
        foreach (Expr c in Conjuncts(where))
        {
            int r = CompileCondition(c);
            Emit(Op.IfNot, r, skip, 1);
        }
    }

    private static IEnumerable<Expr> Conjuncts(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        if (e is BinaryExpr { Op: BinaryOp.And } b)
        {
            foreach (Expr x in Conjuncts(b.Left))
            {
                yield return x;
            }

            foreach (Expr x in Conjuncts(b.Right))
            {
                yield return x;
            }
        }
        else
        {
            yield return e;
        }
    }
}

/// <summary>A column default as an instruction operand.</summary>
internal sealed class DefaultMem
{
    internal static readonly DefaultMem Null = new(default);

    internal DefaultMem(Mem value) => Value = value;

    internal Mem Value { get; }
}
