using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vec;

/// <summary>A k-nearest-neighbour scan reads every chunk, but what it allocates must not grow with the number of chunks: no allocation per vector or per chunk.</summary>
public sealed class VecAllocationTests : IDisposable
{
    private readonly string _dir = VecHelper.TempDir("vec-alloc");

    public void Dispose() => Directory.Delete(_dir, true);

    private static long Fill(Connection c, string table, int rows)
    {
        Assert.Equal(string.Empty, VecHelper.Run(c, $"create virtual table {table} using vec0(e float[64], chunk_size=64)"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "begin"));
        var rnd = new Random(7);
        for (int i = 0; i < rows; i++)
        {
            var sb = new StringBuilder("[");
            for (int k = 0; k < 64; k++)
            {
                sb.Append(k > 0 ? "," : string.Empty).Append(rnd.Next(0, 100));
            }

            Assert.Equal(string.Empty, VecHelper.Run(c, $"insert into {table}(e) values ('{sb}]')"));
        }

        Assert.Equal(string.Empty, VecHelper.Run(c, "commit"));
        return rows;
    }

    private static long QueryBytes(Connection c, string table, string probe)
    {
        string sql = $"select rowid, distance from {table} where e match '{probe}' and k=10";
        long min = long.MaxValue;
        for (int rep = 0; rep < 6; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            using (Statement st = c.Prepare(sql))
            {
                while (st.Step())
                {
                }
            }

            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return min;
    }

    [Fact]
    public void ScanAllocationDoesNotGrowWithTheNumberOfChunks()
    {
        using var d = Database.Open(Path.Combine(_dir, "a.db"));
        using Connection c = d.Connect();
        Fill(c, "small", 64 * 4);
        Fill(c, "big", 64 * 80);
        string probe = "[" + string.Join(",", Enumerable.Repeat("50", 64)) + "]";
        long small = QueryBytes(c, "small", probe);
        long big = QueryBytes(c, "big", probe);
        Assert.True(big - small < 8 * 1024, $"4 chunks allocate {small} bytes, 80 chunks allocate {big}");
    }
}
