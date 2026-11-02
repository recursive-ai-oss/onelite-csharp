using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

public class FaultVfsTests
{
    private const VfsOpenOptions Rw = VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.MainDb;
    private const int Sector = 4;

    private static byte[] Fill(char c, int n) => Enumerable.Repeat((byte)c, n).ToArray();

    private static FaultVfs New(FaultOptions? o = null) => new(new MemoryVfs(), o ?? new FaultOptions { SectorSize = Sector });

    /// <summary>The scripted workload: creates "f", writes, syncs, overwrites, truncates. Returns the vfs after all ops.</summary>
    private static FaultVfs RunScript()
    {
        FaultVfs vfs = New();
        using IVfsFile f = vfs.Open("f", Rw);          // op 0: create
        f.Write(0, Fill('A', 8));                       // op 1: sectors 0,1
        f.Sync(SyncMode.Full);                          // op 2
        f.Write(4, Fill('B', 8));                       // op 3: sectors 1,2
        f.Write(0, Fill('C', 4));                       // op 4: sector 0
        f.Sync(SyncMode.Full);                          // op 5
        f.Truncate(8);                                  // op 6
        f.Write(8, Fill('D', 4));                       // op 7: sector 2 again
        return vfs;
    }

    /// <summary>
    /// An independent statement of what the sector model allows after <paramref name="k"/> ops: the durable image of the last honoured sync,
    /// then any subset of the later sector writes and truncates, applied in issue order. Written with plain arrays and no shared code with the library.
    /// </summary>
    private static HashSet<string> Allowed(long k)
    {
        // ops: (kind, offset, bytes/size)
        var ops = new List<(char Kind, int Off, byte[] Data, int Size)>
        {
            ('c', 0, [], 0),
            ('w', 0, Fill('A', 8), 0),
            ('s', 0, [], 0),
            ('w', 4, Fill('B', 8), 0),
            ('w', 0, Fill('C', 4), 0),
            ('s', 0, [], 0),
            ('t', 0, [], 8),
            ('w', 8, Fill('D', 4), 0),
        };
        byte[] durable = [];
        bool created = false;
        bool createDurable = false;
        var pending = new List<(char Kind, int Off, byte[] Data, int Size)>();
        for (int i = 0; i < k; i++)
        {
            var op = ops[i];
            switch (op.Kind)
            {
                case 'c':
                    created = true;
                    break;
                case 's':
                    foreach (var p in pending)
                    {
                        durable = Apply(durable, p);
                    }

                    pending.Clear();
                    createDurable = true;
                    break;
                case 'w':
                    for (int at = 0; at < op.Data.Length; at += Sector)
                    {
                        pending.Add(('w', op.Off + at, op.Data[at..(at + Sector)], 0));
                    }

                    break;
                default:
                    pending.Add(op);
                    break;
            }
        }

        var result = new HashSet<string>();
        if (!created)
        {
            result.Add("<none>");
            return result;
        }

        if (!createDurable)
        {
            result.Add("<none>"); // the file's creation may not have reached the device either
        }

        for (long mask = 0; mask < (1L << pending.Count); mask++)
        {
            byte[] img = durable;
            for (int i = 0; i < pending.Count; i++)
            {
                if ((mask & (1L << i)) != 0)
                {
                    img = Apply(img, pending[i]);
                }
            }

            result.Add(System.Text.Encoding.ASCII.GetString(img.Select(b => b == 0 ? (byte)'.' : b).ToArray()));
        }

        return result;
    }

    private static byte[] Apply(byte[] img, (char Kind, int Off, byte[] Data, int Size) op)
    {
        if (op.Kind == 't')
        {
            var r = new byte[op.Size];
            Array.Copy(img, r, Math.Min(img.Length, op.Size));
            return r;
        }

        byte[] n = img.Length >= op.Off + op.Data.Length ? (byte[])img.Clone() : [.. img, .. new byte[op.Off + op.Data.Length - img.Length]];
        Array.Copy(op.Data, 0, n, op.Off, op.Data.Length);
        return n;
    }

    private static string Show(CrashImage image) =>
        image.Files.TryGetValue("f", out byte[]? b) ? System.Text.Encoding.ASCII.GetString(b.Select(x => x == 0 ? (byte)'.' : x).ToArray()) : "<none>";

    [Fact]
    public void EveryCrashPointOfTheScriptYieldsExactlyTheAllowedStates()
    {
        FaultVfs vfs = RunScript();
        Assert.Equal(8, vfs.OpCount);
        int total = 0;
        for (long k = 0; k <= vfs.OpCount; k++)
        {
            HashSet<string> expected = Allowed(k);
            var actual = vfs.EnumerateCrashImages(k).Select(Show).ToHashSet();
            Assert.True(expected.SetEquals(actual), $"crash after {k} ops: expected [{string.Join(",", expected.Order())}] got [{string.Join(",", actual.Order())}]");
            total += actual.Count;
        }

        Assert.True(total > 20, $"only {total} states over all crash points");
    }

    [Fact]
    public void RandomPoliciesStayInsideTheAllowedStates()
    {
        // Reordered, in-order and torn variants over many seeds: every image must be a state the model allows (torn ones are checked per sector).
        FaultVfs vfs = RunScript();
        int images = 0;
        for (long k = 0; k <= vfs.OpCount; k++)
        {
            HashSet<string> allowed = Allowed(k);
            for (ulong seed = 1; seed <= 200; seed++)
            {
                foreach (bool reorder in new[] { true, false })
                {
                    CrashImage image = vfs.CrashImage(k, new CrashPolicy { Seed = seed, Reorder = reorder });
                    string s = Show(image);
                    if (reorder)
                    {
                        // Reordering lets an older write land over a newer one, which the in-order enumeration does not list: check each sector instead.
                        AssertSectorsComeFromSomeVersion(s, k);
                    }
                    else
                    {
                        Assert.True(allowed.Contains(s), $"crash {k} seed {seed}: '{s}' not allowed");
                    }

                    images++;
                }
            }
        }

        Assert.Equal(9 * 200 * 2, images);
    }

    private static void AssertSectorsComeFromSomeVersion(string image, long k)
    {
        if (image == "<none>")
        {
            return;
        }

        // Candidates per sector, from the script.
        string[][] versions =
        [
            ["....", "AAAA", "CCCC"],
            ["....", "AAAA", "BBBB"],
            ["....", "BBBB", "DDDD"],
        ];
        Assert.True(image.Length <= 12);
        for (int s = 0; s * Sector < image.Length; s++)
        {
            string sector = image.Substring(s * Sector, Math.Min(Sector, image.Length - s * Sector));
            Assert.Contains(sector, versions[s].Select(v => v[..sector.Length]));
        }

        // Durability: once the second sync (op 5) is behind us, sector 0 and 1 hold at least the content synced then.
        if (k > 5)
        {
            Assert.True(image.Length >= 8, "synced length lost");
            Assert.NotEqual("....", image[..4]);
        }
    }

    [Fact]
    public void SameSeedSameImageDifferentSeedDiffers()
    {
        FaultVfs vfs = RunScript();
        var policy = new CrashPolicy { Seed = 42, TearSectors = true, TornFill = TornFill.Random };
        string a = vfs.CrashImage(4, policy).Fingerprint();
        string b = vfs.CrashImage(4, policy).Fingerprint();
        Assert.Equal(a, b);
        var fingerprints = new HashSet<string>();
        for (ulong seed = 1; seed <= 40; seed++)
        {
            fingerprints.Add(vfs.CrashImage(4, new CrashPolicy { Seed = seed, TearSectors = true, TornFill = TornFill.Random }).Fingerprint());
        }

        Assert.True(fingerprints.Count > 5);
    }

    [Fact]
    public void DropAndKeepBracketTheOutcomes()
    {
        FaultVfs vfs = RunScript();
        CrashImage keep = vfs.CrashImage(CrashPolicy.KeepAll);
        Assert.Equal("CCCCBBBB....".Replace("....", "DDDD"), Show(keep));
        CrashImage drop = vfs.CrashImage(CrashPolicy.DropUnsynced);
        Assert.Equal("CCCCBBBBBBBB", Show(drop));
    }

    [Fact]
    public void TornSectorsKeepTheFirstOrLastBytesOfAWrite()
    {
        FaultVfs vfs = New(new FaultOptions { SectorSize = 8 });
        using (IVfsFile f = vfs.Open("t", Rw))
        {
            f.Write(0, Fill('o', 8));
            f.Sync(SyncMode.Normal);
            f.Write(0, Fill('N', 8));
        }

        bool sawPrefix = false;
        bool sawSuffix = false;
        for (ulong seed = 1; seed <= 300; seed++)
        {
            CrashImage image = vfs.CrashImage(new CrashPolicy { Seed = seed, TearSectors = true, Survival = 1.0 });
            string s = System.Text.Encoding.ASCII.GetString(image.Files["t"]);
            Assert.Matches("^(N*o*|o*N*)$", s);
            sawPrefix |= s.StartsWith('N') && s.EndsWith('o');
            sawSuffix |= s.StartsWith('o') && s.EndsWith('N');
        }

        Assert.True(sawPrefix && sawSuffix);
    }

    [Fact]
    public void LostSyncKeepsTheWritesUnsynced()
    {
        FaultVfs vfs = New();
        vfs.LoseSync(1);
        using (IVfsFile f = vfs.Open("l", Rw))
        {
            f.Write(0, Fill('x', 4));
            f.Sync(SyncMode.Full);       // reported ok, but the device ignored it
        }

        FaultOp last = vfs.Ops[^1];
        Assert.True(last.Kind == FaultOpKind.Sync && last.SyncLost);
        Assert.False(vfs.CrashImage(CrashPolicy.DropUnsynced).Files.ContainsKey("l"));

        FaultVfs vfs2 = New();
        using (IVfsFile f = vfs2.Open("l", Rw))
        {
            f.Write(0, Fill('x', 4));
            f.Sync(SyncMode.Full);
        }

        Assert.Equal(Fill('x', 4), vfs2.CrashImage(CrashPolicy.DropUnsynced).Files["l"]);
    }

    [Fact]
    public void DirectorySyncDecidesWhetherANewFileSurvivesInStrictMode()
    {
        FaultVfs vfs = New(new FaultOptions { SectorSize = Sector, RequireDirectorySync = true });
        using IVfsFile f = vfs.Open("/d/j", Rw);
        f.Write(0, Fill('j', 4));
        f.Sync(SyncMode.Full);
        Assert.False(vfs.CrashImage(CrashPolicy.DropUnsynced).Files.ContainsKey("/d/j"));
        f.SyncDirectory();
        Assert.Equal(Fill('j', 4), vfs.CrashImage(CrashPolicy.DropUnsynced).Files["/d/j"]);

        vfs.Delete("/d/j", false);
        Assert.True(vfs.CrashImage(CrashPolicy.DropUnsynced).Files.ContainsKey("/d/j"));   // the removal was never synced
        Assert.False(vfs.CrashImage(CrashPolicy.KeepAll).Files.ContainsKey("/d/j"));
    }

    [Fact]
    public void ExistingFilesStartDurable()
    {
        var mem = new MemoryVfs();
        mem.Import("pre", Fill('p', 6));
        var vfs = new FaultVfs(mem, new FaultOptions { SectorSize = Sector });
        using (IVfsFile f = vfs.Open("pre", Rw))
        {
            f.Write(2, Fill('q', 2));
        }

        Assert.Equal(Fill('p', 6), vfs.CrashImage(CrashPolicy.DropUnsynced).Files["pre"]);
    }

    [Fact]
    public void InjectedErrorsCarryTheirCodesAndPartialWrites()
    {
        FaultVfs vfs = New();
        vfs.AddRule(new FaultRule { Call = FaultCall.Write, Nth = 2, ExtendedCode = VfsCodes.Full, PartialBytes = 3 });
        vfs.AddRule(new FaultRule { Call = FaultCall.Sync, Nth = 1, ExtendedCode = VfsCodes.IoErrFsync });
        using IVfsFile f = vfs.Open("e", Rw);
        f.Write(0, Fill('a', 8));
        VfsException full = Assert.Throws<VfsException>(() => f.Write(8, Fill('b', 8)));
        Assert.Equal(VfsCodes.Full, full.ExtendedCode);
        Assert.Equal(11, f.FileSize());                         // three bytes of the failed write arrived
        VfsException sync = Assert.Throws<VfsException>(() => f.Sync(SyncMode.Normal));
        Assert.Equal(VfsCodes.IoErrFsync, sync.ExtendedCode);
        Assert.Equal(VfsCodes.IoErr, sync.Code);
        f.Sync(SyncMode.Normal);                                // the rule fired once
    }

    [Fact]
    public void HaltAfterStopsTheWorkloadWithPowerLoss()
    {
        FaultVfs vfs = New();
        vfs.HaltAfter(3);
        using IVfsFile f = vfs.Open("h", Rw);                   // op 0
        f.Write(0, Fill('1', 4));                               // op 1
        f.Write(4, Fill('2', 4));                               // op 2
        Assert.Throws<PowerLossException>(() => f.Write(8, Fill('3', 4)));
        Assert.Throws<PowerLossException>(() => f.Read(0, new byte[4]));
        Assert.Equal(3, vfs.OpCount);
        Assert.Equal(8, vfs.CrashImage(CrashPolicy.KeepAll).Files["h"].Length);
    }

    [Fact]
    public async Task AsyncCallsAreLoggedLikeSyncCalls()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FaultVfs vfs = New();
        using (IVfsFile f = await vfs.OpenAsync("a", Rw, ct))
        {
            await f.WriteAsync(0, Fill('z', 8), ct);
            await f.WriteRunAsync(8, new ReadOnlyMemory<byte>[] { Fill('y', 4), Fill('x', 4) }, ct);
            await f.SyncAsync(SyncMode.Full, ct);
            await f.TruncateAsync(12, ct);
        }

        Assert.Equal(["Create", "Write", "Write", "Sync", "Truncate"], vfs.Ops.Select(o => o.Kind.ToString()));
        Assert.Equal(Fill('z', 8).Concat(Fill('y', 4)).Concat(Fill('x', 4)).ToArray(), vfs.CrashImage(4, CrashPolicy.DropUnsynced).Files["a"]);
        Assert.Equal(12, vfs.CrashImage(CrashPolicy.KeepAll).Files["a"].Length);
    }

    [Fact]
    public void ImageCanBeLoadedIntoAMemoryVfs()
    {
        FaultVfs vfs = RunScript();
        MemoryVfs restored = vfs.CrashImage(CrashPolicy.KeepAll).ToMemoryVfs();
        using IVfsFile f = restored.Open("f", VfsOpenOptions.ReadWrite);
        Assert.Equal(12, f.FileSize());
    }
}
