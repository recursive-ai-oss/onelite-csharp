using System.Linq;
using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Vm;

/// <summary>One PRAGMA, as compiled: its name (lower case) and the value after "=" or in parentheses, if any.</summary>
internal sealed record PragmaCall(string Name, string? Arg, int Db = 0)
{
    /// <summary>True for the temp database.</summary>
    internal bool Temp => Db == 1;
}

/// <summary>The per-connection values of the pragmas that only record a setting (pragma), by name.</summary>
internal sealed class PragmaSettings
{
    private readonly Dictionary<string, long> _values = [];

    internal long Get(string name, long dflt) => _values.TryGetValue(name, out long v) ? v : dflt;

    internal void Set(string name, long value) => _values[name] = value;

    /// <summary>PRAGMA temp_store_directory, "" when unset.</summary>
    internal string TempStoreDirectory { get; set; } = string.Empty;

    /// <summary>A statement has named the temporary database, so it counts as opened.</summary>
    internal bool TempOpen { get; set; }

    /// <summary>The parameters PRAGMA key starts a database with (PRAGMA cipher_default_*): SQLCipher 4's unless changed.</summary>
    internal Storage.Crypto.CipherSettings DefaultCipher { get; set; } = Storage.Crypto.CipherSettings.V4;

    internal bool QueryOnly => Get("query_only", 0) != 0;
}

internal static class PragmaArgs
{
    /// <summary>The integer a pragma argument names: an optional sign and digits, 0 when there are none or the value does not fit 32 bits.</summary>
    internal static int Atoi(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        int i = 0;
        while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
        {
            i++;
        }

        bool neg = false;
        if (i < text.Length && text[i] is '-' or '+')
        {
            neg = text[i] == '-';
            i++;
        }

        long v = 0;
        int start = i;
        while (i < text.Length && text[i] is >= '0' and <= '9')
        {
            v = (v * 10) + (text[i] - '0');
            if (v > 4294967296L)
            {
                v = 4294967296L;
            }

            i++;
        }

        if (i == start)
        {
            return 0;
        }

        if (neg)
        {
            v = -v;
        }

        return v is > int.MaxValue or < int.MinValue ? 0 : (int)v;
    }

    /// <summary>The 64-bit integer a pragma argument names (same rules as <see cref="Atoi"/>, saturating).</summary>
    internal static long Atoi64(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        string t = text.Trim();
        int i = 0;
        bool neg = false;
        if (i < t.Length && t[i] is '-' or '+')
        {
            neg = t[i] == '-';
            i++;
        }

        int start = i;
        decimal v = 0;
        while (i < t.Length && t[i] is >= '0' and <= '9')
        {
            v = Math.Min((v * 10) + (t[i] - '0'), 9223372036854775807m);
            i++;
        }

        if (i == start)
        {
            return 0;
        }

        return (long)(neg ? -v : v);
    }

    /// <summary>The 64-bit integer a pragma argument names when the whole text is an integer, 0 otherwise.</summary>
    internal static long Atoi64Whole(string? text)
        => text is not null && long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v) ? v : 0;

    /// <summary>The boolean a pragma argument names: yes/true/on, no/false/off, or a number (non-zero in its low byte); <paramref name="dflt"/> otherwise.</summary>
    internal static int GetBoolean(string? text, int dflt)
    {
        if (text is null)
        {
            return dflt;
        }

        switch (text.ToLowerInvariant())
        {
            case "yes" or "true" or "on":
                return 1;
            case "no" or "false" or "off":
                return 0;
        }

        return text.Length > 0 && text[0] is >= '0' and <= '9' ? ((byte)Atoi(text) != 0 ? 1 : 0) : dflt;
    }
}

internal sealed partial class Machine
{
    private static readonly Dictionary<string, int> BoolDefaults = new(StringComparer.Ordinal)
    {
        ["automatic_index"] = 1,
        ["cell_size_check"] = 0,
        ["checkpoint_fullfsync"] = 0,
        ["count_changes"] = 0,
        ["defer_foreign_keys"] = 0,
        ["empty_result_callbacks"] = 0,
        ["full_column_names"] = 0,
        ["fullfsync"] = 0,
        ["ignore_check_constraints"] = 0,
        ["legacy_alter_table"] = 0,
        ["query_only"] = 0,
        ["read_uncommitted"] = 0,
        ["recursive_triggers"] = 0,
        ["reverse_unordered_selects"] = 0,
        ["short_column_names"] = 1,
        ["trusted_schema"] = 0,
        ["writable_schema"] = 0,
        ["case_sensitive_like"] = 0,
    };

    private RunResult SetPragmaInt(int d, long v)
    {
        Values.SetInt(ref R[d], v);
        _pc++;
        return RunResult.Done;
    }

    // The pragmas that keep a setting on the connection, and the header fields the application owns (pragma).
    private RunResult DoPragmaVar(ref Instr ins)
    {
        var pc = (PragmaCall)ins.P4!;
        string name = pc.Name;
        string? arg = pc.Arg;
        int d = ins.P2;
        PragmaSettings st = _conn.PragmaSettings;
        string sk = name;
        if (pc.Db != 0 && name is "cache_size" or "locking_mode" or "secure_delete" or "max_page_count")
        {
            // Each other database keeps its own values of the per-database settings; temp starts with no cache of its own.
            sk = (pc.Temp ? "temp." : "db" + pc.Db + ".") + name;
            name = "temp." + name;
        }

        Pager pg = _conn.PagerOf(pc.Db);

        if (BoolDefaults.TryGetValue(name, out int bdef))
        {
            if (arg is null)
            {
                return SetPragmaInt(d, st.Get(name, bdef));
            }

            // The shell runs with the schema locked down, so writable_schema cannot be turned on; the same is done here.
            if (name != "writable_schema")
            {
                st.Set(name, PragmaArgs.GetBoolean(arg, 0));
            }

            _pc++;
            return RunResult.Done;
        }

        switch (name)
        {
            case "analysis_limit":
            {
                if (arg is not null)
                {
                    // The value is read as a whole number, and only its low 32 bits are kept.
                    long v = (int)PragmaArgs.Atoi64Whole(arg);
                    if (v >= 0)
                    {
                        st.Set(name, v);
                    }
                }

                return SetPragmaInt(d, st.Get(name, 0));
            }

            case "mmap_size":
            {
                if (arg is not null)
                {
                    long v = PragmaArgs.Atoi64(arg);
                    st.Set(name, v < 0 ? 0 : Math.Min(v, 0x7fff0000L));
                }

                return SetPragmaInt(d, st.Get(name, 0));
            }

            case "threads":
            {
                if (arg is not null)
                {
                    long v = PragmaArgs.Atoi64Whole(arg);
                    if (v >= 0)
                    {
                        st.Set(name, Math.Min(v, 8));
                    }
                }

                return SetPragmaInt(d, st.Get(name, 0));
            }

            case "soft_heap_limit" or "hard_heap_limit":
            {
                if (arg is not null)
                {
                    long v = PragmaArgs.Atoi64Whole(arg);
                    if (name == "soft_heap_limit" || v >= 0)
                    {
                        st.Set(name, Math.Max(v, 0));
                    }
                }

                return SetPragmaInt(d, st.Get(name, 0));
            }

            case "max_page_count" or "temp.max_page_count":
            {
                if (arg is not null)
                {
                    long v = PragmaArgs.Atoi64Whole(arg);
                    if (v > 0)
                    {
                        st.Set(sk, Math.Min(Math.Max(v, pc.Db == 1 ? 0 : pg.PageCount), 4294967294L));
                    }
                }

                return SetPragmaInt(d, st.Get(sk, 4294967294L));
            }

            case "journal_size_limit":
            {
                if (arg is not null)
                {
                    long v = PragmaArgs.Atoi64(arg);
                    st.Set(name, v < -1 ? -1 : v);
                }

                return SetPragmaInt(d, st.Get(name, -1));
            }

            case "cache_size" or "temp.cache_size":
            {
                if (arg is not null)
                {
                    int v = PragmaArgs.Atoi(arg);
                    st.Set(sk, v);
                    if (pc.Db != 1)
                    {
                        pg.SetCacheSize(v);
                    }
                    _pc++;
                    return RunResult.Done;
                }

                return SetPragmaInt(d, st.Get(sk, pc.Db == 1 ? 0 : -2000));
            }

            case "cache_spill":
            {
                // The spill size is in pages: the cache size converted to pages unless a larger number was asked for.
                long cacheSize = st.Get("cache_size", -2000);
                long cachePages = cacheSize >= 0 ? cacheSize : (-cacheSize * 1024) / (pg.PageSize + 144);
                if (arg is not null)
                {
                    int b = PragmaArgs.GetBoolean(arg, 2);
                    if (b == 0)
                    {
                        st.Set(sk, 0);
                    }
                    else
                    {
                        // A number that is not zero in its low byte also asks for that many pages, never fewer than half the cache.
                        long v = arg.Length > 0 && arg[0] is >= '0' and <= '9' ? PragmaArgs.Atoi(arg) : 0;
                        st.Set(name, v > 0 && v >= cachePages / 2 ? v : cachePages);
                    }

                    _pc++;
                    return RunResult.Done;
                }

                return SetPragmaInt(d, st.Get(name, cachePages));
            }

            case "temp_store":
            {
                if (arg is not null)
                {
                    int v = arg.ToLowerInvariant() switch { "default" => 0, "file" => 1, "memory" => 2, _ => PragmaArgs.Atoi(arg) };
                    if (v is >= 0 and <= 2)
                    {
                        st.Set(name, v);
                    }

                    _pc++;
                    return RunResult.Done;
                }

                return SetPragmaInt(d, st.Get(sk, pc.Db >= 2 ? -2000 : 0));
            }

            case "secure_delete" or "temp.secure_delete":
            {
                if (arg is not null)
                {
                    st.Set(sk, arg.Equals("fast", StringComparison.OrdinalIgnoreCase) ? 2 : PragmaArgs.GetBoolean(arg, 0));
                }

                return SetPragmaInt(d, st.Get(sk, 0));
            }

            case "locking_mode" or "temp.locking_mode":
            {
                if (pc.Db == 1)
                {
                    // A temporary database is always exclusively locked.
                    SetText(ref R[d], ref _own[d], "exclusive");
                    _pc++;
                    return RunResult.Done;
                }

                if (arg is not null)
                {
                    if (arg.Equals("exclusive", StringComparison.OrdinalIgnoreCase))
                    {
                        st.Set(sk, 1);
                    }
                    else if (arg.Equals("normal", StringComparison.OrdinalIgnoreCase))
                    {
                        st.Set(sk, 0);
                    }
                }

                SetText(ref R[d], ref _own[d], st.Get(sk, 0) != 0 ? "exclusive" : "normal");
                _pc++;
                return RunResult.Done;
            }

            case "temp_store_directory":
            {
                if (arg is not null)
                {
                    if (arg.Length > 0 && !Directory.Exists(arg))
                    {
                        throw new OneLiteException(ResultCode.Error, "not a writable directory");
                    }

                    st.TempStoreDirectory = arg;
                    _pc++;
                    return RunResult.Done;
                }

                if (st.TempStoreDirectory.Length == 0)
                {
                    Values.SetNull(ref R[d]);
                    return Skip(ins);
                }

                SetText(ref R[d], ref _own[d], st.TempStoreDirectory);
                _pc++;
                return RunResult.Done;
            }

            case "key" or "hexkey":
                return DoKey(arg, d, hex: name == "hexkey");
            case "rekey" or "hexrekey":
                return DoRekey(arg, d, hex: name == "hexrekey");
            case "cipher_version":
                SetText(ref R[d], ref _own[d], "4.5.6 community (OneLite compatible)");
                _pc++;
                return RunResult.Done;
            case "cipher_provider":
                SetText(ref R[d], ref _own[d], "BCL");
                _pc++;
                return RunResult.Done;
            case "cipher_provider_version":
                SetText(ref R[d], ref _own[d], string.Empty);
                _pc++;
                return RunResult.Done;
            case "cipher_memory_security" or "cipher_use_hmac" or "cipher_default_use_hmac":
                return SetPragmaInt(d, name == "cipher_memory_security" ? 0 : 1);
            case "cipher_page_size" or "kdf_iter" or "cipher_hmac_algorithm" or "cipher_kdf_algorithm" or "cipher_plaintext_header_size" or "cipher_compatibility"
                or "cipher_default_page_size" or "cipher_default_kdf_iter" or "cipher_default_hmac_algorithm" or "cipher_default_kdf_algorithm"
                or "cipher_default_plaintext_header_size" or "cipher_default_compatibility":
                return DoCipherSetting(name, arg, d);
            case "data_version":
                return SetPragmaInt(d, 1);
            case "page_count":
                return SetPragmaInt(d, pg.PageCount);
            case "freelist_count":
            case "default_cache_size":
            case "schema_version":
            case "application_id":
                if (pc.Temp && name == "default_cache_size")
                {
                    if (arg is not null)
                    {
                        st.Set("temp.default_cache_size", Math.Abs(PragmaArgs.Atoi(arg)));
                    }

                    long tv = st.Get("temp.default_cache_size", 0);
                    return SetPragmaInt(d, tv == 0 ? -2000 : tv);
                }

                return DoHeaderPragma(name, arg, d, pc.Db);
            default:
                _pc++;
                return RunResult.Done;
        }
    }

    // A pragma with no row to show: the compiler leaves its ResultRow out when the pragma is a set, and a get of these has none.
    private RunResult Skip(Instr ins)
    {
        _pc++;
        return RunResult.Done;
    }

    // fileformat2 §1.3: freelist page count at offset 36, schema cookie at 40, application id at 68 (the application owns the last).
    private RunResult DoHeaderPragma(string name, string? arg, int d, int db = 0)
    {
        IBtPageSource src = db == 0 ? _conn.Source! : _conn.ContextOf(db).Source;
        Pager pager = _conn.PagerOf(db);
        int off = name switch { "freelist_count" => 36, "schema_version" => 40, "default_cache_size" => 48, _ => 68 };
        if (pager.PageCount == 0)
        {
            return SetPragmaInt(d, name == "default_cache_size" ? -2000 : 0);
        }

        BtStatus s = src.GetPage(1, out PageHandle? h);
        if (s != BtStatus.Ok)
        {
            return s == BtStatus.Pending ? RunResult.Pending : Fail(src.LastError!);
        }

        // schema_version is writable only with writable_schema on, which the shell never allows (see BoolDefaults), so a set is ignored.
        if (arg is not null && name is "application_id" or "default_cache_size")
        {
            s = src.MakeWritable(h!);
            if (s != BtStatus.Ok)
            {
                src.Release(h!);
                return s == BtStatus.Pending ? RunResult.Pending : Fail(src.LastError!);
            }

            // The deprecated default cache size is kept as a magnitude in the header, and also sets this connection's cache.
            int written = name == "default_cache_size" ? Math.Abs(PragmaArgs.Atoi(arg)) : PragmaArgs.Atoi(arg);
            BinaryPrimitives.WriteInt32BigEndian(h!.Data.AsSpan(off, 4), written);
            if (name == "default_cache_size")
            {
                _conn.PragmaSettings.Set("cache_size", written == 0 ? -2000 : written);
                pager.SetCacheSize(written == 0 ? -2000 : written);
            }
        }

        long v = BinaryPrimitives.ReadUInt32BigEndian(h!.Data.AsSpan(off, 4));
        src.Release(h);
        if (name == "default_cache_size")
        {
            return SetPragmaInt(d, v == 0 ? -2000 : (int)v);
        }

        if (name == "application_id" && v > int.MaxValue)
        {
            v -= 1L << 32;
        }

        return SetPragmaInt(d, v);
    }

    private List<string>? _integrityRows;
    private int _integrityPos;

    // PRAGMA integrity_check and quick_check: one row per problem, or "ok". The structural check reads every page of every tree.
    private RunResult DoPragmaIntegrity(ref Instr ins)
    {
        var call = (PragmaCall)ins.P4!;
        if (_integrityRows is null)
        {
            var rows = new List<string>();
            if (_conn.PagerOf(call.Db).PageCount != 0)
            {
                BtContext ctx = call.Db == 0 ? _conn.Context! : _conn.ContextOf(call.Db);
                var trees = new List<BTree> { BTree.Table(ctx, 1) };
                Catalog cat = _conn.Catalog ?? new Catalog();
                foreach (TableInfo t in cat.DictOf(call.Db).Values.Distinct())
                {
                    if (t.Unsupported is not null || t.Root <= 1)
                    {
                        continue;
                    }

                    if (t.WithoutRowid && t.PrimaryKey is { } pk)
                    {
                        var spec = new CursorSpec { Table = t, Index = pk, IsIndexTree = true, Root = t.Root };
                        trees.Add(BTree.Index(ctx, t.Root, _conn.MakeKeyInfo(spec)));
                    }
                    else
                    {
                        trees.Add(BTree.Table(ctx, t.Root));
                    }

                    foreach (IndexInfo ix in t.Indexes)
                    {
                        var spec = new CursorSpec { Table = t, Index = ix, IsIndexTree = true, Root = ix.Root };
                        trees.Add(BTree.Index(ctx, ix.Root, _conn.MakeKeyInfo(spec)));
                    }
                }

                int limit = call.Arg is not null && int.TryParse(call.Arg, out int n) && n > 0 ? n : 100;
                _conn.PagerOf(call.Db).RequireBlockingBackend("integrity_check");
                var chk = new BtIntegrity(ctx, _conn.PagerOf(call.Db).ExecuteSync);
                chk.Check(trees);
                IReadOnlyList<string> problems = chk.Problems;
                if (problems.Count > 0)
                {
                    rows.Add("*** in database " + cat.SchemaName(call.Db) + " ***");
                    rows.AddRange(problems.Take(limit));
                }
            }

            if (rows.Count == 0)
            {
                rows.Add("ok");
            }

            _integrityRows = rows;
            _integrityPos = 0;
        }

        if (_integrityPos >= _integrityRows.Count)
        {
            _integrityRows = null;
            _pc = ins.P2;
            return RunResult.Done;
        }

        SetText(ref R[ins.P1], ref _own[ins.P1], _integrityRows[_integrityPos++]);
        _pc++;
        return RunResult.Done;
    }

    private RunResult NoRow(int d)
    {
        Values.SetNull(ref R[d]);
        _pc++;
        return RunResult.Done;
    }

    private static readonly OneLiteException s_keyError = new(ResultCode.Error,
        "An error occurred with PRAGMA key or rekey. PRAGMA key requires a key of one or more characters. PRAGMA rekey can only be run on an existing encrypted database. Use sqlcipher_export() and ATTACH to convert encrypted/plaintext databases.");

    // PRAGMA key (SQLCipher API): the database is encrypted with the given passphrase or raw key. It takes effect when the file is first read, so
    // the cipher_* settings may still be changed after it. Answers "ok".
    private RunResult DoKey(string? arg, int d, bool hex)
    {
        if (arg is null)
        {
            return NoRow(d);
        }

        if (arg.Length == 0)
        {
            throw new OneLiteException(ResultCode.Error, s_keyError.Message);
        }

        using Storage.Crypto.CipherKey key = Storage.Crypto.CipherKey.Parse(hex && !arg.StartsWith("x'", StringComparison.OrdinalIgnoreCase) ? "x'" + arg + "'" : arg);
        _conn.Pager.TryApplyKey(key, _conn.PragmaSettings.DefaultCipher);
        SetText(ref R[d], ref _own[d], "ok");
        _pc++;
        return RunResult.Done;
    }

    // PRAGMA rekey: re-encrypts the whole database under a new key, crash-safely, inside this statement's write transaction.
    private RunResult DoRekey(string? arg, int d, bool hex)
    {
        Pager pg = _conn.Pager;
        if (arg is null)
        {
            return NoRow(d);
        }

        if (arg.Length == 0 || !pg.IsEncrypted)
        {
            throw new OneLiteException(ResultCode.Error, s_keyError.Message);
        }

        using Storage.Crypto.CipherKey key = Storage.Crypto.CipherKey.Parse(hex && !arg.StartsWith("x'", StringComparison.OrdinalIgnoreCase) ? "x'" + arg + "'" : arg);
        PagerStatus s = pg.Rekey(key);
        if (s == PagerStatus.Pending)
        {
            return RunResult.Pending;
        }

        if (s == PagerStatus.Error)
        {
            Error = pg.Error;
            return RunResult.Error;
        }

        SetText(ref R[d], ref _own[d], "ok");
        _pc++;
        return RunResult.Done;
    }

    // The cipher_* settings (SQLCipher API): the current parameters of an encrypted database, which may be changed after PRAGMA key until the
    // file is first read; cipher_default_* are the parameters the next PRAGMA key starts from. Before a key there is nothing to show.
    private RunResult DoCipherSetting(string name, string? arg, int d)
    {
        PragmaSettings st = _conn.PragmaSettings;
        bool dflt = name.StartsWith("cipher_default_", StringComparison.Ordinal);
        string key = !dflt ? name : name switch
        {
            "cipher_default_page_size" => "cipher_page_size",
            "cipher_default_kdf_iter" => "kdf_iter",
            "cipher_default_hmac_algorithm" => "cipher_hmac_algorithm",
            "cipher_default_kdf_algorithm" => "cipher_kdf_algorithm",
            "cipher_default_plaintext_header_size" => "cipher_plaintext_header_size",
            _ => "cipher_compatibility",
        };

        Storage.Crypto.CipherSettings? cur = dflt ? st.DefaultCipher : _conn.Pager.CipherSettings;
        if (cur is null)
        {
            return NoRow(d);
        }

        if (arg is null)
        {
            switch (key)
            {
                case "cipher_page_size":
                    return SetPragmaInt(d, cur.PageSize);
                case "kdf_iter":
                    return SetPragmaInt(d, cur.KdfIterations);
                case "cipher_plaintext_header_size":
                    return SetPragmaInt(d, cur.PlaintextHeaderSize);
                case "cipher_hmac_algorithm":
                    SetText(ref R[d], ref _own[d], "HMAC_" + DigestName(cur.HmacDigest));
                    _pc++;
                    return RunResult.Done;
                case "cipher_kdf_algorithm":
                    SetText(ref R[d], ref _own[d], "PBKDF2_HMAC_" + DigestName(cur.KdfDigest));
                    _pc++;
                    return RunResult.Done;
                default:
                    return NoRow(d); // cipher_compatibility has no value to read back
            }
        }

        Storage.Crypto.CipherSettings next = cur;
        switch (key)
        {
            case "cipher_page_size":
            {
                int v = PragmaArgs.Atoi(arg);
                if (v is >= 512 and <= 65536 && (v & (v - 1)) == 0)
                {
                    next = cur.WithPageSize(v);
                }

                break;
            }

            case "kdf_iter":
            {
                int v = PragmaArgs.Atoi(arg);
                if (v > 0)
                {
                    next = cur.WithKdfIterations(v);
                }

                break;
            }

            case "cipher_plaintext_header_size":
            {
                int v = PragmaArgs.Atoi(arg);
                if (v is >= 0 and <= 100)
                {
                    next = cur.WithPlaintextHeaderSize(v);
                }

                break;
            }

            case "cipher_hmac_algorithm" when ParseDigest(arg, "HMAC_") is { } hd:
                next = cur.WithDigests(cur.KdfDigest, hd);
                break;
            case "cipher_kdf_algorithm" when ParseDigest(arg, "PBKDF2_HMAC_") is { } kd:
                next = cur.WithDigests(kd, cur.HmacDigest);
                break;
            case "cipher_compatibility":
            {
                int v = PragmaArgs.Atoi(arg);
                if (v is 3 or 4)
                {
                    next = Storage.Crypto.CipherSettings.ForCompatibility(v);
                }

                break;
            }
        }

        if (dflt)
        {
            st.DefaultCipher = next;
        }
        else if (!ReferenceEquals(next, cur))
        {
            _conn.Pager.TryChangeCipherSettings(next);
        }

        return NoRow(d);
    }

    private static string DigestName(Storage.Crypto.CipherDigest d) => d switch
    {
        Storage.Crypto.CipherDigest.Sha1 => "SHA1",
        Storage.Crypto.CipherDigest.Sha256 => "SHA256",
        _ => "SHA512",
    };

    private static Storage.Crypto.CipherDigest? ParseDigest(string text, string prefix)
    {
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return text[prefix.Length..].ToUpperInvariant() switch
        {
            "SHA1" => Storage.Crypto.CipherDigest.Sha1,
            "SHA256" => Storage.Crypto.CipherDigest.Sha256,
            "SHA512" => Storage.Crypto.CipherDigest.Sha512,
            _ => null,
        };
    }
}
