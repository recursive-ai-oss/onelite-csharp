using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;
using SqlStatement = RecursiveAI.OneLite.Sql.Statement;

namespace RecursiveAI.OneLite.Planning;

// Transaction control statements (lang_transaction, lang_savepoint). Each is a tiny program; the work is in Machine.DoTxControl.
internal sealed partial class Compiler
{
    private VmProgram Finish0(int regs)
    {
        Emit(Op.Halt);
        return new VmProgram { Code = Finish(), RegisterCount = regs, Cursors = [.. _cursors], ParameterNames = [.. _params], Encoding = _enc };
    }

    internal VmProgram CompileControl(SqlStatement stmt)
    {
        switch (stmt)
        {
            case BeginStatement b:
                if (b.Mode == BeginMode.Concurrent)
                {
                    throw Unsupported("BEGIN CONCURRENT");
                }

                if (b.Mode is BeginMode.Immediate or BeginMode.Exclusive)
                {
                    // IMMEDIATE takes the write lock now (lockingv3 RESERVED / the WAL write lock). EXCLUSIVE also takes EXCLUSIVE in rollback mode
                    // (bit 16), which keeps readers out; in WAL mode it is IMMEDIATE. The lock comes first, as in the reference: when it is busy
                    // the statement fails with SQLITE_BUSY and the connection stays in autocommit mode, and inside a transaction a busy lock is
                    // reported before "cannot start a transaction within a transaction".
                    Emit(Op.Transaction, 0, 0, 0, null, 1 | 4 | (b.Mode == BeginMode.Exclusive ? 16 : 0));
                }

                Emit(Op.TxControl, 0);

                return Finish0(1);
            case CommitStatement:
                Emit(Op.TxControl, 1);
                return Finish0(1);
            case RollbackStatement { ToSavepoint: { } sp }:
                Emit(Op.TxControl, 5, 0, 0, sp.Text);
                return Finish0(1);
            case RollbackStatement:
                Emit(Op.TxControl, 2);
                return Finish0(1);
            case SavepointStatement s:
                Emit(Op.TxControl, 3, 0, 0, s.Savepoint.Text);
                return Finish0(1);
            case ReleaseStatement r:
                Emit(Op.TxControl, 4, 0, 0, r.Savepoint.Text);
                return Finish0(1);
            default:
                throw Unsupported("statement");
        }
    }
}
