using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Vm;

// Virtual-table instructions (see docs/vtab.md). Each one that can wait for a page returns Pending without moving pc and is run again with
// the same registers; the module keeps its own progress.
internal sealed partial class Machine
{
    private RunResult DoVirtual(ref Instr ins)
    {
        if (ins.Op != Op.VtFilter)
        {
            _conn.ConcOpaque("writes a virtual table");
        }

        Mem[] r = R;
        switch (ins.Op)
        {
            case Op.VtFilter:
            {
                VmCursor c = _cur[ins.P1];
                c.VCur ??= _conn.ConnectVirtual(c.Spec.Table).Open();
                var fs = (VtFilterSpec)ins.P4!;
                c.NullRow = false;
                if (c.VCur.Filter(fs.IndexNumber, fs.IndexString, new ReadOnlySpan<Mem>(r, ins.P3, ins.P5)) == ModStatus.Pending)
                {
                    return RunResult.Pending;
                }

                _pc = c.VCur.Eof ? ins.P2 : _pc + 1;
                return RunResult.Done;
            }

            case Op.VtUpdate:
            {
                VTab v = _conn.ConnectVirtual((TableInfo)ins.P4!);
                _conn.VtabBegin(v);
                if (v.Update(new ReadOnlySpan<Mem>(r, ins.P1, ins.P2), out long rowid) == ModStatus.Pending)
                {
                    return RunResult.Pending;
                }

                Values.SetInt(ref r[ins.P3], rowid);
                if ((ins.P5 & 1) != 0)
                {
                    _changes++;
                }

                if ((ins.P5 & 2) != 0)
                {
                    _conn.LastInsertRowid = rowid;
                }

                _pc++;
                return RunResult.Done;
            }

            case Op.VtCreate:
            {
                var spec = (VtCreateSpec)ins.P4!;
                IVirtualTableModule module = _conn.FindUserModule(spec.Module) ?? throw new OneLiteException(ResultCode.Error, "no such module: " + spec.Module);
                bool declared = false;
                var ctx = new VirtualTableContext(_conn, spec.Schema, spec.Name, spec.Args, sql =>
                {
                    var probe = new TableInfo { Name = spec.Name, Virtual = new VirtualInfo() };
                    Connection.DeclareColumns(probe, sql);
                    declared = true;
                });
                IVirtualTable inst = module.Create(ctx);
                inst.Dispose();
                if (!declared)
                {
                    throw new OneLiteException(ResultCode.Error, "vtable constructor did not declare schema: " + spec.Name);
                }

                _pc++;
                return RunResult.Done;
            }

            default:
            {
                // VtDestroy
                var t = (TableInfo)ins.P4!;
                VTab v = _conn.ConnectVirtual(t);
                v.Destroy();
                t.Virtual!.Instance = null;
                v.Dispose();
                _pc++;
                return RunResult.Done;
            }
        }
    }
}
