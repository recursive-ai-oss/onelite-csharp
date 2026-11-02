using System.Collections.Immutable;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite;

public sealed partial class Connection
{
    private readonly Dictionary<string, IVirtualTableModule> _userModules = new(AsciiComparer.Instance);

    /// <summary>Tables that began a transaction on this connection and still owe it a Sync/Commit or Rollback, in the order they began.</summary>
    internal readonly List<VTab> VtabsInTx = [];

    /// <summary>Savepoint depth as the modules number it (1 for the outermost).</summary>
    internal int VtabSavepointDepth;

    private bool _vtabSynced;

    /// <summary>
    /// Registers a virtual-table module under <paramref name="name"/> (case-insensitive) for <c>CREATE VIRTUAL TABLE ... USING name(...)</c> on this
    /// connection. A later registration under the same name replaces the earlier one. The built-in modules (<c>rtree</c>, <c>rtree_i32</c>,
    /// <c>dbstat</c>) cannot be replaced.
    /// </summary>
    /// <param name="name">The module name.</param>
    /// <param name="module">The module.</param>
    /// <exception cref="ArgumentException">The name is empty or names a built-in module.</exception>
    public void RegisterModule(string name, IVirtualTableModule module)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(module);
        if (VirtualModules.FindBuiltin(name) is not null)
        {
            throw new ArgumentException("the module name is taken by a built-in module", nameof(name));
        }

        _userModules[name] = module;
    }

    internal IVirtualTableModule? FindUserModule(string name) => _userModules.GetValueOrDefault(name);

    /// <summary>
    /// Makes sure the virtual table <paramref name="t"/> is connected: its module has declared the columns and its instance exists.
    /// Throws "no such module: NAME" when the module is not registered.
    /// </summary>
    internal VTab ConnectVirtual(TableInfo t)
    {
        VirtualInfo v = t.Virtual!;
        if (v.Instance is { } existing)
        {
            return existing;
        }

        ImmutableArray<string> args = v.Args;
        string schema = v.Schema;
        Action<string> declare = sql => DeclareColumns(t, sql);
        VTab inst;
        if (VirtualModules.FindBuiltin(v.Module) is { } b)
        {
            inst = b.Connect(this, t, schema, args, declare);
        }
        else if (FindUserModule(v.Module) is { } um)
        {
            var ctx = new VirtualTableContext(this, schema, t.Name, args, declare);
            inst = new UserVTab(um.Connect(ctx));
        }
        else
        {
            throw new OneLiteException(ResultCode.Error, "no such module: " + v.Module);
        }

        if (!v.Declared)
        {
            inst.Dispose();
            throw new OneLiteException(ResultCode.Error, "vtable constructor did not declare schema: " + t.Name);
        }

        inst.Table = t;
        v.Instance = inst;
        return inst;
    }

    /// <summary>Parses the module's declared <c>CREATE TABLE</c> and installs the columns on the table.</summary>
    internal static void DeclareColumns(TableInfo t, string sql)
    {
        Sql.Statement? st;
        try
        {
            st = SqlParser.ParseOne(sql, 0, out _);
        }
        catch (OneLiteException e)
        {
            throw new OneLiteException(ResultCode.Error, e.Message, e);
        }

        if (st is not CreateTableStatement ct)
        {
            throw new OneLiteException(ResultCode.Error, "declared schema is not a CREATE TABLE statement");
        }

        TableInfo built = SchemaBuilder.BuildTable(ct, 0, sql);
        for (int i = 0; i < ct.Columns.Length; i++)
        {
            if (ct.Columns[i].Type is { } ty && ty.Words.Length > 0 && string.Equals(ty.Words[^1], "hidden", StringComparison.OrdinalIgnoreCase))
            {
                built.Columns[i].Hidden = true;
            }
        }

        t.Columns = built.Columns;
        t.RecordWidth = built.RecordWidth;
        t.RowidAlias = -1;
        t.Virtual!.Declared = true;
    }

    /// <summary>Calls Begin on a table the first time a statement writes to it in the current transaction.</summary>
    internal void VtabBegin(VTab v)
    {
        if (!v.WantsTransactions || VtabsInTx.Contains(v))
        {
            return;
        }

        v.Begin();
        VtabsInTx.Add(v);
    }

    /// <summary>First phase of commit for the tables that began a transaction.</summary>
    internal void VtabSync()
    {
        if (_vtabSynced)
        {
            return;
        }

        _vtabSynced = true;
        foreach (VTab v in VtabsInTx)
        {
            v.Sync();
        }
    }

    /// <summary>Second phase of commit.</summary>
    internal void VtabCommit()
    {
        _vtabSynced = false;
        if (VtabsInTx.Count == 0)
        {
            return;
        }

        VTab[] list = [.. VtabsInTx];
        VtabsInTx.Clear();
        VtabSavepointDepth = 0;
        foreach (VTab v in list)
        {
            v.Commit();
        }
    }

    /// <summary>Rollback of the whole transaction.</summary>
    internal void VtabRollback()
    {
        _vtabSynced = false;
        if (VtabsInTx.Count == 0)
        {
            return;
        }

        VTab[] list = [.. VtabsInTx];
        VtabsInTx.Clear();
        VtabSavepointDepth = 0;
        foreach (VTab v in list)
        {
            v.Rollback();
        }
    }

    internal void VtabSavepoint(int level)
    {
        foreach (VTab v in VtabsInTx)
        {
            if (v.WantsSavepoints)
            {
                v.Savepoint(level);
            }
        }
    }

    internal void VtabRelease(int level)
    {
        foreach (VTab v in VtabsInTx)
        {
            if (v.WantsSavepoints)
            {
                v.Release(level);
            }
        }
    }

    internal void VtabRollbackTo(int level)
    {
        foreach (VTab v in VtabsInTx)
        {
            if (v.WantsSavepoints)
            {
                v.RollbackTo(level);
            }
        }
    }

    /// <summary>Drops the connected instances of a catalog that is being thrown away (the schema changed).</summary>
    internal void DisconnectVirtuals(Catalog? old)
    {
        if (old is null)
        {
            return;
        }

        foreach (TableInfo t in old.Tables.Values)
        {
            if (t.Virtual is { Instance: { } inst } v && !VtabsInTx.Contains(inst))
            {
                v.Instance = null;
                inst.Dispose();
            }
        }
    }
}
