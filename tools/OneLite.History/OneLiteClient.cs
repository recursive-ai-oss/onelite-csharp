using System;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.History;

/// <summary>Creates clients on the OneLite engine's <c>BEGIN CONCURRENT</c>, one connection each, all on one database file.</summary>
public sealed class OneLiteClientFactory : IHistoryClientFactory
{
    private readonly string _db;
    private readonly bool _snapshot;

    /// <summary>Creates the factory.</summary>
    /// <param name="dbPath">Database file (created if absent).</param>
    /// <param name="snapshotIsolation">True for <c>BEGIN CONCURRENT SNAPSHOT</c>, false for the serializable default.</param>
    public OneLiteClientFactory(string dbPath, bool snapshotIsolation)
    {
        _db = dbPath;
        _snapshot = snapshotIsolation;
    }

    private static DatabaseOptions Options() => new()
    {
        JournalMode = JournalMode.Wal,
        ConcurrentWrites = true,
        // ONELITE_HISTORY_SYNC=full runs the same workloads with WAL commits that share syncs (group commit).
        Synchronous = Environment.GetEnvironmentVariable("ONELITE_HISTORY_SYNC") == "full" ? SynchronousMode.Full : SynchronousMode.Normal,
        BusyTimeoutMs = 30000,
    };

    /// <inheritdoc/>
    public void Prepare()
    {
        using var db = Database.Open(_db, Options());
        using var c = db.Connect();
        Exec(c, "CREATE TABLE IF NOT EXISTS l(k INTEGER PRIMARY KEY, v TEXT NOT NULL)");
        Exec(c, "CREATE TABLE IF NOT EXISTS r(k INTEGER PRIMARY KEY, v INTEGER NOT NULL)");
    }

    internal static void Exec(Connection c, string sql)
    {
        using var st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    /// <inheritdoc/>
    public IHistoryClient Create(int worker) => new Client(Database.Open(_db, Options()), _snapshot);

    private sealed class Client : IHistoryClient
    {
        private readonly Database _database;
        private readonly Connection _c;
        private readonly string _begin;
        private readonly Statement _readList;
        private readonly Statement _appendList;
        private readonly Statement _readReg;
        private readonly Statement _writeReg;
        private bool _open;

        public Client(Database db, bool snapshot)
        {
            _database = db;
            _c = db.Connect();
            _begin = snapshot ? "BEGIN CONCURRENT SNAPSHOT" : "BEGIN CONCURRENT";
            _readList = _c.Prepare("SELECT v FROM l WHERE k = ?1");
            _appendList = _c.Prepare("INSERT INTO l(k, v) VALUES(?1, CAST(?2 AS TEXT)) ON CONFLICT(k) DO UPDATE SET v = v || ',' || ?2");
            _readReg = _c.Prepare("SELECT v FROM r WHERE k = ?1");
            _writeReg = _c.Prepare("INSERT INTO r(k, v) VALUES(?1, ?2) ON CONFLICT(k) DO UPDATE SET v = ?2");
        }

        public OpStatus Begin()
        {
            try
            {
                Exec(_c, _begin);
                _open = true;
                return OpStatus.Ok;
            }
            catch (OneLiteException)
            {
                return OpStatus.Fail;
            }
        }

        public OpStatus ReadList(int key, out int[] list)
        {
            list = [];
            try
            {
                _readList.Bind(1, (long)key);
                if (_readList.Step())
                {
                    list = ParseList(_readList.GetString(0));
                }

                _readList.Reset();
                return OpStatus.Ok;
            }
            catch (OneLiteException)
            {
                return Failed();
            }
        }

        public OpStatus ReadRegister(int key, out int value)
        {
            value = 0;
            try
            {
                _readReg.Bind(1, (long)key);
                if (_readReg.Step())
                {
                    value = (int)_readReg.GetInt64(0);
                }

                _readReg.Reset();
                return OpStatus.Ok;
            }
            catch (OneLiteException)
            {
                return Failed();
            }
        }

        public OpStatus Write(int key, int value) => Run(_writeReg, key, value);

        public OpStatus Append(int key, int value) => Run(_appendList, key, value);

        private OpStatus Run(Statement st, int key, int value)
        {
            try
            {
                st.Bind(1, (long)key);
                st.Bind(2, (long)value);
                while (st.Step())
                {
                }

                st.Reset();
                return OpStatus.Ok;
            }
            catch (OneLiteException)
            {
                return Failed();
            }
        }

        public OpStatus Commit()
        {
            try
            {
                Exec(_c, "COMMIT");
                _open = false;
                return OpStatus.Ok;
            }
            catch (OneLiteException e)
            {
                // A refused commit (busy, busy snapshot) committed nothing; anything else is not known to have failed cleanly.
                bool busy = e.Code == ResultCode.Busy;
                Abort();
                return busy ? OpStatus.Fail : OpStatus.Info;
            }
        }

        public void Abort()
        {
            if (_open)
            {
                try
                {
                    Exec(_c, "ROLLBACK");
                }
                catch (OneLiteException)
                {
                    // The connection is going away or already rolled back.
                }

                _open = false;
            }
        }

        public void Dispose()
        {
            Abort();
            _readList.Dispose();
            _appendList.Dispose();
            _readReg.Dispose();
            _writeReg.Dispose();
            _c.Dispose();
            _database.Dispose();
        }

        private OpStatus Failed()
        {
            Abort();
            return OpStatus.Fail;
        }

        private static int[] ParseList(string? text) => SqliteClientFactory.ParseList(text);
    }
}
