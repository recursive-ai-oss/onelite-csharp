using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// The table a write statement changes, as one FROM item of its own scope, so that expressions in WHERE, SET, CHECK and RETURNING resolve names
// the way a SELECT's do (subqueries included). While a row is being built its columns live in registers (_colRegs) instead of under a cursor.
internal sealed partial class Compiler
{
    private Source? _dmlSource;
    private Scope? _dmlScope;

    private TableInfo? _table
    {
        get => _dmlSource?.Table;
        set
        {
            if (value is null)
            {
                _dmlSource = null;
                _dmlScope = null;
                return;
            }

            _dmlSource = new Source { Table = value, Kind = SourceKind.Table, Alias = value.Name, Cursor = -1 };
            _dmlScope = new Scope { Parent = null, Depth = 0 };
            _dmlScope.Sources.Add(_dmlSource);
        }
    }

    private string _alias
    {
        get => _dmlSource?.Alias ?? "";
        set
        {
            if (_dmlSource is not null)
            {
                _dmlSource.Alias = value;
            }
        }
    }

    private int _tableCursor
    {
        get => _dmlSource?.Cursor ?? -1;
        set
        {
            if (_dmlSource is not null)
            {
                _dmlSource.Cursor = value;
            }
        }
    }

    /// <summary>Whether column names of the target table resolve (not in the VALUES of an INSERT, which reads no row).</summary>
    private void SetColumns(bool on)
    {
        _columnsAllowed = on;
        _scope = on ? _dmlScope : null;
    }

    /// <summary>Makes the statement's target table the FROM item the expressions of the statement resolve against.</summary>
    private Source BindTarget(NamedTable nt)
    {
        string name = nt.Name.Name.Text;
        TableInfo t = _cat.FindIn(nt.Name.Schema?.Text, name) ?? throw Err("no such table: " + (nt.Name.Schema is { } sc ? sc.Text + "." : "") + name);
        _cursors.Add(new CursorSpec { Table = t, Index = t.WithoutRowid ? t.PrimaryKey : null, IsIndexTree = t.WithoutRowid, Root = t.Root });
        _table = t;
        _alias = nt.Alias?.Text ?? t.Name;
        _tableCursor = _cursors.Count - 1;
        SetColumns(true);
        return _dmlSource!;
    }

    /// <summary>Reads a column of the target: from the row's registers while one is being built, else from the cursor it stands on.</summary>
    private void CompileTableColumn(int col, int dest)
    {
        if (_colRegs != null)
        {
            // While a row is being written its columns live in registers, not in a cursor.
            Emit(Op.Copy, col == _table!.RowidAlias || col < 0 ? _rowidReg : _colRegs[col], dest);
            return;
        }

        CompileTableColumn(_dmlSource!, col, dest);
    }
}
