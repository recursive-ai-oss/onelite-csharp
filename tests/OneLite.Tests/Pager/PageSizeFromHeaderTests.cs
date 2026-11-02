using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Tests.Wal;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

/// <summary>
/// The page size of an existing database comes from its header (fileformat2 section 1.3), not from <see cref="PagerOptions.PageSize"/>, which only
/// sizes a new file. Opening with a different value must neither misread the file nor skip the write-ahead log.
/// </summary>
public class PageSizeFromHeaderTests
{
    public static TheoryData<int, JournalMode> Cases()
    {
        var d = new TheoryData<int, JournalMode>();
        foreach (int ps in new[] { 512, 1024, 2048, 8192, 65536 })
        {
            foreach (JournalMode jm in new[] { JournalMode.Delete, JournalMode.Truncate, JournalMode.Persist, JournalMode.Memory, JournalMode.Off, JournalMode.Wal })
            {
                d.Add(ps, jm);
            }
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AnExistingDatabaseKeepsItsPageSizeWhateverTheOptionsSay(int ps, JournalMode jm)
    {
        MemoryVfs vfs = jm == JournalMode.Wal ? WalKit.PrepareWal(8, ps) : Kit.Prepare(8, ps);
        // The writer stays open, so for WAL mode the committed frames are still in the log (no closing checkpoint has run).
        var writer = PagerClass.Open(vfs, Kit.Db, new PagerOptions { PageSize = ps, JournalMode = jm == JournalMode.Wal ? JournalMode.Delete : jm, WalAutoCheckpoint = 0 });
        writer.BeginReadSync();
        WalKit.Write(writer, [2u, 3u, 7u], 1);
        writer.EndReadSync();

        // The default page size (4096) differs from the file's in every case but none.
        using var reader = PagerClass.Open(vfs, Kit.Db, new PagerOptions { WalAutoCheckpoint = 0 });
        reader.BeginReadSync();
        Assert.Equal(ps, reader.PageSize);
        Assert.Equal(jm == JournalMode.Wal, reader.IsWal);
        Assert.Equal(8u, reader.PageCount);
        WalKit.CheckPattern(reader, 2, 1, ps);
        WalKit.CheckPattern(reader, 3, 1, ps);
        WalKit.CheckPattern(reader, 4, 0, ps);
        WalKit.CheckPattern(reader, 7, 1, ps);
        reader.EndReadSync();

        // Writing through the mismatched connection keeps the file's page size as well.
        reader.BeginReadSync();
        WalKit.Write(reader, [4u], 2);
        reader.EndReadSync();
        writer.BeginReadSync();
        WalKit.CheckPattern(writer, 4, 2, ps);
        WalKit.CheckPattern(writer, 7, 1, ps);
        writer.EndReadSync();
        writer.Dispose();
    }
}
