namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>
/// Base class for a VFS backend that implements one flavour (<see cref="VfsCaps.Sync"/> or <see cref="VfsCaps.Async"/>) or both, and gets the
/// other flavour from two rules. An async-only backend serves the synchronous call by blocking only when it declares
/// <see cref="VfsCaps.BlockingOk"/> and otherwise fails with MISUSE; a sync-only backend serves the asynchronous call inline on the awaiting
/// thread and returns a completed <see cref="ValueTask"/> (an I/O call can block that thread).
/// A subclass overrides at least every method of the flavour it declares.
/// </summary>
public abstract class VfsBase : IVfs
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract VfsCaps Caps { get; }

    /// <inheritdoc/>
    public virtual int CurrentTimeMs => unchecked((int)Environment.TickCount64);

    /// <inheritdoc/>
    public virtual IVfsFile Open(string path, VfsOpenOptions flags) => Flavours.Block(OpenAsync(path, flags, default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default) => Flavours.Inline(() => Open(path, flags), ct);

    /// <inheritdoc/>
    public virtual bool Access(string path, AccessMode mode) => Flavours.Block(AccessAsync(path, mode, default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default) => Flavours.Inline(() => Access(path, mode), ct);

    /// <inheritdoc/>
    public virtual void Delete(string path, bool syncDirectory) => Flavours.Block(DeleteAsync(path, syncDirectory, default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default) => Flavours.Inline(() => Delete(path, syncDirectory), ct);

    /// <inheritdoc/>
    public abstract string FullPathname(string path);

    /// <inheritdoc/>
    public virtual void Sleep(int ms) => Thread.Sleep(ms);

    /// <inheritdoc/>
    public virtual ValueTask SleepAsync(int ms, CancellationToken ct = default) => new(Task.Delay(ms, ct));
}

/// <summary>The file counterpart of <see cref="VfsBase"/>: implement one flavour of each call, get the other from the same two rules.</summary>
public abstract class VfsFileBase : IVfsFile
{
    /// <inheritdoc/>
    public abstract VfsCaps Caps { get; }

    /// <inheritdoc/>
    public abstract int SectorSize { get; }

    /// <inheritdoc/>
    public abstract DeviceCharacteristics Device { get; }

    /// <inheritdoc/>
    public virtual IVfsShm? Shm => null;

    /// <inheritdoc/>
    public virtual int Read(long offset, Span<byte> dst)
    {
        // A span cannot cross an await, so an async-only backend is read through a pooled buffer. Only reached when the backend has no sync flavour.
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(dst.Length);
        try
        {
            int n = Flavours.Block(ReadAsync(offset, buf.AsMemory(0, dst.Length), default), Caps);
            buf.AsSpan(0, n).CopyTo(dst);
            return n;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <inheritdoc/>
    public virtual ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default) => Flavours.Inline(() => Read(offset, dst.Span), ct);

    /// <inheritdoc/>
    public virtual void Write(long offset, ReadOnlySpan<byte> src)
    {
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(src.Length);
        try
        {
            src.CopyTo(buf);
            Flavours.Block(WriteAsync(offset, buf.AsMemory(0, src.Length), default), Caps);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <inheritdoc/>
    public virtual ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default) => Flavours.Inline(() => Write(offset, src.Span), ct);

    /// <inheritdoc/>
    public virtual void WriteRun(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> pages)
    {
        foreach (ReadOnlyMemory<byte> page in pages)
        {
            Write(offset, page.Span);
            offset += page.Length;
        }
    }

    /// <inheritdoc/>
    public virtual async ValueTask WriteRunAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> pages, CancellationToken ct = default)
    {
        for (int i = 0; i < pages.Length; i++)
        {
            ReadOnlyMemory<byte> page = pages.Span[i];
            await WriteAsync(offset, page, ct).ConfigureAwait(false);
            offset += page.Length;
        }
    }

    /// <inheritdoc/>
    public virtual void Sync(SyncMode mode) => Flavours.Block(SyncAsync(mode, default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default) => Flavours.Inline(() => Sync(mode), ct);

    /// <inheritdoc/>
    public virtual void SyncDirectory() => Flavours.Block(SyncDirectoryAsync(default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask SyncDirectoryAsync(CancellationToken ct = default) => Flavours.Inline(SyncDirectory, ct);

    /// <inheritdoc/>
    public virtual void Truncate(long size) => Flavours.Block(TruncateAsync(size, default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask TruncateAsync(long size, CancellationToken ct = default) => Flavours.Inline(() => Truncate(size), ct);

    /// <inheritdoc/>
    public virtual long FileSize() => Flavours.Block(FileSizeAsync(default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask<long> FileSizeAsync(CancellationToken ct = default) => Flavours.Inline(FileSize, ct);

    /// <inheritdoc/>
    public virtual bool Lock(LockLevel level) => Flavours.Block(LockAsync(level, default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default) => Flavours.Inline(() => Lock(level), ct);

    /// <inheritdoc/>
    public virtual void Unlock(LockLevel level) => Flavours.Block(UnlockAsync(level, default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default) => Flavours.Inline(() => Unlock(level), ct);

    /// <inheritdoc/>
    public virtual bool CheckReservedLock() => Flavours.Block(CheckReservedLockAsync(default), Caps);

    /// <inheritdoc/>
    public virtual ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default) => Flavours.Inline(CheckReservedLock, ct);

    /// <inheritdoc/>
    public abstract void Dispose();
}

/// <summary>The two driver-fallback rules in one place.</summary>
internal static class Flavours
{
    /// <summary>Runs a synchronous implementation for an async caller: inline, as a completed task; failures become a faulted task.</summary>
    internal static ValueTask<T> Inline<T>(Func<T> call, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<T>(ct);
        }

        try
        {
            return new ValueTask<T>(call());
        }
        catch (Exception e)
        {
            return ValueTask.FromException<T>(e);
        }
    }

    internal static ValueTask Inline(Action call, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        try
        {
            call();
            return default;
        }
        catch (Exception e)
        {
            return ValueTask.FromException(e);
        }
    }

    /// <summary>Waits for an async implementation on behalf of a sync caller, only if the backend says blocking is fine.</summary>
    internal static T Block<T>(ValueTask<T> task, VfsCaps caps)
    {
        if (task.IsCompleted)
        {
            return task.GetAwaiter().GetResult();
        }

        RequireBlockingOk(caps);
        return task.AsTask().GetAwaiter().GetResult();
    }

    internal static void Block(ValueTask task, VfsCaps caps)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        RequireBlockingOk(caps);
        task.AsTask().GetAwaiter().GetResult();
    }

    private static void RequireBlockingOk(VfsCaps caps)
    {
        if ((caps & VfsCaps.BlockingOk) == 0)
        {
            throw new VfsException(VfsCodes.Misuse, "backend requires the async driver");
        }
    }
}
