using System.Collections.Immutable;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using SqlStatement = RecursiveAI.OneLite.Sql.Statement;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// Triggers (lang_createtrigger). A trigger fires from a TrigRun instruction placed around the row's change; its programs are compiled from
// the trigger's own SQL text on first use, like a foreign key action. OLD.x and NEW.x in that text are rewritten to numbered parameters
// (OLD first, then NEW, each the table's columns followed by the rowid) before the text is parsed, so the programs never need to know
// about the row that fired them.

/// <summary>The programs of one trigger: an optional WHEN test and one program per body statement, compiled on first use.</summary>
internal sealed class TrigSpec
{
    internal TriggerInfo Info = null!;
    internal TableInfo Table = null!;
    internal Catalog Cat = null!;
    internal TextEncoding Enc;
    internal bool ForeignKeys;
    private VmProgram?[]? _programs;
    private string[]? _texts;

    internal bool HasWhen => Info.Ast.When is not null;

    internal int BodyCount => Info.Ast.Body.Length;

    /// <summary>The program at <paramref name="index"/>: -1 is the WHEN test (it returns a row when the trigger applies), 0.. the body statements.</summary>
    internal VmProgram Get(int index)
    {
        _programs ??= new VmProgram?[Info.Ast.Body.Length + 1];
        if (_programs[index + 1] is { } done)
        {
            return done;
        }

        CreateTriggerStatement ast = Info.Ast;
        string text;
        SqlStatement stmt;
        if (index == -1)
        {
            text = Rewrite("SELECT 1 WHERE (" + Info.Sql[ast.When!.Span.Start..ast.When.Span.End] + ")");
            stmt = SqlParser.ParseOne(text, 0, out _)!;
        }
        else
        {
            SqlStatement b = ast.Body[index];
            text = Rewrite(Info.Sql[b.Span.Start..b.Span.End]);
            stmt = SqlParser.ParseOne(text, 0, out _) ?? throw new OneLiteException(ResultCode.Error, "empty trigger statement");
        }

        var c = new Compiler(Cat, text, Enc) { ForeignKeysEnabled = ForeignKeys, FkLazy = true, InTrigger = true };
        VmProgram p = stmt switch
        {
            SelectStatement s => c.CompileSelect(s),
            InsertStatement i => c.CompileInsert(i),
            UpdateStatement u => c.CompileUpdate(u),
            DeleteStatement d => c.CompileDelete(d),
            _ => throw new OneLiteException(ResultCode.Error, "unsupported: trigger statement"),
        };
        p.SchemaCookie = Cat.Cookie;
        p.Encoding = Enc;
        _programs[index + 1] = p;
        _texts ??= [];
        return p;
    }

    private static string Unquote(string raw)
    {
        if (raw.Length >= 2)
        {
            char q = raw[0];
            if (q is '"' or '`')
            {
                return raw[1..^1].Replace(new string(q, 2), new string(q, 1), StringComparison.Ordinal);
            }

            if (q == '[')
            {
                return raw[1..^1];
            }
        }

        return raw;
    }

    // Replaces "new.col" and "old.col" with parameters. Table.Columns.Length + 1 slots per row image: the columns, then the rowid.
    private string Rewrite(string sql)
    {
        int n = Table.Columns.Length + 1;
        var sb = new StringBuilder();
        int pos = 0;
        int copied = 0;
        var toks = new List<Token>();
        for (; ; )
        {
            Token t = Lexer.Next(sql, pos);
            if (t.Kind == TokenKind.Eof)
            {
                break;
            }

            toks.Add(t);
            pos = t.End;
        }

        for (int i = 0; i < toks.Count; i++)
        {
            Token t = toks[i];
            if (t.Kind == TokenKind.Id && i + 2 < toks.Count && toks[i + 1].Kind == TokenKind.Dot && (i == 0 || toks[i - 1].Kind != TokenKind.Dot))
            {
                string q = sql[t.Start..t.End];
                bool isNew = q.Equals("new", StringComparison.OrdinalIgnoreCase);
                bool isOld = q.Equals("old", StringComparison.OrdinalIgnoreCase);
                if ((isNew && Info.Ast.Event != TriggerEvent.Delete) || (isOld && Info.Ast.Event != TriggerEvent.Insert))
                {
                    Token c = toks[i + 2];
                    string col = Unquote(sql[c.Start..c.End]);
                    int slot = Table.FindColumn(col);
                    if (slot < 0 && (col.Equals("rowid", StringComparison.OrdinalIgnoreCase) || col.Equals("oid", StringComparison.OrdinalIgnoreCase) || col.Equals("_rowid_", StringComparison.OrdinalIgnoreCase)))
                    {
                        slot = Table.Columns.Length;
                    }

                    if (slot < 0)
                    {
                        throw new OneLiteException(ResultCode.Error, "no such column: " + q + "." + col);
                    }

                    sb.Append(sql, copied, t.Start - copied).Append('?').Append((isNew ? n : 0) + slot + 1);
                    copied = c.End;
                    i += 2;
                }
            }
        }

        sb.Append(sql, copied, sql.Length - copied);
        return sb.ToString();
    }
}

internal sealed partial class Compiler
{
    /// <summary>True for the programs of trigger bodies: RAISE() is allowed and a missing table is reported as the trigger's statement finds it.</summary>
    internal bool InTrigger { get; set; }

    private VmProgram CompileCreateTrigger(CreateTriggerStatement ct)
    {
        PreassignParameters();
        string? schema = ct.Name.Schema?.Text;
        string name = ct.Name.Name.Text;
        string? tschema = ct.Table.Schema?.Text;
        string tn = ct.Table.Name.Text;
        if (ct.Temporary && schema is not null)
        {
            throw Err("temporary trigger may not have qualified name");
        }

        int named = schema is null ? -1 : CreateDb(schema, false);
        if (!ct.Temporary && named != 1 && tschema is not null && AsciiComparer.Instance.Equals(tschema, "temp"))
        {
            throw Err("trigger " + name + " cannot reference objects in database temp");
        }

        string? lookup = tschema ?? (named == 0 && !ct.Temporary ? "main" : null);
        TableInfo? t = _cat.FindIn(lookup, tn);

        // The trigger lives with its table, unless it is TEMP or named into temp.
        int db = ct.Temporary || named == 1 ? 1 : (named == 0 ? 0 : (t?.Db ?? 0));
        if (t is null)
        {
            throw Err("no such table: " + (tschema is not null ? tschema + "." : !ct.Temporary && named != 1 ? "main." : "") + tn);
        }

        if (db == 0 && t.Db == 1)
        {
            throw Err("trigger " + name + " cannot reference objects in database temp");
        }

        if ((db == 1 ? _cat.Triggers.Values.Any(x => x.Db == 1 && AsciiComparer.Instance.Equals(x.Name, name)) : _cat.Triggers.Values.Any(x => x.Db == 0 && AsciiComparer.Instance.Equals(x.Name, name))))
        {
            if (ct.IfNotExists)
            {
                return EmptyProgram();
            }

            throw Err("trigger " + name + " already exists");
        }

        if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
        {
            throw Err("object name reserved for internal use: " + name);
        }

        if (ReferenceEquals(t, _cat.MasterOf(t.Db)) || t.Unsupported is not null)
        {
            throw Err(t.Unsupported is not null ? "cannot create triggers on virtual tables" : "cannot create trigger on system table");
        }

        if (t.View is null && ct.Time == TriggerTime.InsteadOf)
        {
            throw Err("cannot create INSTEAD OF trigger on table: " + tn);
        }

        if (t.View is not null && ct.Time is TriggerTime.Before or TriggerTime.After or TriggerTime.Default)
        {
            throw Err("cannot create " + (ct.Time == TriggerTime.After ? "AFTER" : "BEFORE") + " trigger on view: " + tn);
        }

        foreach (Name uc in ct.UpdateColumns)
        {
            if (t.View is null && t.FindColumn(uc.Text) < 0)
            {
                throw Err("no such column: " + uc.Text);
            }
        }

        _forceTemp = db == 1;
        TableInfo master = _cat.MasterOf(db);
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
        Emit(Op.OpenWrite, mc);
        EmitSchemaRow(mc, master, "trigger", name, tn, 0, SchemaText("CREATE TRIGGER ", ct));
        Emit(Op.BumpCookie, db);
        Emit(Op.Halt);
        return DdlProgram();
    }

    private VmProgram CompileDropTrigger(DropStatement ds)
    {
        string name = ds.Name.Name.Text;
        string? schema = ds.Name.Schema?.Text;
        int sdb = NamedDb(schema, "trigger", name);
        TriggerInfo? tr = null;
        for (int d = 1; d >= 0 && tr is null; d--)
        {
            if (sdb < 0 || sdb == d)
            {
                tr = _cat.Triggers.Values.FirstOrDefault(x => x.Db == d && AsciiComparer.Instance.Equals(x.Name, name));
            }
        }

        if (tr is null)
        {
            if (ds.IfExists)
            {
                return EmptyProgram();
            }

            throw Err("no such trigger: " + (schema is null ? name : schema + "." + name));
        }

        _forceTemp = tr.Db == 1;
        TableInfo master = _cat.MasterOf(tr.Db);
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
        Emit(Op.OpenWrite, mc);
        Emit(Op.SchemaDelete, mc, 0, 3, name);
        Emit(Op.BumpCookie, tr.Db);
        Emit(Op.Halt);
        return DdlProgram();
    }

    private void CompileRaise(RaiseExpr r)
    {
        if (!InTrigger)
        {
            throw Err("RAISE() may only be used within a trigger-program");
        }

        if (r.Kind == RaiseKind.Ignore)
        {
            Emit(Op.TrigIgnore);
            return;
        }

        string msg = r.Message is LiteralExpr { } lit ? lit.Text : "";
        ErrorAction act = r.Kind switch { RaiseKind.Rollback => ErrorAction.Rollback, RaiseKind.Fail => ErrorAction.Fail, _ => ErrorAction.Abort };
        Emit(Op.Raise, 1811, 0, 0, msg, (int)act);
    }

    // ------------------------------------------------------------------ firing

    private static bool Fires(TriggerInfo tr, TriggerTime time, TriggerEvent ev, HashSet<int>? setCols, TableInfo t)
    {
        if (tr.Time != time || tr.Ast.Event != ev)
        {
            return false;
        }

        if (ev == TriggerEvent.Update && !tr.Ast.UpdateColumns.IsDefaultOrEmpty && setCols is not null)
        {
            return tr.Ast.UpdateColumns.Any(u =>
            {
                int ci = t.FindColumn(u.Text);
                return setCols.Contains(ci) || (ci == t.RowidAlias && ci >= 0 && setCols.Contains(-1));
            });
        }

        return true;
    }

    internal static bool HasTrigger(TableInfo t, TriggerTime time, TriggerEvent ev, HashSet<int>? setCols = null)
        => t.Triggers.Any(tr => Fires(tr, time, ev, setCols, t));

    /// <summary>Copies the row under the write cursor into registers: the columns, then the rowid. Returns the first register.</summary>
    private int CaptureOldRow(WriteCtx w)
    {
        TableInfo t = w.T;
        SetOldRowMode(w);
        int b = NewRegs(t.Columns.Length + 1);
        for (int i = 0; i < t.Columns.Length; i++)
        {
            if (i == t.RowidAlias)
            {
                Emit(Op.Rowid, w.Cursor, b + i);
            }
            else
            {
                CompileTableColumn(i, b + i);
            }
        }

        if (t.WithoutRowid)
        {
            Emit(Op.Null, 0, b + t.Columns.Length);
        }
        else
        {
            Emit(Op.Rowid, w.Cursor, b + t.Columns.Length);
        }

        return b;
    }

    /// <summary>The new row as registers in the layout of <see cref="CaptureOldRow"/>.</summary>
    private int CaptureNewRow(WriteCtx w)
    {
        TableInfo t = w.T;
        int b = NewRegs(t.Columns.Length + 1);
        for (int i = 0; i < t.Columns.Length; i++)
        {
            Emit(Op.Copy, w.ColReg[i], b + i);
            // lang_createtrigger: NEW.x already has the column's affinity applied when a BEFORE trigger reads it.
            ColumnInfo c = t.Columns[i];
            if (c.Affinity != Affinity.None && i != t.RowidAlias && c.Generated == GeneratedKind.None)
            {
                Emit(Op.Affinity, b + i, 0, 0, null, (int)c.Affinity);
            }
        }

        if (t.WithoutRowid)
        {
            Emit(Op.Null, 0, b + t.Columns.Length);
        }
        else
        {
            Emit(Op.Copy, w.RowidReg, b + t.Columns.Length);
        }

        return b;
    }

    /// <summary>Emits the TrigRun of every matching trigger. <paramref name="oldB"/> and <paramref name="newB"/> are row images (0 for none); RAISE(IGNORE) jumps to <paramref name="skip"/>.</summary>
    private void EmitTriggers(TableInfo t, TriggerTime time, TriggerEvent ev, int oldB, int newB, HashSet<int>? setCols, int skip)
    {
        int n = t.Columns.Length + 1;
        // observed: the most recently created trigger fires first
        foreach (TriggerInfo tr in Enumerable.Reverse(t.Triggers))
        {
            if (!Fires(tr, time, ev, setCols, t))
            {
                continue;
            }

            tr.Spec ??= new TrigSpec { Info = tr, Table = t, Cat = _cat, Enc = _enc, ForeignKeys = ForeignKeysEnabled };
            int pb = NewRegs(2 * n);
            for (int i = 0; i < n; i++)
            {
                if (oldB != 0)
                {
                    Emit(Op.Copy, oldB + i, pb + i);
                }
                else
                {
                    Emit(Op.Null, 0, pb + i);
                }

                if (newB != 0)
                {
                    Emit(Op.Copy, newB + i, pb + n + i);
                }
                else
                {
                    Emit(Op.Null, 0, pb + n + i);
                }
            }

            Emit(Op.TrigRun, 0, skip, pb, tr.Spec, 2 * n);
        }
    }
}
