using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneLite.Interop;

/// <summary>Layout choices of one corpus database.</summary>
/// <param name="PageSize">Page size in bytes (512 to 65536).</param>
/// <param name="Encoding">Text encoding as the pragma spells it: UTF-8, UTF-16le or UTF-16be.</param>
/// <param name="AutoVacuum">0 none, 1 full, 2 incremental.</param>
/// <param name="Reserved">Reserved bytes at the end of every page (0 = the default).</param>
internal sealed record Settings(int PageSize, string Encoding, int AutoVacuum, int Reserved = 0)
{
    public string EncodingTag => Encoding switch { "UTF-8" => "utf8", "UTF-16le" => "utf16le", _ => "utf16be" };
}

/// <summary>
/// The SQL that builds the corpus databases. All content is deterministic (no random(), no clock) so the same script
/// always yields the same logical data, and the same script can be handed to any writer to build the file again.
/// </summary>
internal static class Scripts
{
    // Deterministic filler text of exactly n characters, different per seed: the seed repeated with commas.
    private static string Fill(string n, string seed)
        => string.Create(CultureInfo.InvariantCulture, $"substr(replace(hex(zeroblob({n})),'00',{seed}||','),1,{n})");

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The pragmas that must precede the first table.</summary>
    public static string Pragmas(Settings s)
        => string.Create(CultureInfo.InvariantCulture, $"PRAGMA page_size={s.PageSize};\nPRAGMA encoding='{s.Encoding}';\nPRAGMA auto_vacuum={s.AutoVacuum};\n");

    /// <summary>
    /// The base content: every b-tree page type, overflow chains on table cells and on index keys, WITHOUT ROWID and STRICT
    /// tables, edge-case values of every storage class and integer width, a view, a trigger, generated columns, AUTOINCREMENT,
    /// ANALYZE statistics and (except for 64 KiB pages in a way that stays small) a freelist.
    /// </summary>
    public static string Base(Settings s, bool withPragmas = true)
    {
        int p = s.PageSize;
        int rows = 300 + p / 16;
        var sb = new StringBuilder();
        if (withPragmas)
        {
            sb.Append(Pragmas(s));
        }
        sb.Append("""
            CREATE TABLE t1(id INTEGER PRIMARY KEY, i INT, r REAL, s TEXT, b BLOB, n);
            INSERT INTO t1(id,i,r,s,b,n) VALUES
              (1,0,0.0,'',x'',NULL),
              (2,1,1.5,'a',x'00',1),
              (3,-1,-0.0,'héllo wörld',x'ff',2.5),
              (4,127,1e300,'日本語',x'0102','n'),
              (5,-128,5e-324,'😀 emoji',x'deadbeef',x'cafe'),
              (6,32767,3.14159265358979,'a'||char(9)||'b'||char(10)||'c',x'00ff00',0),
              (7,32768,9007199254740992.0,'quote''s "double"',x'',-1),
              (8,-32769,1e-7,'x',x'61',1e20),
              (9,8388607,-1e300,'y',x'62',NULL),
              (10,8388608,0.1,'z',x'63',NULL),
              (11,-8388609,0.30000000000000004,'',x'',''),
              (12,2147483647,2.5e-300,'12',x'31',12),
              (13,2147483648,100.0,'13',x'32',13.0),
              (14,140737488355327,1.0,'14',x'33','14'),
              (15,140737488355328,-1.0,'15',x'34',NULL),
              (16,9223372036854775807,9e999,'16',x'35',NULL),
              (17,-9223372036854775807,-9e999,'17',x'36',NULL),
              (18,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO t1(id,i,r,s,b,n) VALUES (19,-9223372036854775808,2251799813685248.0,'min int',x'37',9223372036854775807);
            CREATE INDEX t1s ON t1(s);
            CREATE INDEX t1ir ON t1(i, r);

            """);
        sb.Append(CultureInfo.InvariantCulture, $"""
            WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<{rows})
            INSERT INTO t1(i,r,s,b) SELECT (x*7919)%1000, x/3.0, printf('row %d %s', x, hex(zeroblob(x%40))), zeroblob(x%50) FROM c;

            """);

        // Overflow chains on table cells: lengths just under, at and over what stays local, and a long chain.
        sb.Append(CultureInfo.InvariantCulture, $"""
            CREATE TABLE ov(id INTEGER PRIMARY KEY, kind TEXT, big TEXT, bl BLOB);
            WITH v(id,len) AS (VALUES (1,10),(2,{p / 2}),(3,{p - 60}),(4,{p - 30}),(5,{p}),(6,{3 * p + 17}))
            INSERT INTO ov SELECT id, 'len'||len, {Fill("len", "id")}, CAST({Fill("len", "id+100")} AS BLOB) FROM v;

            """);

        // Overflow on index keys: keys larger than an index page keeps local, so the entry spills into a chain; many
        // medium keys make interior index pages with long separators.
        sb.Append(CultureInfo.InvariantCulture, $"""
            CREATE TABLE ik(id INTEGER PRIMARY KEY, k TEXT, v INT);
            CREATE INDEX ik_k ON ik(k);
            WITH v(id,len) AS (VALUES (1,{p / 2}),(2,{p}),(3,{2 * p + 7}),(4,{3 * p}))
            INSERT INTO ik SELECT id, {Fill("len", "id")}, id FROM v;
            WITH RECURSIVE c(x) AS (SELECT 10 UNION ALL SELECT x+1 FROM c WHERE x<60)
            INSERT INTO ik SELECT x, {Fill($"{p / 8}+x", "x")}, x FROM c;

            """);

        int wr = 211;
        sb.Append(CultureInfo.InvariantCulture, $"""
            CREATE TABLE wr(a TEXT, b INT, c BLOB, d, PRIMARY KEY(a,b)) WITHOUT ROWID;
            WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<{wr})
            INSERT INTO wr SELECT printf('k%05d', (x*37)%{wr}), x%5, zeroblob(x%30), x*1.5 FROM c;
            INSERT INTO wr VALUES ('big1',1,CAST({Fill(N(p + 100), "1")} AS BLOB),NULL),('big2',2,CAST({Fill(N(2 * p + 9), "2")} AS BLOB),'two');
            CREATE INDEX wr_d ON wr(d);

            CREATE TABLE st(id INTEGER PRIMARY KEY, a INT, b REAL, c TEXT, d BLOB, e ANY) STRICT;
            INSERT INTO st VALUES (1,1,1.5,'one',x'01',1),(2,-2,-2.5,'two',x'0202','two'),(3,NULL,NULL,NULL,NULL,NULL),(4,9223372036854775807,1e10,'',x'',x'ff'),(5,5,5.0,'five',x'05',5.5);

            CREATE TABLE audit(id INTEGER PRIMARY KEY AUTOINCREMENT, note TEXT);
            CREATE TRIGGER t1_big AFTER INSERT ON t1 WHEN new.id > 100000000 BEGIN INSERT INTO audit(note) VALUES ('big id'); END;
            INSERT INTO audit(note) VALUES ('first'),('second'),('third');
            DELETE FROM audit WHERE id = 2;
            CREATE VIEW v1 AS SELECT id, s FROM t1 WHERE i > 500;
            CREATE TABLE g(a INT, b INT GENERATED ALWAYS AS (a*2) VIRTUAL, c INT GENERATED ALWAYS AS (a+1) STORED);
            INSERT INTO g(a) VALUES (1),(2),(3),(NULL);

            """);

        // A freelist: fill a table with about two rows per page, then drop it. With auto_vacuum=FULL the pages are
        // returned at commit, so the freelist only remains for NONE and INCREMENTAL. The size is capped to keep files small.
        long junkPages = Math.Min(Math.Min(p / 4 + 20, 1100), 6_000_000 / p);
        sb.Append(CultureInfo.InvariantCulture, $"""
            CREATE TABLE junk(id INTEGER PRIMARY KEY, x TEXT);
            WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<{junkPages * 2})
            INSERT INTO junk SELECT x, {Fill(N(p / 3), "x")} FROM c;
            DROP TABLE junk;

            """);
        if (s.AutoVacuum == 2)
        {
            sb.Append(CultureInfo.InvariantCulture, $"PRAGMA incremental_vacuum({junkPages / 3});\n");
        }
        sb.Append("""
            ANALYZE;
            PRAGMA user_version=42;
            PRAGMA application_id=1329874252;

            """);
        return sb.ToString();
    }

    /// <summary>The transactions that follow the base content in the WAL cases; each is one commit.</summary>
    public static IReadOnlyList<string> WalTransactions(Settings s)
    {
        int p = s.PageSize;
        return
        [
            // 2: inserts and an update of an overflow row
            "BEGIN;\nWITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<40) INSERT INTO t1(i,r,s,b) SELECT x, x*0.25, 'wal2 '||x, zeroblob(x) FROM c;\nUPDATE ov SET kind='updated' WHERE id=2;\nCOMMIT;\n",
            // 3: deletes and an audit row
            "BEGIN;\nDELETE FROM t1 WHERE id%5=0 AND id>20;\nINSERT INTO audit(note) VALUES ('t3');\nUPDATE g SET a=a+10;\nCOMMIT;\n",
            // 4: a larger transaction that touches many pages
            string.Create(CultureInfo.InvariantCulture, $"BEGIN;\nWITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<120) INSERT INTO ov SELECT 100+x, 'w4', {Fill($"{p / 2}+x", "x")}, NULL FROM c;\nCOMMIT;\n"),
            // 5: schema change plus data
            "BEGIN;\nDROP INDEX ik_k;\nCREATE TABLE late(a INTEGER PRIMARY KEY, b TEXT) STRICT;\nINSERT INTO late VALUES (1,'late'),(2,'later');\nUPDATE st SET a=a+1 WHERE a IS NOT NULL AND a < 100;\nCOMMIT;\n",
        ];
    }

    /// <summary>One big transaction (for spills past the cache) used by the hot-journal and big-WAL cases.</summary>
    public static string BigTransactionBody(Settings s, string kind)
    {
        int p = s.PageSize;
        return kind switch
        {
            // touches every row of the big tables so the cache has to spill modified pages
            "update" => "UPDATE t1 SET s = s || '-changed-' || id, b = zeroblob(70); UPDATE ov SET big = big || 'tail'; UPDATE ik SET k = k || '!';",
            // grows the file well past its size at the start of the transaction
            "grow" => string.Create(CultureInfo.InvariantCulture, $"CREATE TABLE grown(id INTEGER PRIMARY KEY, x TEXT); WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<{Math.Max(200, Math.Min(600, 4_000_000 / p))}) INSERT INTO grown SELECT x, {Fill(N(p), "x")} FROM c;"),
            // frees and reshuffles pages
            "drop" => "DROP TABLE ov; DELETE FROM t1 WHERE id > 20; DROP INDEX ik_k;",
            _ => throw new ArgumentException("unknown transaction kind " + kind),
        };
    }
}
