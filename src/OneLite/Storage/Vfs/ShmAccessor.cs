using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>One 32 KiB region of the wal-index. Obtained from <see cref="IVfsShm.Map"/>; owned by the backend, which keeps it valid until <see cref="IVfsShm.Unmap"/> of the last user.</summary>
public sealed class ShmRegion
{
    /// <summary>The "no such region" result of <see cref="IVfsShm.Map"/> with <c>extend: false</c>.</summary>
    public static ShmRegion Empty { get; } = new(Memory<byte>.Empty, null);

    private readonly IDisposable? _owner;

    internal ShmRegion(Memory<byte> memory, IDisposable? owner)
    {
        Memory = memory;
        _owner = owner;
    }

    /// <summary>The region's bytes. Shared with every connection that mapped the same file, in this process and in others. Access words through <see cref="ShmAccessor"/>.</summary>
    public Memory<byte> Memory { get; }

    /// <summary>True for <see cref="Empty"/>.</summary>
    public bool IsEmpty => Memory.IsEmpty;

    internal void Release() => _owner?.Dispose();
}

/// <summary>
/// Atomic access to 32-bit words in a shared-memory region, the only code in the library that touches the mapped <c>-shm</c> view through
/// pointers. Other processes write the same bytes at the same time, so words are read and written with acquire/release ordering, and
/// multi-word structures (the wal-index header copies) are validated by checksum by the caller (walformat).
/// </summary>
public static class ShmAccessor
{
    /// <summary>Reads the 32-bit word at byte <paramref name="offset"/> (a multiple of 4) with acquire ordering.</summary>
    public static uint ReadUInt32(Memory<byte> region, int offset)
    {
        // Bounds: offset is checked against the region and for alignment before any reference is formed.
        CheckWord(region.Length, offset);
        ref byte b = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(region.Span);
        return Volatile.Read(ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref b, offset)));
    }

    /// <summary>Writes the 32-bit word at byte <paramref name="offset"/> (a multiple of 4) with release ordering.</summary>
    public static void WriteUInt32(Memory<byte> region, int offset, uint value)
    {
        CheckWord(region.Length, offset);
        ref byte b = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(region.Span);
        Volatile.Write(ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref b, offset)), value);
    }

    /// <summary>Atomically replaces the word if it equals <paramref name="expected"/>; returns the value that was there.</summary>
    public static uint CompareExchange(Memory<byte> region, int offset, uint value, uint expected)
    {
        CheckWord(region.Length, offset);
        ref byte b = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(region.Span);
        return Interlocked.CompareExchange(ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref b, offset)), value, expected);
    }

    /// <summary>A full memory barrier.</summary>
    public static void Barrier() => Interlocked.MemoryBarrier();

    private static void CheckWord(int length, int offset)
    {
        if ((offset & 3) != 0 || offset < 0 || offset > length - 4)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "A shared-memory word must be 4-byte aligned and inside the region.");
        }
    }

    /// <summary>Maps one region of an open <c>-shm</c> file. Creating the view extends the file to cover it.</summary>
    internal static ShmRegion MapFileRegion(FileStream file, int region)
    {
        long end = ((long)region + 1) * IVfsShm.RegionSize;
        MemoryMappedFile? mmf = null;
        MemoryMappedViewHandleOwner? owner = null;
        try
        {
            // leaveOpen: the file's descriptor must stay the one that holds the lock slots; closing any descriptor of the file would drop them (howtocorrupt §2.2).
            // The capacity may not be smaller than the file: another process may already have grown the wal-index past this region.
            mmf = MemoryMappedFile.CreateFromFile(file, null, Math.Max(end, RandomAccess.GetLength(file.SafeFileHandle)), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            MemoryMappedViewAccessor view = mmf.CreateViewAccessor((long)region * IVfsShm.RegionSize, IVfsShm.RegionSize, MemoryMappedFileAccess.ReadWrite);
            owner = new MemoryMappedViewHandleOwner(mmf, view);
            mmf = null;
            return new ShmRegion(owner.Memory, owner);
        }
        finally
        {
            mmf?.Dispose();
        }
    }

    private sealed unsafe class MemoryMappedViewHandleOwner : MemoryManager<byte>
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private byte* _base;

        public MemoryMappedViewHandleOwner(MemoryMappedFile file, MemoryMappedViewAccessor view)
        {
            _file = file;
            _view = view;
            // Bounds: the view is exactly RegionSize bytes starting at PointerOffset; GetSpan never exposes more.
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + view.PointerOffset;
        }

        public override Span<byte> GetSpan() => new(_base, IVfsShm.RegionSize);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex >= IVfsShm.RegionSize)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            return new MemoryHandle(_base + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (_base != null)
            {
                _base = null;
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _view.Dispose();
                _file.Dispose();
            }
        }
    }
}
