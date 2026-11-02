using System.Linq;

namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>A deterministic generator (SplitMix64) so a seed means the same thing on every runtime version.</summary>
internal struct SeededRng
{
    private ulong _state;

    public SeededRng(ulong seed)
    {
        _state = seed;
    }

    public ulong NextU64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    public int Next(int maxExclusive) => (int)(NextU64() % (ulong)maxExclusive);
}

/// <summary>The unsynced changes of each file and its durable image, built by replaying the log up to a crash point.</summary>
internal sealed class CrashModel
{
    private readonly FaultOptions _options;
    private readonly Dictionary<int, NodeState> _nodes = [];
    private readonly Dictionary<string, int> _durableNamespace = new(StringComparer.Ordinal);
    private readonly List<NamespaceEvent> _pendingNamespace = [];

    private CrashModel(FaultOptions options)
    {
        _options = options;
    }

    private sealed class NodeState
    {
        public byte[] Durable = [];
        public readonly List<Change> Pending = [];
    }

    /// <summary>One unsynced change to a file: a write or a length change.</summary>
    private readonly struct Change
    {
        public Change(long offset, byte[]? data, long truncateTo)
        {
            Offset = offset;
            Data = data;
            TruncateTo = truncateTo;
        }

        public long Offset { get; }

        public byte[]? Data { get; }

        public long TruncateTo { get; }

        public bool IsTruncate => Data == null;
    }

    private sealed class NamespaceEvent
    {
        public bool Create;
        public string Path = string.Empty;
        public int Node;
    }

    public static CrashModel Replay(List<LogEntry> log, FaultOptions options)
    {
        var m = new CrashModel(options);
        foreach (LogEntry e in log)
        {
            switch (e.Kind)
            {
                case LogKind.Baseline:
                    m._nodes[e.Node] = new NodeState { Durable = e.Data ?? [] };
                    m._durableNamespace[e.Path] = e.Node;
                    break;
                case LogKind.Create:
                    m._nodes[e.Node] = new NodeState();
                    m._pendingNamespace.Add(new NamespaceEvent { Create = true, Path = e.Path, Node = e.Node });
                    break;
                case LogKind.Write:
                    m._nodes[e.Node].Pending.Add(new Change(e.Offset, e.Data, 0));
                    break;
                case LogKind.Truncate:
                    m._nodes[e.Node].Pending.Add(new Change(0, null, e.Size));
                    break;
                case LogKind.Sync:
                    if (!e.Lost)
                    {
                        m.MakeDurable(e.Node);
                    }

                    break;
                case LogKind.SyncDirectory:
                    m.SyncDirectory(DirectoryOf(e.Path));
                    break;
                case LogKind.Delete:
                    m._pendingNamespace.Add(new NamespaceEvent { Create = false, Path = e.Path, Node = e.Node });
                    if (e.SyncDirectory)
                    {
                        m.SyncDirectory(DirectoryOf(e.Path));
                    }

                    break;
            }
        }

        return m;
    }

    private static string DirectoryOf(string path) => Path.GetDirectoryName(path) ?? string.Empty;

    private void MakeDurable(int node)
    {
        NodeState n = _nodes[node];
        var buf = new List<byte>(n.Durable);
        foreach (Change c in n.Pending)
        {
            Apply(buf, c.Offset, c.Data, c.TruncateTo);
        }

        n.Durable = [.. buf];
        n.Pending.Clear();
        if (!_options.RequireDirectorySync)
        {
            // Ordered metadata: a synced file's creation, and every directory change made before it, are on the device with it.
            int last = _pendingNamespace.FindLastIndex(e => e.Create && e.Node == node);
            if (last >= 0)
            {
                PromoteNamespace(_pendingNamespace.GetRange(0, last + 1));
            }
        }
    }

    private void SyncDirectory(string dir) => PromoteNamespace(_pendingNamespace.FindAll(e => DirectoryOf(e.Path) == dir));

    private void PromoteNamespace(List<NamespaceEvent> events)
    {
        foreach (NamespaceEvent e in events)
        {
            ApplyNamespace(_durableNamespace, e);
            _pendingNamespace.Remove(e);
        }
    }

    private static void ApplyNamespace(Dictionary<string, int> ns, NamespaceEvent e)
    {
        if (e.Create)
        {
            ns[e.Path] = e.Node;
        }
        else
        {
            ns.Remove(e.Path);
        }
    }

    private static void Apply(List<byte> buf, long offset, byte[]? data, long truncateTo)
    {
        if (data == null)
        {
            Resize(buf, truncateTo);
            return;
        }

        if (offset + data.Length > buf.Count)
        {
            Resize(buf, offset + data.Length);
        }

        for (int i = 0; i < data.Length; i++)
        {
            buf[(int)offset + i] = data[i];
        }
    }

    private static void Resize(List<byte> buf, long size)
    {
        if (size < buf.Count)
        {
            buf.RemoveRange((int)size, buf.Count - (int)size);
        }
        else
        {
            while (buf.Count < size)
            {
                buf.Add(0);
            }
        }
    }

    /// <summary>One sector-sized piece of an unsynced write, or a length change.</summary>
    private sealed class Unit
    {
        public long Offset;
        public byte[]? Data;
        public long TruncateTo;
    }

    private List<Unit> Units(NodeState n)
    {
        var units = new List<Unit>();
        foreach (Change c in n.Pending)
        {
            if (c.IsTruncate)
            {
                units.Add(new Unit { TruncateTo = c.TruncateTo });
                continue;
            }

            byte[] d = c.Data!;
            long pos = c.Offset;
            int at = 0;
            while (at < d.Length)
            {
                int room = _options.SectorSize - (int)(pos % _options.SectorSize);
                int len = Math.Min(room, d.Length - at);
                units.Add(new Unit { Offset = pos, Data = d.AsSpan(at, len).ToArray() });
                pos += len;
                at += len;
            }
        }

        return units;
    }

    // ---- one random or fixed outcome ----

    public CrashImage Realize(CrashPolicy policy, long afterOps)
    {
        var rng = new SeededRng(policy.Seed);
        var ns = new Dictionary<string, int>(_durableNamespace, StringComparer.Ordinal);
        int keep = policy.Mode switch
        {
            CrashMode.DropUnsynced => 0,
            CrashMode.KeepAll => _pendingNamespace.Count,
            _ => LeadingSurvivors(_pendingNamespace.Count, policy, ref rng),
        };
        for (int i = 0; i < keep; i++)
        {
            ApplyNamespace(ns, _pendingNamespace[i]);
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> f in new SortedDictionary<string, int>(ns, StringComparer.Ordinal))
        {
            files[f.Key] = RealizeFile(_nodes[f.Value], policy, ref rng);
        }

        return new CrashImage(files, afterOps);
    }

    private static int LeadingSurvivors(int count, CrashPolicy p, ref SeededRng rng)
    {
        int n = 0;
        while (n < count && rng.NextDouble() < p.Survival)
        {
            n++;
        }

        return n;
    }

    private byte[] RealizeFile(NodeState node, CrashPolicy policy, ref SeededRng rng)
    {
        var buf = new List<byte>(node.Durable);
        if (policy.Mode == CrashMode.DropUnsynced)
        {
            return [.. buf];
        }

        if (policy.Mode == CrashMode.KeepAll)
        {
            foreach (Change c in node.Pending)
            {
                Apply(buf, c.Offset, c.Data, c.TruncateTo);
            }

            return [.. buf];
        }

        List<Unit> units = Units(node);
        List<Unit> survivors;
        if (policy.Reorder)
        {
            survivors = [];
            foreach (Unit u in units)
            {
                if (rng.NextDouble() < policy.Survival)
                {
                    survivors.Add(u);
                }
            }

            for (int i = survivors.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (survivors[i], survivors[j]) = (survivors[j], survivors[i]);
            }
        }
        else
        {
            survivors = units.GetRange(0, LeadingSurvivors(units.Count, policy, ref rng));
        }

        foreach (Unit u in survivors)
        {
            if (u.Data == null)
            {
                Apply(buf, 0, null, u.TruncateTo);
                continue;
            }

            byte[] data = u.Data;
            if (policy.TearSectors && data.Length > 1 && rng.NextDouble() < 0.5)
            {
                // atomiccommit §2.2: a sector is written linearly, so either its first or its last bytes arrive.
                int arrived = rng.Next(data.Length);
                bool fromStart = rng.Next(2) == 0;
                var torn = new byte[data.Length];
                if (policy.TornFill == TornFill.Keep)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        long p = u.Offset + i;
                        torn[i] = p < buf.Count ? buf[(int)p] : (byte)0;
                    }
                }
                else if (policy.TornFill == TornFill.Random)
                {
                    for (int i = 0; i < torn.Length; i++)
                    {
                        torn[i] = (byte)rng.NextU64();
                    }
                }

                if (fromStart)
                {
                    Array.Copy(data, 0, torn, 0, arrived);
                }
                else
                {
                    Array.Copy(data, data.Length - arrived, torn, data.Length - arrived, arrived);
                }

                data = torn;
            }

            Apply(buf, u.Offset, data, 0);
        }

        return [.. buf];
    }

    // ---- every outcome of the independent-survival model ----

    public IEnumerable<CrashImage> Enumerate(long afterOps, int maxUnits)
    {
        var allUnits = new List<(int Node, Unit Unit)>();
        foreach (KeyValuePair<int, NodeState> n in _nodes.OrderBy(k => k.Key))
        {
            foreach (Unit u in Units(n.Value))
            {
                allUnits.Add((n.Key, u));
            }
        }

        if (allUnits.Count > maxUnits)
        {
            throw new ArgumentException($"{allUnits.Count} unsynced units; the limit is {maxUnits}.", nameof(maxUnits));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Iterate();

        IEnumerable<CrashImage> Iterate()
        {
            for (int prefix = 0; prefix <= _pendingNamespace.Count; prefix++)
            {
                var ns = new Dictionary<string, int>(_durableNamespace, StringComparer.Ordinal);
                for (int i = 0; i < prefix; i++)
                {
                    ApplyNamespace(ns, _pendingNamespace[i]);
                }

                for (long mask = 0; mask < (1L << allUnits.Count); mask++)
                {
                    var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, int> f in ns)
                    {
                        var buf = new List<byte>(_nodes[f.Value].Durable);
                        for (int i = 0; i < allUnits.Count; i++)
                        {
                            if ((mask & (1L << i)) != 0 && allUnits[i].Node == f.Value)
                            {
                                Unit u = allUnits[i].Unit;
                                Apply(buf, u.Offset, u.Data, u.TruncateTo);
                            }
                        }

                        files[f.Key] = [.. buf];
                    }

                    var image = new CrashImage(files, afterOps);
                    if (seen.Add(image.Fingerprint()))
                    {
                        yield return image;
                    }
                }
            }
        }
    }
}
