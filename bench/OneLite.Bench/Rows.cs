using System.Globalization;

namespace RecursiveAI.OneLite.Bench;

/// <summary>A small deterministic generator (xorshift64*), so both sides load byte-identical data from the same seed.</summary>
internal struct Rng(ulong seed)
{
    private ulong _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong Next()
    {
        _s ^= _s >> 12;
        _s ^= _s << 25;
        _s ^= _s >> 27;
        return _s * 0x2545F4914F6CDD1DUL;
    }

    public long Below(long n) => (long)(Next() % (ulong)n);

    public double Unit() => (Next() >> 11) * (1.0 / 9007199254740992.0);
}

/// <summary>What a row's code is given: sizes, the path of its database file, and a way to open it again.</summary>
internal sealed class Ctx(string level, string side, string path, int n, string? vecExt)
{
    public string Level { get; } = level;

    public string Side { get; } = side;

    public string Path { get; } = path;

    /// <summary>Base table size.</summary>
    public int N { get; } = n;

    public string? VecExt { get; } = vecExt;

    public IDriver Open() => Drivers.Open(Level, Side, Path, VecExt);
}

/// <summary>One benchmark row: settings, untimed setup, an untimed per-repetition reset, and the timed body.</summary>
internal sealed class Row
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>Applied first, on both sides, before anything is created.</summary>
    public string Journal { get; init; } = "wal";

    public string Synchronous { get; init; } = "normal";

    public bool NeedsVec { get; init; }

    public required Func<Ctx, long> Ops { get; init; }

    public required Action<IDriver, Ctx> Setup { get; init; }

    public Action<IDriver, Ctx>? PerRep { get; init; }

    /// <summary>The timed body; returns a checksum of what it read, which must agree between the two sides.</summary>
    public required Func<IDriver, Ctx, long> Run { get; init; }
}

internal static class Rows
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Every table below is t(id integer primary key, a integer, b text, c real); the data comes from one seeded generator.
    private const string CreateT = "create table t(id integer primary key, a integer, b text, c real)";

    private static string NameText(ref Rng r, long id)
        => "name-" + r.Below(1_000_000_000).ToString("D9", Inv) + "-" + id.ToString("D8", Inv) + "-padding-to-forty";

    private static void LoadT(IDriver d, int n, ulong seed = 1)
    {
        var r = new Rng(seed);
        d.Exec("begin");
        using (IStmt ins = d.Prepare("insert into t(id, a, b, c) values(@p1, @p2, @p3, @p4)"))
        {
            for (int i = 1; i <= n; i++)
            {
                ins.Bind(1, i);
                ins.Bind(2, r.Below(n));
                ins.Bind(3, NameText(ref r, i));
                ins.Bind(4, r.Unit());
                ins.Exec();
            }
        }

        d.Exec("commit");
    }

    private static void LoadU(IDriver d, int n)
    {
        var r = new Rng(2);
        d.Exec("create table u(id integer primary key, tid integer, v integer)");
        d.Exec("begin");
        using (IStmt ins = d.Prepare("insert into u(id, tid, v) values(@p1, @p2, @p3)"))
        {
            for (int i = 1; i <= n; i++)
            {
                ins.Bind(1, i);
                ins.Bind(2, r.Below(n) + 1);
                ins.Bind(3, r.Below(1000));
                ins.Exec();
            }
        }

        d.Exec("commit");
        d.Exec("create index u_tid on u(tid)");
    }

    private static void Table(IDriver d, Ctx c) { d.Exec(CreateT); LoadT(d, c.N); }

    private static long Drain(IStmt s, Func<IStmt, long> f)
    {
        long ck = 0;
        while (s.Step())
        {
            ck += f(s);
        }

        return ck;
    }

    private static Row Insert(string name, int n) => new()
    {
        Name = name,
        Description = $"insert {n:N0} rows with 4 bound parameters in one transaction (WAL, synchronous NORMAL)",
        Ops = _ => n,
        Setup = (d, _) => d.Exec(CreateT),
        PerRep = (d, _) => d.Exec("delete from t"),
        Run = (d, _) =>
        {
            var r = new Rng(1);
            d.Exec("begin");
            using (IStmt ins = d.Prepare("insert into t(id, a, b, c) values(@p1, @p2, @p3, @p4)"))
            {
                for (int i = 1; i <= n; i++)
                {
                    ins.Bind(1, i);
                    ins.Bind(2, r.Below(n));
                    ins.Bind(3, NameText(ref r, i));
                    ins.Bind(4, r.Unit());
                    ins.Exec();
                }
            }

            d.Exec("commit");
            return n;
        },
    };

    private static Row Autocommit(string name, string journal, string sync, int n) => new()
    {
        Name = name,
        Description = $"{n:N0} single-row inserts, each its own transaction ({journal}, synchronous {sync}): commit latency",
        Journal = journal,
        Synchronous = sync,
        Ops = _ => n,
        Setup = (d, _) => d.Exec(CreateT),
        PerRep = (d, _) => d.Exec("delete from t"),
        Run = (d, _) =>
        {
            var r = new Rng(1);
            using IStmt ins = d.Prepare("insert into t(id, a, b, c) values(@p1, @p2, @p3, @p4)");
            for (int i = 1; i <= n; i++)
            {
                ins.Bind(1, i);
                ins.Bind(2, r.Below(n));
                ins.Bind(3, NameText(ref r, i));
                ins.Bind(4, r.Unit());
                ins.Exec();
            }

            return n;
        },
    };

    /// <summary>All rows, in the order they are listed in the report.</summary>
    public static IReadOnlyList<Row> All { get; } =
    [
        Insert("insert_tx_10k", 10_000),
        Insert("insert_tx_100k", 100_000),
        Autocommit("insert_autocommit", "delete", "full", 200),
        Autocommit("wal_commit_normal", "wal", "normal", 1000),
        Autocommit("wal_commit_full", "wal", "full", 1000),
        new Row
        {
            Name = "lookup_rowid",
            Description = "200,000 point lookups `select b from t where id = ?` on a 100k-row table",
            Ops = _ => 200_000,
            Setup = Table,
            Run = (d, c) =>
            {
                var r = new Rng(7);
                using IStmt s = d.Prepare("select b from t where id = @p1");
                long ck = 0;
                for (int i = 0; i < 200_000; i++)
                {
                    s.Bind(1, r.Below(c.N) + 1);
                    ck += Drain(s, x => x.TextLength(0));
                }

                return ck;
            },
        },
        new Row
        {
            Name = "lookup_index",
            Description = "200,000 point lookups `select id from t where a = ?` through a secondary index",
            Ops = _ => 200_000,
            Setup = (d, c) => { Table(d, c); d.Exec("create index t_a on t(a)"); },
            Run = (d, c) =>
            {
                var r = new Rng(8);
                using IStmt s = d.Prepare("select id from t where a = @p1");
                long ck = 0;
                for (int i = 0; i < 200_000; i++)
                {
                    s.Bind(1, r.Below(c.N));
                    ck += Drain(s, x => x.Int(0));
                }

                return ck;
            },
        },
        new Row
        {
            Name = "scan_filter",
            Description = "unindexed scan of 100k rows, `select id, a, c from t where c > 0.9` (about 10% of rows returned)",
            Ops = c => c.N,
            Setup = Table,
            Run = (d, _) =>
            {
                using IStmt s = d.Prepare("select id, a, c from t where c > @p1");
                s.Bind(1, 0.9);
                return Drain(s, x => x.Int(0) + x.Int(1) + (long)(x.Real(2) * 1000));
            },
        },
        new Row
        {
            Name = "range_scan",
            Description = "2,000 indexed range scans `select id, a from t where a between ? and ? + 50` (about 50 rows each)",
            Ops = _ => 2000,
            Setup = (d, c) => { Table(d, c); d.Exec("create index t_a on t(a)"); },
            Run = (d, c) =>
            {
                var r = new Rng(9);
                using IStmt s = d.Prepare("select id, a from t where a between @p1 and @p2");
                long ck = 0;
                for (int i = 0; i < 2000; i++)
                {
                    long lo = r.Below(c.N - 60);
                    s.Bind(1, lo);
                    s.Bind(2, lo + 50);
                    ck += Drain(s, x => x.Int(0) ^ x.Int(1));
                }

                return ck;
            },
        },
        new Row
        {
            Name = "join",
            Description = "two-table join, `select count(*), sum(u.v), sum(length(t.b)) from t join u on u.tid = t.id where t.a < 10000` (100k x 100k, index on u.tid)",
            Ops = c => c.N,
            Setup = (d, c) => { Table(d, c); LoadU(d, c.N); },
            Run = (d, c) =>
            {
                using IStmt s = d.Prepare("select count(*), sum(u.v), sum(length(t.b)) from t join u on u.tid = t.id where t.a < @p1");
                s.Bind(1, c.N / 10);
                return Drain(s, x => x.Int(0) * 3 + x.Int(1) * 5 + x.Int(2));
            },
        },
        new Row
        {
            Name = "group_by",
            Description = "`select a % 100, count(*), sum(c) from t group by 1` over 100k rows",
            Ops = c => c.N,
            Setup = Table,
            Run = (d, _) =>
            {
                using IStmt s = d.Prepare("select a % 100, count(*), sum(c) from t group by 1");
                return Drain(s, x => x.Int(0) + x.Int(1) + (long)Math.Round(x.Real(2)));
            },
        },
        new Row
        {
            Name = "order_by_sort",
            Description = "`select id, b from t order by b` over 100k rows (about 7 MB of data against a 2 MB page cache)",
            Ops = c => c.N,
            Setup = Table,
            Run = (d, _) =>
            {
                using IStmt s = d.Prepare("select id, b from t order by b");
                long ck = 0;
                long k = 0;
                while (s.Step())
                {
                    ck += s.Int(0) * (++k % 1021);
                    ck += s.TextLength(1);
                }

                return ck;
            },
        },
        new Row
        {
            Name = "fts5_top10",
            Description = "300 two-word `MATCH` queries `order by rank limit 10` on 20,000 documents of 30 words",
            Ops = _ => 300,
            Setup = (d, _) =>
            {
                var r = new Rng(11);
                d.Exec("create virtual table d using fts5(body)");
                d.Exec("begin");
                using (IStmt ins = d.Prepare("insert into d(rowid, body) values(@p1, @p2)"))
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 1; i <= 20_000; i++)
                    {
                        sb.Clear();
                        for (int w = 0; w < 30; w++)
                        {
                            double u = r.Unit();
                            sb.Append('w').Append((int)(u * u * u * 2000)).Append(' ');
                        }

                        ins.Bind(1, i);
                        ins.Bind(2, sb.ToString());
                        ins.Exec();
                    }
                }

                d.Exec("commit");
            },
            Run = (d, _) =>
            {
                var r = new Rng(12);
                using IStmt s = d.Prepare("select rowid from d where d match @p1 order by rank limit 10");
                long ck = 0;
                for (int i = 0; i < 300; i++)
                {
                    s.Bind(1, "w" + (5 + r.Below(120)).ToString(Inv) + " w" + (5 + r.Below(120)).ToString(Inv));
                    ck += Drain(s, x => x.Int(0));
                }

                return ck;
            },
        },
        new Row
        {
            Name = "vec0_knn",
            Description = "100 exact k=10 nearest-neighbour queries over 20,000 float32 vectors of 128 dimensions (vec0)",
            NeedsVec = true,
            Ops = _ => 100,
            Setup = (d, _) =>
            {
                var r = new Rng(13);
                d.Exec("create virtual table v using vec0(e float[128])");
                d.Exec("begin");
                using (IStmt ins = d.Prepare("insert into v(rowid, e) values(@p1, @p2)"))
                {
                    for (int i = 1; i <= 20_000; i++)
                    {
                        ins.Bind(1, i);
                        ins.BindBlob(2, Vector(ref r));
                        ins.Exec();
                    }
                }

                d.Exec("commit");
            },
            Run = (d, _) =>
            {
                var r = new Rng(14);
                using IStmt s = d.Prepare("select rowid, distance from v where e match @p1 and k = 10");
                long ck = 0;
                for (int i = 0; i < 100; i++)
                {
                    s.BindBlob(1, Vector(ref r));
                    ck += Drain(s, x => x.Int(0));
                }

                return ck;
            },
        },
        new Row
        {
            Name = "prepare_cost",
            Description = "1,000 rounds of preparing three statements (a join with GROUP BY, an UPDATE, a point SELECT); the app level also executes each once against empty tables, since ADO.NET prepares at first use",
            Ops = _ => 3000,
            Setup = (d, _) => { d.Exec(CreateT); d.Exec("create table u(id integer primary key, tid integer, v integer)"); d.Exec("create index u_tid on u(tid)"); },
            Run = (d, c) =>
            {
                string[] sqls =
                [
                    "select t.a, count(*), sum(u.v) from t join u on u.tid = t.id where t.c > @p1 and u.v between @p2 and @p3 group by t.a order by 2 desc limit 20",
                    "update t set a = @p1, b = @p2 where id = @p3",
                    "select id, a, b from t where id = @p1",
                ];
                int[] counts = [3, 3, 1];
                bool exec = c.Level == "app";
                long ck = 0;
                for (int i = 0; i < 1000; i++)
                {
                    for (int q = 0; q < 3; q++)
                    {
                        using IStmt s = d.Prepare(sqls[q]);
                        if (exec)
                        {
                            for (int p = 1; p <= counts[q]; p++)
                            {
                                if (q == 1 && p == 2)
                                {
                                    s.Bind(p, "x");
                                }
                                else
                                {
                                    s.Bind(p, 0L);
                                }
                            }

                            ck += s.Step() ? 1 : 0;
                            s.Reset();
                        }

                        ck++;
                    }
                }

                return ck;
            },
        },
        new Row
        {
            Name = "prepare_cold",
            Description = "cold prepare: 1,000 rounds of preparing three statements of the prepare_cost shapes, each round with a new literal so no text has been prepared before (nothing to reuse)",
            Ops = _ => 3000,
            Setup = (d, _) => { d.Exec(CreateT); d.Exec("create table u(id integer primary key, tid integer, v integer)"); d.Exec("create index u_tid on u(tid)"); },
            Run = (d, c) =>
            {
                long ck = 0;
                for (int i = 0; i < 1000; i++)
                {
                    string[] sqls =
                    [
                        "select t.a, count(*), sum(u.v) from t join u on u.tid = t.id where t.c > " + i + " and u.v between 1 and 100 group by t.a order by 2 desc limit 20",
                        "update t set a = 1, b = 'x' where id = " + i,
                        "select id, a, b from t where id = " + i,
                    ];
                    for (int q = 0; q < 3; q++)
                    {
                        using IStmt s = d.Prepare(sqls[q]);
                        if (c.Level == "app")
                        {
                            ck += s.Step() ? 1 : 0;
                            s.Reset();
                        }

                        ck++;
                    }
                }

                return ck;
            },
        },
        new Row
        {
            Name = "open_time",
            Description = "200 rounds of opening a 100k-row WAL database, running one point query and closing (report only)",
            Ops = _ => 200,
            Setup = Table,
            Run = (d, c) =>
            {
                d.Dispose();
                long ck = 0;
                for (int i = 0; i < 200; i++)
                {
                    using IDriver o = c.Open();
                    using IStmt s = o.Prepare("select a from t where id = @p1");
                    s.Bind(1, 500);
                    ck += Drain(s, x => x.Int(0));
                }

                return ck;
            },
        },
    ];

    private static byte[] Vector(ref Rng r)
    {
        var b = new byte[128 * 4];
        for (int i = 0; i < 128; i++)
        {
            BitConverter.TryWriteBytes(b.AsSpan(i * 4), (float)(r.Unit() * 2 - 1));
        }

        return b;
    }
}
