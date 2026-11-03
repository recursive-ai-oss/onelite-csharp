using System.Text;
using RecursiveAI.OneLite.Storage.Record;
using SQLitePCL;

namespace RecursiveAI.OneLite.SQLitePCLRaw;

public sealed partial class OneLiteSqlite3Provider
{
    // ------------------------------------------------------------------ backup

    private sealed class BackupState
    {
        internal nint Id;
        internal DbState Destination = null!;
        internal Backup Backup = null!;
        internal int LastError;
    }

    /// <inheritdoc />
    public sqlite3_backup sqlite3_backup_init(sqlite3 destDb, utf8z destName, sqlite3 sourceDb, utf8z sourceName)
    {
        DbState? dest = Db(destDb);
        DbState? src = Db(sourceDb);
        if (dest?.Connection is null || src?.Connection is null)
        {
            return null!;
        }

        try
        {
            Backup b = Backup.Create(dest.Connection, destName.utf8_to_string() ?? "main", src.Connection, sourceName.utf8_to_string() ?? "main");
            var state = new BackupState { Destination = dest, Backup = b };
            state.Id = Handles.Add(state);
            dest.Backups++;
            return sqlite3_backup.From(state.Id);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            dest.Fail(e);
            return null!;
        }
    }

    /// <inheritdoc />
    public int sqlite3_backup_step(sqlite3_backup backup, int nPage)
    {
        BackupState? b = Handles.Get<BackupState>(backup.DangerousGetHandle());
        if (b is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            BackupStatus st = b.Backup.Step(nPage < 0 ? int.MaxValue : nPage);
            int rc = st switch
            {
                BackupStatus.More => 0,
                BackupStatus.Done => Done,
                BackupStatus.Busy => ResultCode.Busy,
                _ => ResultCode.Locked,
            };
            if (rc is 0 or Done)
            {
                b.Destination.Ok();
            }
            else
            {
                b.Destination.Fail(rc, ErrorString(rc));
            }

            b.LastError = rc is 0 or Done ? 0 : rc;
            return rc;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            int rc = b.Destination.Fail(e);
            b.LastError = b.Destination.ExtCode;
            return rc;
        }
    }

    /// <inheritdoc />
    public int sqlite3_backup_remaining(sqlite3_backup backup) => Handles.Get<BackupState>(backup.DangerousGetHandle())?.Backup.Remaining ?? 0;

    /// <inheritdoc />
    public int sqlite3_backup_pagecount(sqlite3_backup backup) => Handles.Get<BackupState>(backup.DangerousGetHandle())?.Backup.PageCount ?? 0;

    /// <inheritdoc />
    public int sqlite3_backup_finish(IntPtr backup)
    {
        BackupState? b = Handles.Get<BackupState>(backup);
        if (b is null)
        {
            return 0;
        }

        Handles.Remove(backup);
        b.Destination.Backups--;
        try
        {
            b.Backup.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return b.Destination.Fail(e);
        }

        return b.Destination.Return(b.LastError);
    }

    // ------------------------------------------------------------------ incremental blob I/O

    private sealed class BlobState
    {
        internal nint Id;
        internal DbState Db = null!;
        internal Blob Blob = null!;
    }

    /// <inheritdoc />
    public int sqlite3_blob_open(sqlite3 db, utf8z db_utf8, utf8z table_utf8, utf8z col_utf8, long rowid, int flags, out sqlite3_blob blob)
    {
        blob = null!;
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            Blob b = Blob.Open(d.Connection, db_utf8.utf8_to_string() ?? "main", table_utf8.utf8_to_string() ?? string.Empty, col_utf8.utf8_to_string() ?? string.Empty, rowid, flags != 0);
            var state = new BlobState { Db = d, Blob = b };
            state.Id = Handles.Add(state);
            d.Blobs++;
            blob = BlobHandle.Create(state.Id);
            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    private static BlobState? Bl(sqlite3_blob b) => b is null ? null : Handles.Get<BlobState>(b.DangerousGetHandle());

    /// <inheritdoc />
    public int sqlite3_blob_bytes(sqlite3_blob blob) => Bl(blob)?.Blob.Length ?? 0;

    /// <inheritdoc />
    public int sqlite3_blob_reopen(sqlite3_blob blob, long rowid)
    {
        BlobState? b = Bl(blob);
        if (b is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            b.Blob.Reopen(rowid);
            return b.Db.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return b.Db.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_blob_write(sqlite3_blob blob, ReadOnlySpan<byte> b, int offset)
    {
        BlobState? s = Bl(blob);
        if (s is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            s.Blob.Write(offset, b);
            return s.Db.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return s.Db.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_blob_read(sqlite3_blob blob, Span<byte> b, int offset)
    {
        BlobState? s = Bl(blob);
        if (s is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            s.Blob.Read(offset, b);
            return s.Db.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return s.Db.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_blob_close(IntPtr blob)
    {
        BlobState? s = Handles.Get<BlobState>(blob);
        if (s is null)
        {
            return 0;
        }

        Handles.Remove(blob);
        s.Db.Blobs--;
        try
        {
            s.Blob.Close();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return s.Db.Fail(e);
        }

        return 0;
    }

    // ------------------------------------------------------------------ hooks

    /// <inheritdoc />
    public void sqlite3_commit_hook(sqlite3 db, delegate_commit func, object v)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return;
        }

        d.Connection.CommitHook = func is null ? null : () => func(v) != 0;
    }

    /// <inheritdoc />
    public void sqlite3_rollback_hook(sqlite3 db, delegate_rollback func, object v)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return;
        }

        d.Connection.RollbackHook = func is null ? null : () => func(v);
    }

    /// <inheritdoc />
    public void sqlite3_update_hook(sqlite3 db, delegate_update func, object v)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return;
        }

        d.Connection.UpdateHook = func is null ? null : (op, database, table, rowid) => func(v, op, utf8z.FromString(database), utf8z.FromString(table), rowid);
    }

    /// <inheritdoc />
    public void sqlite3_trace(sqlite3 db, delegate_trace func, object v)
    {
        // Not supported: the callback is accepted and never called (docs/adapter.md).
    }

    /// <inheritdoc />
    public void sqlite3_profile(sqlite3 db, delegate_profile func, object v)
    {
        // Not supported: the callback is accepted and never called (docs/adapter.md).
    }

    /// <inheritdoc />
    public void sqlite3_progress_handler(sqlite3 db, int instructions, delegate_progress func, object v)
    {
        DbState? d = Db(db);
        if (d is null)
        {
            return;
        }

        // The handler runs once before each step rather than every N VM instructions (docs/adapter.md).
        d.Progress = func;
        d.ProgressUser = v;
        d.ProgressEvery = func is null ? 0 : Math.Max(instructions, 1);
    }

    /// <inheritdoc />
    public int sqlite3_set_authorizer(sqlite3 db, delegate_authorizer authorizer, object user_data)
        => authorizer is null ? 0 : Db(db)?.Fail(ResultCode.Error, "authorizer callbacks are not supported by OneLite") ?? ResultCode.Misuse;

    private static delegate_log? s_log;
    private static object? s_logUser;

    /// <inheritdoc />
    public int sqlite3_config_log(delegate_log func, object v)
    {
        s_log = func;
        s_logUser = v;
        return 0;
    }

    /// <inheritdoc />
    public void sqlite3_log(int errcode, utf8z s) => s_log?.Invoke(s_logUser, errcode, s);

    /// <inheritdoc />
    public int sqlite3_db_status(sqlite3 db, int op, out int current, out int highest, int resetFlg)
    {
        current = 0;
        highest = 0;
        return 0;
    }

    // ------------------------------------------------------------------ snapshots

    /// <inheritdoc />
    public int sqlite3_snapshot_get(sqlite3 db, utf8z schema, out IntPtr snap)
    {
        snap = IntPtr.Zero;
        return Db(db)?.Fail(ResultCode.Error, "snapshots are not supported by OneLite") ?? ResultCode.Misuse;
    }

    /// <inheritdoc />
    public int sqlite3_snapshot_cmp(sqlite3_snapshot p1, sqlite3_snapshot p2) => 0;

    /// <inheritdoc />
    public int sqlite3_snapshot_open(sqlite3 db, utf8z schema, sqlite3_snapshot snap)
        => Db(db)?.Fail(ResultCode.Error, "snapshots are not supported by OneLite") ?? ResultCode.Misuse;

    /// <inheritdoc />
    public int sqlite3_snapshot_recover(sqlite3 db, utf8z name)
        => Db(db)?.Fail(ResultCode.Error, "snapshots are not supported by OneLite") ?? ResultCode.Misuse;

    /// <inheritdoc />
    public void sqlite3_snapshot_free(IntPtr snap)
    {
    }

    // ------------------------------------------------------------------ user functions and collations

    private static string FunctionName(byte[] zName)
    {
        int n = Array.IndexOf(zName, (byte)0);
        return Encoding.UTF8.GetString(zName, 0, n < 0 ? zName.Length : n);
    }

    /// <inheritdoc />
    public int sqlite3_create_collation(sqlite3 db, byte[] name, object v, delegate_collation func)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            d.Connection.CreateCollation(FunctionName(name), func is null ? null : (a, b) => func(v, a, b));
            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_create_function(sqlite3 db, byte[] name, int nArg, int flags, object v, delegate_function_scalar func)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            d.Connection.CreateFunction(FunctionName(name), nArg, func is null ? null : args => CallScalar(func, v, args), (flags & 0x800) != 0);
            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_create_function(sqlite3 db, byte[] name, int nArg, int flags, object v, delegate_function_aggregate_step func_step, delegate_function_aggregate_final func_final)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            d.Connection.CreateAggregate(FunctionName(name), nArg, func_step is null || func_final is null ? null : () => new UserAggregate(func_step, func_final, v));
            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    // A call into application code: the arguments and the result live in a frame on a per-thread stack, and the pointer values SQLitePCLRaw
    // passes back to the result_* and value_* methods index into it.
    private sealed class CallFrame
    {
        internal Mem[] Args = [];
        internal int ArgCount;
        internal Mem Result;
        internal OneLiteException? Error;
    }

    [ThreadStatic]
    private static List<CallFrame>? t_frames;

    [ThreadStatic]
    private static int t_depth;

    private const int ArgBits = 12;

    private static CallFrame Enter(ReadOnlySpan<Mem> args)
    {
        List<CallFrame> frames = t_frames ??= [];
        if (t_depth == frames.Count)
        {
            frames.Add(new CallFrame());
        }

        CallFrame f = frames[t_depth++];
        if (f.Args.Length < args.Length)
        {
            f.Args = new Mem[args.Length];
        }

        args.CopyTo(f.Args);
        f.ArgCount = args.Length;
        f.Result = default;
        f.Error = null;
        return f;
    }

    private static void Leave() => t_depth--;

    private static CallFrame ContextFrame(IntPtr ctx) => t_frames![checked((int)ctx) - 1];

    private static ref Mem ValueMem(IntPtr v)
    {
        int code = checked((int)v);
        CallFrame f = t_frames![(code >> ArgBits) - 1];
        return ref f.Args[(code & ((1 << ArgBits) - 1)) - 1];
    }

    private static sqlite3_value[] ValueHandles(int depthIndex, int count)
    {
        var values = new sqlite3_value[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = new sqlite3_value((IntPtr)(((depthIndex + 1) << ArgBits) | (i + 1)));
        }

        return values;
    }

    private static Mem CallScalar(delegate_function_scalar f, object user, ReadOnlySpan<Mem> args)
    {
        CallFrame frame = Enter(args);
        try
        {
            var ctx = new ContextHandle(user);
            ctx.SetPtr((IntPtr)t_depth);
            f(ctx, user, ValueHandles(t_depth - 1, args.Length));
            if (frame.Error is { } e)
            {
                throw e;
            }

            return frame.Result;
        }
        finally
        {
            Leave();
        }
    }

    private sealed class UserAggregate : IUserAggregate
    {
        private readonly delegate_function_aggregate_step _step;
        private readonly delegate_function_aggregate_final _final;
        private readonly object _user;
        private readonly ContextHandle _ctx;

        internal UserAggregate(delegate_function_aggregate_step step, delegate_function_aggregate_final final, object user)
        {
            _step = step;
            _final = final;
            _user = user;
            _ctx = new ContextHandle(user);
        }

        public void Accumulate(ReadOnlySpan<Mem> args)
        {
            CallFrame frame = Enter(args);
            try
            {
                _ctx.SetPtr((IntPtr)t_depth);
                _step(_ctx, _user, ValueHandles(t_depth - 1, args.Length));
                if (frame.Error is { } e)
                {
                    throw e;
                }
            }
            finally
            {
                Leave();
            }
        }

        public Mem GetResult()
        {
            CallFrame frame = Enter([]);
            try
            {
                _ctx.SetPtr((IntPtr)t_depth);
                _final(_ctx, _user);
                if (frame.Error is { } e)
                {
                    throw e;
                }

                return frame.Result;
            }
            finally
            {
                Leave();
            }
        }
    }

    // ------------------------------------------------------------------ results of a function call

    /// <inheritdoc />
    public void sqlite3_result_blob(IntPtr context, ReadOnlySpan<byte> val) => ContextFrame(context).Result = Mem.FromBlob(val.ToArray());

    /// <inheritdoc />
    public void sqlite3_result_double(IntPtr context, double val) => ContextFrame(context).Result = double.IsNaN(val) ? default : Mem.FromReal(val);

    /// <inheritdoc />
    public void sqlite3_result_error(IntPtr context, ReadOnlySpan<byte> strErr) => SetErrorText(ContextFrame(context), DbState.Utf8(strErr));

    /// <inheritdoc />
    public void sqlite3_result_error(IntPtr context, utf8z strErr) => SetErrorText(ContextFrame(context), strErr.utf8_to_string() ?? string.Empty);

    /// <inheritdoc />
    public void sqlite3_result_int(IntPtr context, int val) => ContextFrame(context).Result = Mem.FromInt(val);

    /// <inheritdoc />
    public void sqlite3_result_int64(IntPtr context, long val) => ContextFrame(context).Result = Mem.FromInt(val);

    /// <inheritdoc />
    public void sqlite3_result_null(IntPtr context) => ContextFrame(context).Result = default;

    /// <inheritdoc />
    public void sqlite3_result_text(IntPtr context, ReadOnlySpan<byte> val) => ContextFrame(context).Result = Mem.FromTextBytes(val.ToArray());

    /// <inheritdoc />
    public void sqlite3_result_text(IntPtr context, utf8z val) => ContextFrame(context).Result = Mem.FromTextBytes(Encoding.UTF8.GetBytes(val.utf8_to_string() ?? string.Empty));

    /// <inheritdoc />
    public void sqlite3_result_zeroblob(IntPtr context, int n) => ContextFrame(context).Result = Mem.FromBlob(new byte[Math.Max(n, 0)]);

    /// <inheritdoc />
    public void sqlite3_result_error_toobig(IntPtr context) => ContextFrame(context).Error = new OneLiteException(ResultCode.TooBig, "string or blob too big");

    /// <inheritdoc />
    public void sqlite3_result_error_nomem(IntPtr context) => ContextFrame(context).Error = new OneLiteException(ResultCode.NoMem, "out of memory");

    /// <inheritdoc />
    public void sqlite3_result_error_code(IntPtr context, int code)
    {
        // c3ref result_blob: the code replaces the code only; an error message set earlier stays
        CallFrame f = ContextFrame(context);
        f.Error = new OneLiteException(code, f.Error?.Message ?? ErrorString(code));
    }

    private static void SetErrorText(CallFrame f, string text) => f.Error = new OneLiteException(f.Error is OneLiteException { ExtendedCode: var code } ? code : ResultCode.Error, text);

    // ------------------------------------------------------------------ the arguments of a function call

    /// <inheritdoc />
    public ReadOnlySpan<byte> sqlite3_value_blob(IntPtr p)
    {
        ref Mem m = ref ValueMem(p);
        return m.Kind is StorageClass.Text or StorageClass.Blob ? m.Bytes : MemConversions.ToBytes(m);
    }

    /// <inheritdoc />
    public int sqlite3_value_bytes(IntPtr p)
    {
        ref Mem m = ref ValueMem(p);
        return m.Kind is StorageClass.Text or StorageClass.Blob ? m.Length : MemConversions.ToBytes(m).Length;
    }

    /// <inheritdoc />
    public double sqlite3_value_double(IntPtr p) => MemConversions.ToDouble(ValueMem(p));

    /// <inheritdoc />
    public int sqlite3_value_int(IntPtr p) => unchecked((int)MemConversions.ToInt64(ValueMem(p)));

    /// <inheritdoc />
    public long sqlite3_value_int64(IntPtr p) => MemConversions.ToInt64(ValueMem(p));

    /// <inheritdoc />
    public int sqlite3_value_type(IntPtr p) => ValueMem(p).Kind switch
    {
        StorageClass.Integral => 1,
        StorageClass.Real => 2,
        StorageClass.Text => 3,
        StorageClass.Blob => 4,
        _ => 5,
    };

    /// <inheritdoc />
    public utf8z sqlite3_value_text(IntPtr p)
    {
        ref Mem m = ref ValueMem(p);
        return m.Kind == StorageClass.Null ? default : Utf8Z.Copy(m.Kind is StorageClass.Text or StorageClass.Blob ? m.Bytes : MemConversions.ToBytes(m));
    }
}

/// <summary>SQLitePCLRaw offers no public way to make a <c>sqlite3_blob</c> (its constructor and <c>From</c> are internal), so one is made with its own parameterless constructor and given its handle through <see cref="System.Runtime.InteropServices.Marshal.InitHandle"/>. The constructor is named by a constant type, which the trimmer and NativeAOT keep.</summary>
internal static class BlobHandle
{
    internal static SQLitePCL.sqlite3_blob Create(nint handle)
    {
        var blob = (SQLitePCL.sqlite3_blob)Activator.CreateInstance(typeof(SQLitePCL.sqlite3_blob), true)!;
        System.Runtime.InteropServices.Marshal.InitHandle(blob, handle);
        return blob;
    }
}

/// <summary>The <c>sqlite3_context</c> a user function receives: the application's own object plus the pointer the result_* calls take.</summary>
internal sealed class ContextHandle : SQLitePCL.sqlite3_context
{
    internal ContextHandle(object user)
        : base(user)
    {
    }

    internal void SetPtr(nint ptr) => set_context_ptr(ptr);
}
