using System.Text;
using SQLitePCL;

namespace RecursiveAI.OneLite.SQLitePCLRaw;

public sealed partial class OneLiteSqlite3Provider
{
    // ------------------------------------------------------------------ prepare

    /// <inheritdoc />
    public int sqlite3_prepare_v2(sqlite3 db, ReadOnlySpan<byte> sql, out IntPtr stmt, out ReadOnlySpan<byte> remain)
        => Prepare(db, sql, out stmt, out remain);

    /// <inheritdoc />
    public int sqlite3_prepare_v3(sqlite3 db, ReadOnlySpan<byte> sql, uint flags, out IntPtr stmt, out ReadOnlySpan<byte> remain)
        => Prepare(db, sql, out stmt, out remain);

    /// <inheritdoc />
    public int sqlite3_prepare_v2(sqlite3 db, utf8z sql, out IntPtr stmt, out utf8z remain)
        => PrepareZ(db, sql, out stmt, out remain);

    /// <inheritdoc />
    public int sqlite3_prepare_v3(sqlite3 db, utf8z sql, uint flags, out IntPtr stmt, out utf8z remain)
        => PrepareZ(db, sql, out stmt, out remain);

    private static int PrepareZ(sqlite3 db, utf8z sql, out IntPtr stmt, out utf8z remain)
    {
        string text = sql.utf8_to_string() ?? string.Empty;
        int rc = Prepare(db, Utf8Z.Terminated(Encoding.UTF8.GetBytes(text)), out stmt, out ReadOnlySpan<byte> t);
        remain = utf8z.FromSpan(t);
        return rc;
    }

    private static int Prepare(sqlite3 db, ReadOnlySpan<byte> sql, out IntPtr stmt, out ReadOnlySpan<byte> remain)
    {
        stmt = IntPtr.Zero;
        remain = default;
        DbState? d = Db(db);
        if (d is null || d.Closed || d.Connection is null)
        {
            return ResultCode.Misuse;
        }

        int nul = sql.IndexOf((byte)0);
        ReadOnlySpan<byte> text = nul >= 0 ? sql[..nul] : sql;
        if (text.Length > d.Limits[1])
        {
            return d.Fail(ResultCode.TooBig, "statement too long");
        }

        string s = Encoding.UTF8.GetString(text);
        Statement st;
        try
        {
            st = d.Conn.Prepare(s);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }

        // The engine reports the unconsumed text; the caller wants it as a position in its own bytes.
        string rest = st.Tail;
        string consumed = rest.Length <= s.Length ? s[..(s.Length - rest.Length)] : s;
        // SQLitePCLRaw reads the tail up to its zero terminator: hand back the caller's own bytes when they end with one, else a terminated copy.
        int consumedBytes = Encoding.UTF8.GetByteCount(consumed);
        remain = nul >= 0 ? sql[consumedBytes..] : Utf8Z.Terminated(text[consumedBytes..]);
        d.Ok();
        if (IsBlank(consumed))
        {
            // Only whitespace, comments and semicolons: SQLite returns OK and a NULL statement.
            st.Dispose();
            return 0;
        }

        var state = new StmtState { Db = d, Statement = st, Sql = consumed };
        state.Id = Handles.Add(state);
        // A statement handle can be released by the finalizer thread, so the list is guarded.
        lock (d.Statements)
        {
            d.Statements.Add(state);
        }

        stmt = state.Id;
        return 0;
    }

    private static bool IsBlank(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or ';' or '\0')
            {
                i++;
            }
            else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                int nl = s.IndexOf('\n', i);
                i = nl < 0 ? s.Length : nl + 1;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>c3ref complete: true if the text ends a statement with a semicolon outside quotes and comments (a trigger body counts only up to its END).</summary>
    private static bool IsComplete(string s)
    {
        bool lastSemicolon = false;
        bool inTrigger = false;
        bool seenCreate = false;
        bool seenTemp = false;
        bool seenTrigger = false;
        bool beginSeen = false;
        string prevWord = string.Empty;
        int i = 0;
        bool any = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                int nl = s.IndexOf('\n', i);
                i = nl < 0 ? s.Length : nl + 1;
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                i = end + 2;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                int j = i + 1;
                for (; ; )
                {
                    if (j >= s.Length)
                    {
                        return false;
                    }

                    if (s[j] == c)
                    {
                        if (j + 1 < s.Length && s[j + 1] == c)
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    j++;
                }

                i = j + 1;
                lastSemicolon = false;
                any = true;
                continue;
            }

            if (c == '[')
            {
                int j = s.IndexOf(']', i + 1);
                if (j < 0)
                {
                    return false;
                }

                i = j + 1;
                lastSemicolon = false;
                any = true;
                continue;
            }

            if (c == ';')
            {
                lastSemicolon = !inTrigger || prevWord.Equals("END", StringComparison.OrdinalIgnoreCase);
                if (lastSemicolon)
                {
                    inTrigger = false;
                    seenCreate = seenTemp = seenTrigger = beginSeen = false;
                }

                prevWord = string.Empty;
                i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '$'))
                {
                    j++;
                }

                string w = s[i..j];
                if (!any)
                {
                    seenCreate = w.Equals("CREATE", StringComparison.OrdinalIgnoreCase);
                }
                else if (seenCreate && !seenTrigger && (w.Equals("TEMP", StringComparison.OrdinalIgnoreCase) || w.Equals("TEMPORARY", StringComparison.OrdinalIgnoreCase)))
                {
                    seenTemp = true;
                }
                else if (seenCreate && w.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
                {
                    seenTrigger = true;
                }
                else if (seenTrigger && w.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    inTrigger = true;
                    beginSeen = true;
                }

                prevWord = w;
                lastSemicolon = false;
                any = true;
                i = j;
                continue;
            }

            lastSemicolon = false;
            any = true;
            prevWord = string.Empty;
            i++;
        }

        _ = seenTemp;
        _ = beginSeen;
        return lastSemicolon;
    }

    // ------------------------------------------------------------------ step, reset, finalize

    /// <inheritdoc />
    public int sqlite3_step(sqlite3_stmt stmt) => StepCore(Stmt(stmt));

    private static int StepCore(StmtState? s)
    {
        if (s is null || s.Finalized || s.Db.Closed)
        {
            return ResultCode.Misuse;
        }

        DbState d = s.Db;
        try
        {
            if (s.Db.ProgressEvery > 0 && s.Db.Progress is { } progress && progress(s.Db.ProgressUser) != 0)
            {
                throw new OneLiteException(ResultCode.Interrupt, "interrupted");
            }

            bool row = s.Statement.Step();
            s.HasRow = row;
            s.Active = row;
            s.StepError = 0;
            s.StepMessage = null;
            d.Code = row ? Row : Done;
            d.ExtCode = d.Code;
            d.Message = null;
            return d.Code;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            s.HasRow = false;
            s.Active = false;
            int rc = d.Fail(e);
            s.StepError = d.ExtCode;
            s.StepMessage = d.Message;
            return rc;
        }
    }

    private static int ResetState(StmtState s)
    {
        DbState d = s.Db;
        int pending = s.StepError;
        string? message = s.StepMessage;
        s.StepError = 0;
        s.StepMessage = null;
        s.HasRow = false;
        s.Active = false;
        if (!s.Finalized && !d.Closed)
        {
            try
            {
                s.Statement.Reset();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return d.Fail(e);
            }
        }

        if (pending != 0)
        {
            return d.Fail(pending, message ?? ErrorString(pending));
        }

        return d.Ok();
    }

    /// <inheritdoc />
    public int sqlite3_reset(sqlite3_stmt stmt)
    {
        StmtState? s = Stmt(stmt);
        return s is null ? ResultCode.Misuse : ResetState(s);
    }

    private static int FinalizeStatement(StmtState s)
    {
        if (s.Finalized)
        {
            return 0;
        }

        int rc = s.StepError;
        s.Finalized = true;
        s.HasRow = false;
        s.Active = false;
        lock (s.Db.Statements)
        {
            s.Db.Statements.Remove(s);
        }

        try
        {
            s.Statement.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            rc = rc != 0 ? rc : (e as OneLiteException)?.ExtendedCode ?? ResultCode.Error;
        }

        Handles.Remove(s.Id);
        if (rc != 0 && !s.Db.Closed)
        {
            s.Db.Fail(rc, s.StepMessage ?? ErrorString(rc));
        }

        return s.Db.Return(rc);
    }

    /// <inheritdoc />
    public int sqlite3_finalize(IntPtr stmt)
    {
        StmtState? s = Handles.Get<StmtState>(stmt);
        return s is null ? 0 : FinalizeStatement(s);
    }

    /// <inheritdoc />
    public int sqlite3_clear_bindings(sqlite3_stmt stmt)
    {
        StmtState? s = Stmt(stmt);
        if (s is null)
        {
            return ResultCode.Misuse;
        }

        s.Statement.ClearBindings();
        return 0;
    }

    /// <inheritdoc />
    public int sqlite3_stmt_status(sqlite3_stmt stmt, int op, int resetFlg) => 0;

    /// <inheritdoc />
    public utf8z sqlite3_sql(sqlite3_stmt stmt) => Stmt(stmt) is { } s ? utf8z.FromString(s.Sql) : default;

    /// <inheritdoc />
    public IntPtr sqlite3_db_handle(IntPtr stmt) => Handles.Get<StmtState>(stmt)?.Db.Id ?? IntPtr.Zero;

    /// <inheritdoc />
    public IntPtr sqlite3_next_stmt(sqlite3 db, IntPtr stmt)
    {
        DbState? d = Db(db);
        if (d is null)
        {
            return IntPtr.Zero;
        }

        if (stmt == IntPtr.Zero)
        {
            lock (d.Statements)
            {
                return d.Statements.Count > 0 ? d.Statements[0].Id : IntPtr.Zero;
            }
        }

        StmtState? cur = Handles.Get<StmtState>(stmt);
        lock (d.Statements)
        {
            int i = cur is null ? -1 : d.Statements.IndexOf(cur);
            return i >= 0 && i + 1 < d.Statements.Count ? d.Statements[i + 1].Id : IntPtr.Zero;
        }
    }

    /// <inheritdoc />
    public int sqlite3_stmt_isexplain(sqlite3_stmt stmt)
    {
        string sql = Stmt(stmt)?.Sql.TrimStart() ?? string.Empty;
        if (sql.StartsWith("EXPLAIN QUERY PLAN", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return sql.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <inheritdoc />
    public int sqlite3_stmt_busy(sqlite3_stmt stmt) => Stmt(stmt)?.Active == true ? 1 : 0;

    /// <inheritdoc />
    public int sqlite3_stmt_readonly(sqlite3_stmt stmt) => Stmt(stmt)?.Statement.IsReadOnly == true ? 1 : 0;

    /// <inheritdoc />
    public int sqlite3_exec(sqlite3 db, utf8z sql, delegate_exec callback, object user_data, out IntPtr errMsg)
    {
        errMsg = IntPtr.Zero;
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        int result = ExecText(d, db, sql.utf8_to_string() ?? string.Empty, callback, user_data);
        if (result != 0 && d.Message is { } message)
        {
            // The caller frees this with sqlite3_free.
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            errMsg = sqlite3_malloc_static(bytes.Length + 1);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, errMsg, bytes.Length);
            System.Runtime.InteropServices.Marshal.WriteByte(errMsg, bytes.Length, 0);
        }

        return result;
    }

    private static IntPtr NativeUtf8(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        IntPtr p = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length + 1);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, p, bytes.Length);
        System.Runtime.InteropServices.Marshal.WriteByte(p, bytes.Length, 0);
        return p;
    }

    private static IntPtr sqlite3_malloc_static(int n) => System.Runtime.InteropServices.Marshal.AllocHGlobal(Math.Max(n, 1));

    private static int ExecText(DbState d, sqlite3 db, string text, delegate_exec? callback, object? user)
    {
        while (!IsBlank(text))
        {
            int rc = Prepare(db, Encoding.UTF8.GetBytes(text), out IntPtr h, out ReadOnlySpan<byte> tail);
            string rest = Encoding.UTF8.GetString(tail);
            if (rc != 0)
            {
                return rc;
            }

            if (h != IntPtr.Zero)
            {
                StmtState s = Handles.Get<StmtState>(h)!;
                bool aborted = false;
                do
                {
                    rc = StepCore(s);
                    if (rc == Row && callback is not null)
                    {
                        int n = s.Statement.ColumnCount;
                        var values = new IntPtr[n];
                        var names = new IntPtr[n];
                        try
                        {
                            for (int i = 0; i < n; i++)
                            {
                                names[i] = NativeUtf8(s.Statement.ColumnName(i));
                                values[i] = s.Statement.IsNull(i) ? IntPtr.Zero : NativeUtf8(s.Statement.GetString(i)!);
                            }

                            if (callback(user!, values, names) != 0)
                            {
                                aborted = true;
                                break;
                            }
                        }
                        finally
                        {
                            foreach (IntPtr p in values)
                            {
                                System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
                            }

                            foreach (IntPtr p in names)
                            {
                                System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
                            }
                        }
                    }
                }
                while (rc == Row);
                int fin = FinalizeStatement(s);
                if (aborted)
                {
                    return d.Fail(ResultCode.Abort, "query aborted");
                }

                if (rc != Done)
                {
                    return rc;
                }

                if (fin != 0)
                {
                    return fin;
                }
            }

            text = rest;
        }

        return d.Ok();
    }

    // ------------------------------------------------------------------ binding

    private static int BindFail(StmtState s, Exception e) => s.Db.Fail(e);

    private static bool InRange(StmtState s, int index) => index >= 1 && index <= s.Statement.ParameterCount;

    private static int Bind(sqlite3_stmt stmt, int index, Action<Statement> bind)
    {
        StmtState? s = Stmt(stmt);
        if (s is null || s.Finalized)
        {
            return ResultCode.Misuse;
        }

        if (s.Active)
        {
            // c3ref bind: a statement that has not been reset since it returned a row is still running
            return ResultCode.Misuse;
        }

        if (!InRange(s, index))
        {
            return s.Db.Fail(25, "column index out of range");
        }

        try
        {
            bind(s.Statement);
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return BindFail(s, e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_bind_zeroblob(sqlite3_stmt stmt, int index, int size)
    {
        if (Stmt(stmt) is { } s && size > s.Db.Limits[0])
        {
            return s.Db.Fail(ResultCode.TooBig, "string or blob too big");
        }

        return Bind(stmt, index, st => st.Bind(index, new byte[Math.Max(size, 0)]));
    }

    /// <inheritdoc />
    public utf8z sqlite3_bind_parameter_name(sqlite3_stmt stmt, int index)
    {
        StmtState? s = Stmt(stmt);
        if (s is null || !InRange(s, index))
        {
            return default;
        }

        string? n = s.Statement.ParameterName(index);
        return n is null ? default : utf8z.FromString(n);
    }

    /// <inheritdoc />
    public int sqlite3_bind_blob(sqlite3_stmt stmt, int index, ReadOnlySpan<byte> blob)
    {
        byte[] copy = blob.ToArray();
        return Bind(stmt, index, st => st.Bind(index, copy));
    }

    /// <inheritdoc />
    public int sqlite3_bind_double(sqlite3_stmt stmt, int index, double val)
        => Bind(stmt, index, st =>
        {
            // SQLite stores a NaN as NULL.
            if (double.IsNaN(val))
            {
                st.BindNull(index);
            }
            else
            {
                st.Bind(index, val);
            }
        });

    /// <inheritdoc />
    public int sqlite3_bind_int(sqlite3_stmt stmt, int index, int val) => Bind(stmt, index, st => st.Bind(index, (long)val));

    /// <inheritdoc />
    public int sqlite3_bind_int64(sqlite3_stmt stmt, int index, long val) => Bind(stmt, index, st => st.Bind(index, val));

    /// <inheritdoc />
    public int sqlite3_bind_null(sqlite3_stmt stmt, int index) => Bind(stmt, index, st => st.BindNull(index));

    /// <inheritdoc />
    public int sqlite3_bind_text(sqlite3_stmt stmt, int index, ReadOnlySpan<byte> text)
    {
        if (Stmt(stmt) is { } s && text.Length > s.Db.Limits[0])
        {
            return s.Db.Fail(ResultCode.TooBig, "string or blob too big");
        }

        string str = Encoding.UTF8.GetString(text);
        return Bind(stmt, index, st => st.Bind(index, str));
    }

    /// <inheritdoc />
    public int sqlite3_bind_text16(sqlite3_stmt stmt, int index, ReadOnlySpan<char> text)
    {
        string str = new(text);
        return Bind(stmt, index, st => st.Bind(index, str));
    }

    /// <inheritdoc />
    public int sqlite3_bind_text(sqlite3_stmt stmt, int index, utf8z text)
    {
        string? str = text.utf8_to_string();
        return str is null ? Bind(stmt, index, st => st.BindNull(index)) : Bind(stmt, index, st => st.Bind(index, str));
    }

    /// <inheritdoc />
    public int sqlite3_bind_parameter_count(sqlite3_stmt stmt) => Stmt(stmt)?.Statement.ParameterCount ?? 0;

    /// <inheritdoc />
    public int sqlite3_bind_parameter_index(sqlite3_stmt stmt, utf8z strName)
        => Stmt(stmt)?.Statement.ParameterIndex(strName.utf8_to_string() ?? string.Empty) ?? 0;

    // ------------------------------------------------------------------ result columns

    private static bool Has(StmtState? s, int i) => s is { HasRow: true, Finalized: false } && (uint)i < (uint)s.Statement.ColumnCount;

    /// <inheritdoc />
    public utf8z sqlite3_column_database_name(sqlite3_stmt stmt, int index)
        => Stmt(stmt) is { } s && (uint)index < (uint)s.Statement.ColumnCount && s.Statement.ColumnDatabaseName(index) is { } v ? utf8z.FromString(v) : default;

    /// <inheritdoc />
    public utf8z sqlite3_column_name(sqlite3_stmt stmt, int index)
    {
        StmtState? s = Stmt(stmt);
        return s is not null && (uint)index < (uint)s.Statement.ColumnCount ? utf8z.FromString(s.Statement.ColumnName(index)) : default;
    }

    /// <inheritdoc />
    public utf8z sqlite3_column_origin_name(sqlite3_stmt stmt, int index)
        => Stmt(stmt) is { } s && (uint)index < (uint)s.Statement.ColumnCount && s.Statement.ColumnOriginName(index) is { } v ? utf8z.FromString(v) : default;

    /// <inheritdoc />
    public utf8z sqlite3_column_table_name(sqlite3_stmt stmt, int index)
        => Stmt(stmt) is { } s && (uint)index < (uint)s.Statement.ColumnCount && s.Statement.ColumnTableName(index) is { } v ? utf8z.FromString(v) : default;

    /// <inheritdoc />
    public utf8z sqlite3_column_text(sqlite3_stmt stmt, int index)
    {
        StmtState? s = Stmt(stmt);
        if (!Has(s, index) || s!.Statement.IsNull(index))
        {
            return default;
        }

        if (s.TextBuffers.Length != s.Statement.ColumnCount)
        {
            s.TextBuffers = new byte[]?[s.Statement.ColumnCount];
        }

        return Utf8Z.Reuse(ref s.TextBuffers[index], s.Statement.GetTextUtf8(index));
    }

    /// <inheritdoc />
    public int sqlite3_data_count(sqlite3_stmt stmt) => Stmt(stmt) is { HasRow: true } s ? s.Statement.ColumnCount : 0;

    /// <inheritdoc />
    public int sqlite3_column_count(sqlite3_stmt stmt) => Stmt(stmt)?.Statement.ColumnCount ?? 0;

    /// <inheritdoc />
    public double sqlite3_column_double(sqlite3_stmt stmt, int index) => Stmt(stmt) is { } s && Has(s, index) ? s.Statement.GetDouble(index) : 0;

    /// <inheritdoc />
    public int sqlite3_column_int(sqlite3_stmt stmt, int index) => Stmt(stmt) is { } s && Has(s, index) ? unchecked((int)s.Statement.GetInt64(index)) : 0;

    /// <inheritdoc />
    public long sqlite3_column_int64(sqlite3_stmt stmt, int index) => Stmt(stmt) is { } s && Has(s, index) ? s.Statement.GetInt64(index) : 0;

    /// <inheritdoc />
    public ReadOnlySpan<byte> sqlite3_column_blob(sqlite3_stmt stmt, int index)
    {
        StmtState? s = Stmt(stmt);
        return Has(s, index) ? s!.Statement.GetBlob(index) : default;
    }

    /// <inheritdoc />
    public int sqlite3_column_bytes(sqlite3_stmt stmt, int index)
    {
        StmtState? s = Stmt(stmt);
        return Has(s, index) ? s!.Statement.GetBlob(index).Length : 0;
    }

    /// <inheritdoc />
    public int sqlite3_column_type(sqlite3_stmt stmt, int index)
    {
        StmtState? s = Stmt(stmt);
        return Has(s, index) ? (int)s!.Statement.ColumnType(index) : 5;
    }

    /// <inheritdoc />
    public utf8z sqlite3_column_decltype(sqlite3_stmt stmt, int index)
    {
        StmtState? s = Stmt(stmt);
        if (s is null || (uint)index >= (uint)s.Statement.ColumnCount)
        {
            return default;
        }

        string? decl = s.Statement.ColumnDeclaredType(index);
        return string.IsNullOrEmpty(decl) ? default : utf8z.FromString(decl);
    }

    /// <inheritdoc />
    public int sqlite3_table_column_metadata(sqlite3 db, utf8z dbName, utf8z tblName, utf8z colName, out utf8z dataType, out utf8z collSeq, out int notNull, out int primaryKey, out int autoInc)
    {
        dataType = default;
        collSeq = default;
        notNull = 0;
        primaryKey = 0;
        autoInc = 0;
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            ColumnMetadata m = d.Conn.GetColumnMetadata(dbName.utf8_to_string(), tblName.utf8_to_string() ?? string.Empty, colName.utf8_to_string() ?? string.Empty);
            dataType = utf8z.FromString(m.DeclaredType);
            collSeq = utf8z.FromString(m.Collation);
            notNull = m.NotNull ? 1 : 0;
            primaryKey = m.PrimaryKey ? 1 : 0;
            autoInc = m.AutoIncrement ? 1 : 0;
            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }
}
