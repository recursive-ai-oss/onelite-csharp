using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;
using SqlStatement = RecursiveAI.OneLite.Sql.Statement;

namespace RecursiveAI.OneLite.Planning;

// ATTACH and DETACH (lang_attach, lang_detach). The file name, schema name and key are expressions evaluated when the statement runs;
// a bare word counts as a string, as in "ATTACH 'a.db' AS aux".
internal sealed partial class Compiler
{
    private int CompileNameExpr(Expr e)
    {
        if (e is ColumnRef { Schema: null, Table: null } cr)
        {
            int r = NewReg();
            Emit(Op.String8, 0, r, 0, System.Text.Encoding.UTF8.GetBytes(cr.Column.Text));
            return r;
        }

        return Compile(e);
    }

    internal VmProgram CompileAttach(SqlStatement stmt)
    {
        switch (stmt)
        {
            case AttachStatement a:
            {
                int file = CompileNameExpr(a.Database);
                int name = CompileNameExpr(a.SchemaName);
                int key = a.Key is null ? 0 : CompileNameExpr(a.Key);
                Emit(Op.AttachDb, file, name, key, null, a.Key is null ? 0 : 1);
                break;
            }

            case DetachStatement d:
                Emit(Op.DetachDb, CompileNameExpr(d.SchemaName));
                break;
            default:
                throw Unsupported("statement");
        }

        return Finish0(_nReg + 1);
    }
}
