using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RecursiveAI.OneLite.Internal.Simd;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vec;

/// <summary>
/// Indicative speed rows, run only when ONELITE_VEC_SPEED_OUT names a file to append to (use a Release build): exact k=10 over 100,000 vectors of 768
/// float32, once through the distance kernel alone and once through SQL on a vec0 table. Rates are vectors per second on one thread.
/// </summary>
public sealed class VecSpeedTests : IDisposable
{
    private readonly string _dir = VecHelper.TempDir("vec-speed");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ExactKnnRates()
    {
        string? outFile = Environment.GetEnvironmentVariable("ONELITE_VEC_SPEED_OUT");
        Assert.SkipUnless(!string.IsNullOrEmpty(outFile), "set ONELITE_VEC_SPEED_OUT to run");
        const int N = 100_000, D = 768;
        var rnd = new Random(11);
        var data = new float[(long)N * D];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)rnd.NextDouble();
        }

        var probe = new float[D];
        for (int i = 0; i < D; i++)
        {
            probe[i] = (float)rnd.NextDouble();
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# {DateTime.UtcNow:s}Z path={VectorKernels.Path} arch={RuntimeInformation.ProcessArchitecture} cpu={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")} runtime={RuntimeInformation.FrameworkDescription}\n");
        double best = double.MaxValue, sink = 0;
        for (int rep = 0; rep < 5; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                sink += VectorKernels.L2Squared(probe, data.AsSpan(i * D, D));
            }

            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }

        sb.Append(CultureInfo.InvariantCulture, $"kernel l2 100k x 768 f32: {N / best:F0} vectors/s ({best * 1000:F1} ms) [{sink > 0}]\n");

        using var db = Database.Open(Path.Combine(_dir, "s.db"));
        using Connection c = db.Connect();
        VecHelper.Run(c, "pragma journal_mode=wal");
        VecHelper.Run(c, "create virtual table v using vec0(e float[768])");
        VecHelper.Run(c, "begin");
        var ins = Stopwatch.StartNew();
        using (Statement st = c.Prepare("insert into v(e) values (?1)"))
        {
            var buf = new byte[D * 4];
            for (int i = 0; i < N; i++)
            {
                MemoryMarshal.AsBytes(data.AsSpan(i * D, D)).CopyTo(buf);
                st.Reset();
                st.Bind(1, buf);
                st.Step();
            }
        }

        VecHelper.Run(c, "commit");
        sb.Append(CultureInfo.InvariantCulture, $"sql insert 100k x 768 f32: {N / ins.Elapsed.TotalSeconds:F0} rows/s\n");
        var pb = new byte[D * 4];
        MemoryMarshal.AsBytes(probe.AsSpan()).CopyTo(pb);
        double bestQ = double.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            var sw = Stopwatch.StartNew();
            using Statement q = c.Prepare("select rowid, distance from v where e match ?1 and k=10");
            q.Bind(1, pb);
            int rows = 0;
            while (q.Step())
            {
                rows++;
            }

            Assert.Equal(10, rows);
            bestQ = Math.Min(bestQ, sw.Elapsed.TotalSeconds);
        }

        sb.Append(CultureInfo.InvariantCulture, $"sql knn k=10 100k x 768 f32: {N / bestQ:F0} vectors/s ({bestQ * 1000:F1} ms per query)\n");
        File.AppendAllText(outFile!, sb.ToString());
    }
}
