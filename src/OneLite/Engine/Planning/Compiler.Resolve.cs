using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

internal sealed partial class Compiler
{
    // ------------------------------------------------------------------ name resolution

    private static bool IsRowidName(string n)
        => AsciiComparer.Instance.Equals(n, "rowid") || AsciiComparer.Instance.Equals(n, "_rowid_") || AsciiComparer.Instance.Equals(n, "oid");

    /// <summary>The schema of a star-expanded column of a subquery whose alias a table shares; compared by reference, so no user text can equal it.</summary>
    internal static readonly Name SubquerySchema = new("*");

    private static string RefText(ColumnRef cr)
        => cr.Schema is not null ? cr.Schema.Text + "." + cr.Table!.Text + "." + cr.Column.Text
            : cr.Table is not null ? cr.Table.Text + "." + cr.Column.Text : cr.Column.Text;

    /// <summary>
    /// Finds what a column reference names, innermost SELECT first (lang_select, lang_expr): a column of a FROM item, the rowid, or an alias
    /// of a result column where aliases are visible. A name found in several FROM items of one SELECT is ambiguous.
    /// </summary>
    private bool Resolve(ColumnRef cr, out Source? src, out int col, out Expr? alias, bool report)
    {
        _ = report;
        src = null;
        col = -2;
        alias = null;
        bool inSubquery = ReferenceEquals(cr.Schema, SubquerySchema);
        int wantDb = -1;
        if (cr.Schema is not null && !inSubquery)
        {
            if (cr.Table is null)
            {
                return false;
            }

            wantDb = _cat.SlotOf(cr.Schema.Text) is var sl && sl >= 0 ? sl : -2;
            if (wantDb == -2)
            {
                return false;
            }
        }

        string name = cr.Column.Text;
        for (Scope? sc = _scope; sc is not null; sc = sc.Parent)
        {
            Source? found = null;
            int fcol = -2, count = 0;
            foreach (Source s in sc.Sources)
            {
                if (cr.Table is { } tq && !AsciiComparer.Instance.Equals(tq.Text, s.Alias))
                {
                    continue;
                }

                if (cr.Schema is not null && (s.Kind == SourceKind.Table) == inSubquery)
                {
                    continue;
                }

                if (wantDb >= 0 && s.Table.Db != wantDb)
                {
                    continue;
                }

                int c = s.Table.FindColumn(name);
                if (c >= 0)
                {
                    if (cr.Table is null && !_inGenerated && s.Hidden.Contains(c))
                    {
                        continue;
                    }

                    count++;
                    if (found is null)
                    {
                        found = s;
                        fcol = c;
                    }
                }
                else if (((s.Kind == SourceKind.Table && !s.Table.WithoutRowid) || s.Kind is SourceKind.Module or SourceKind.VTab) && IsRowidName(name))
                {
                    count++;
                    if (found is null)
                    {
                        found = s;
                        fcol = -1;
                    }
                }
            }

            if (count > 1)
            {
                throw Err("ambiguous column name: " + RefText(cr));
            }

            if (count == 1)
            {
                foreach (Probe p in _probes)
                {
                    if (p.InnerDepth > sc.Depth)
                    {
                        p.Correlated = true;
                        p.Outer?.Add(found!);
                    }
                }

                src = found;
                col = fcol;
                return true;
            }

            if (cr.Table is null && cr.Schema is null && sc.AliasesVisible && sc.Aliases.TryGetValue(name, out alias))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves a reference to a column of a FROM item (column -1 is the rowid); false for an alias or an unknown name.</summary>
    private bool TryResolveColumn(ColumnRef cr, out Source src, out int col)
    {
        if (Resolve(cr, out Source? s, out col, out Expr? alias, false) && alias is null)
        {
            src = s!;
            return true;
        }

        src = null!;
        col = -2;
        return false;
    }

    private OneLiteException NoSuchColumn(ColumnRef cr)
    {
        string text = RefText(cr);
        SourceSpan sp = cr.Span;
        bool dq = cr.Table is null && sp.Start < _sql.Length && _sql[sp.Start] == '"';
        return Err("no such column: " + (dq ? "\"" + text + "\" - should this be a string literal in single-quotes?" : text));
    }

    // ------------------------------------------------------------------ recursive CTE

    /// <summary>
    /// The loop of a recursive common table expression (lang_with "Recursive Common Table Expressions"): the initial rows go in a queue; each queued row
    /// is handed to the outer query and then expanded by the recursive part into more queued rows. LIMIT and OFFSET of the CTE count the rows handed out.
    /// </summary>
    private void EmitRecursiveLoop(Source s, int lend, Action<int> body)
    {
        RecursiveCte r = s.Rec!;
        CteDef def = r.Def;
        int q = r.QueueCursor, cc = r.CurrentCursor;
        Emit(Op.EphOpen, q, 0, 0, null, 1);
        Emit(Op.EphOpen, cc);
        Dest dq;
        if (r.Distinct)
        {
            int set = NewSet();
            Emit(Op.SetOpen, set, 0, 0, new SetSpec { Collations = [.. Enumerable.Repeat(CollKind.Binary, s.Table.Columns.Length)] });
            dq = new Dest { Kind = DestKind.EphDistinct, Cursor = q, Set = set };
        }
        else
        {
            dq = new Dest { Kind = DestKind.Eph, Cursor = q };
        }

        CteNode? savedEnv = _cteEnv;
        Scope? savedScope = _scope;
        int limReg = 0, offReg = 0;
        try
        {
            _scope = null;
            _cteEnv = def.Env;
            foreach (SimpleSelect init in r.Initial)
            {
                CompileOne(init, dq, false);
            }

            _scope = savedScope;
            EmitLimitRegs((r.Select.Cores[^1] as SimpleSelect)?.Limit, ref limReg, ref offReg);
            if (limReg != 0)
            {
                Emit(Op.IfNot, limReg, lend, 0);
            }

            Emit(Op.ERewind, q, lend);
            int top = NewLabel(), next = NewLabel(), skip = NewLabel();
            Mark(top);
            if (offReg != 0)
            {
                Emit(Op.IfPos, offReg, skip, 1);
            }

            body(next);
            Mark(next);
            if (limReg != 0)
            {
                Emit(Op.DecrJumpZero, limReg, lend);
            }

            Mark(skip);
            Emit(Op.ECopyRow, cc, q);
            _scope = null;
            _cteEnv = def.EnvForBody;
            def.CurrentCursor = cc;
            try
            {
                foreach (SimpleSelect step in r.Step)
                {
                    CompileOne(step, dq, false);
                }
            }
            finally
            {
                def.CurrentCursor = -1;
            }

            Emit(Op.ENext, q, top);
        }
        finally
        {
            _cteEnv = savedEnv;
            _scope = savedScope;
        }
    }
}
