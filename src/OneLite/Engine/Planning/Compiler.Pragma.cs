using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// The pragmas that choose how a database is written (pragma): journal_mode, synchronous, foreign_keys, busy_timeout, wal_autocheckpoint,
// wal_checkpoint, user_version (the application's own counter, header offset 60), and the read-only page_size, auto_vacuum and encoding.
internal sealed partial class Compiler
{
    private int SlotOfSchema(Name? schema) => schema is null ? 0 : Math.Max(_cat.SlotOf(schema.Text), 0);

    internal VmProgram CompilePragma(PragmaStatement ps)
    {
        VmProgram p = CompilePragmaCore(ps);
        int slot = SlotOfSchema(ps.Schema);
        if (slot >= 2 && p is not null)
        {
            // The attached database's transaction starts with the statement; a pragma that changes its header writes.
            string pn = ps.Name.Text.ToLowerInvariant();
            p.ExtraReadMask |= 1 << slot;
            if (ps.Value is not null && pn is "user_version" or "application_id" or "default_cache_size")
            {
                p.ExtraWriteMask |= 1 << slot;
            }
        }

        return p!;
    }

    private VmProgram CompilePragmaCore(PragmaStatement ps)
    {
        string name = ps.Name.Text.ToLowerInvariant();
        if (ps.Schema is { } sch && _cat.SlotOf(sch.Text) < 0)
        {
            throw Err("unknown database " + sch.Text);
        }

        if (CompilePragmaInfo(ps, name) is { } info)
        {
            return info;
        }

        if (name == "mmap_size" && ps.Schema is { } ms && AsciiComparer.Instance.Equals(ms.Text, "temp"))
        {
            // The temporary database has no memory-mapped I/O: the pragma says nothing about it.
            Emit(Op.Halt);
            return new VmProgram { Code = Finish(), RegisterCount = _nReg + 1, ColumnNames = [], ParameterNames = [], Encoding = _enc };
        }

        if (PragmaVars.TryGetValue(name, out (string Col, bool GetRow, bool SetRow) pv))
        {
            return CompilePragmaVar(ps, name, pv);
        }

        if (name is "integrity_check" or "quick_check")
        {
            // pragma: the argument is a table name or an error limit; a table is checked as part of the whole database here.
            if (ps.Value is { Kind: not PragmaValueKind.Number } av && _cat.Find(av.Text) is null && FindIndex(av.Text) is null)
            {
                throw Err("no such table: " + av.Text);
            }

            Emit(Op.Transaction, 0, 0, 0, null, 4);
            int ib = NewReg();
            int again = NewLabel(), fin = NewLabel();
            Mark(again);
            Emit(Op.PragmaIntegrity, ib, fin, 0, new PragmaCall(name, ps.Value?.Text, SlotOfSchema(ps.Schema)));
            Emit(Op.ResultRow, ib, 1);
            Emit(Op.Goto, 0, again);
            Mark(fin);
            Emit(Op.Halt);
            return new VmProgram { Code = Finish(), RegisterCount = _nReg + 1, ColumnNames = [name], ResultBase = ib, ResultCount = 1, ParameterNames = [], Encoding = _enc };
        }

        if (name == "foreign_key_list")
        {
            return CompileForeignKeyList(ps);
        }

        if (name == "foreign_key_check")
        {
            return CompileForeignKeyCheck(ps);
        }

        (int id, string[] cols) = name switch
        {
            "journal_mode" => (1, new[] { "journal_mode" }),
            "synchronous" => (2, new[] { "synchronous" }),
            "foreign_keys" => (3, new[] { "foreign_keys" }),
            "busy_timeout" => (4, new[] { "timeout" }),
            "page_size" => (5, new[] { Encrypted ? "cipher_page_size" : "page_size" }),
            "wal_autocheckpoint" => (6, new[] { "wal_autocheckpoint" }),
            "wal_checkpoint" => (7, new[] { "busy", "log", "checkpointed" }),
            "auto_vacuum" => (8, new[] { "auto_vacuum" }),
            "encoding" => (9, new[] { "encoding" }),
            "user_version" => (10, new[] { "user_version" }),
            "recursive_triggers" => (11, new[] { "recursive_triggers" }),
            _ => (0, []),
        };
        if (id == 0)
        {
            // pragma: a name the library does not know is accepted and does nothing.
            Emit(Op.Halt);
            return new VmProgram { Code = Finish(), RegisterCount = _nReg + 1, ColumnNames = [], ParameterNames = [], Encoding = _enc };
        }

        string? arg = ps.Value is null ? null : ps.Value.Text;
        // Setting user_version writes page 1, so it opens a write transaction (flag 1); every pragma skips the schema-cookie check (flag 4).
        // Flag 32: these pragmas read the connection's settings, not the file, so inside a transaction they do not start the read transaction
        // that the transaction would otherwise keep (observed on the reference: another connection can still write).
        int noHold = id is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 9 ? 32 : 0;
        Emit(Op.Transaction, 0, 0, 0, null, (id == 10 && ps.Value is not null ? 1 | 4 : 4) | noHold);
        int b = NewRegs(cols.Length);
        Emit(Op.PragmaOp, id, b, cols.Length, arg, SlotOfSchema(ps.Schema));
        bool returns = arg is null || id is 1 or 4 or 6 or 7;
        if (id == 7 && ps.Value is null)
        {
            returns = true;
        }

        if (returns)
        {
            Emit(Op.ResultRow, b, cols.Length);
        }

        Emit(Op.Halt);
        return new VmProgram
        {
            Code = Finish(),
            RegisterCount = _nReg + 1,
            ColumnNames = returns ? cols : [],
            ResultBase = b,
            ResultCount = returns ? cols.Length : 0,
            ParameterNames = [],
            Encoding = _enc,
        };
    }

    // name -> (result column, a get returns a row, a set returns a row)
    private static readonly Dictionary<string, (string Col, bool GetRow, bool SetRow)> PragmaVars = BuildPragmaVars();

    private static Dictionary<string, (string, bool, bool)> BuildPragmaVars()
    {
        var m = new Dictionary<string, (string, bool, bool)>(StringComparer.Ordinal);
        foreach (string n in new[]
        {
            "automatic_index", "cell_size_check", "checkpoint_fullfsync", "count_changes", "defer_foreign_keys", "empty_result_callbacks",
            "full_column_names", "fullfsync", "ignore_check_constraints", "legacy_alter_table", "query_only", "read_uncommitted",
            "recursive_triggers", "reverse_unordered_selects", "short_column_names", "trusted_schema", "writable_schema",
            "cache_size", "cache_spill", "temp_store", "default_cache_size", "data_version", "page_count", "freelist_count", "schema_version", "application_id",
        })
        {
            m[n] = (n, true, false);
        }

        m["default_cache_size"] = ("cache_size", true, false);

        foreach (string n in new[] { "analysis_limit", "mmap_size", "threads", "soft_heap_limit", "hard_heap_limit", "max_page_count", "journal_size_limit", "locking_mode", "secure_delete" })
        {
            m[n] = (n, true, true);
        }

        m["case_sensitive_like"] = ("case_sensitive_like", false, false);
        foreach (string n in new[] { "cipher_page_size", "kdf_iter", "cipher_hmac_algorithm", "cipher_kdf_algorithm", "cipher_plaintext_header_size", "cipher_compatibility",
            "cipher_default_page_size", "cipher_default_kdf_iter", "cipher_default_hmac_algorithm", "cipher_default_kdf_algorithm", "cipher_default_plaintext_header_size",
            "cipher_default_compatibility", "cipher_use_hmac", "cipher_default_use_hmac", "cipher_memory_security" })
        {
            m[n] = (n, true, false);
        }

        foreach (string n in new[] { "cipher_version", "cipher_provider", "cipher_provider_version" })
        {
            m[n] = (n, true, false);
        }

        foreach (string n in new[] { "key", "hexkey", "rekey", "hexrekey" })
        {
            m[n] = ("ok", false, true);
        }

        m["temp_store_directory"] = ("temp_store_directory", true, false);
        return m;
    }

    private VmProgram CompilePragmaVar(PragmaStatement ps, string name, (string Col, bool GetRow, bool SetRow) pv)
    {
        string? arg = ps.Value?.Text;
        if (name is "data_version" or "page_count" or "freelist_count")
        {
            arg = null; // read-only here: a value after "=" is ignored and the pragma reports as for a get
        }

        bool set = arg is not null;
        bool writes = set && name is "application_id" or "default_cache_size";
        bool cipher = name.StartsWith("cipher_", StringComparison.Ordinal) || name is "kdf_iter" or "key" or "hexkey" or "rekey" or "hexrekey";
        if (name is "rekey" or "hexrekey")
        {
            Emit(Op.Transaction, 0, 0, 0, null, 1 | 4); // the new key takes over when this write transaction commits
        }
        else if (!cipher)
        {
            Emit(Op.Transaction, 0, 0, 0, null, writes ? 1 | 4 : 4);
        }

        int b = NewReg();
        Emit(Op.PragmaVar, 0, b, 0, new PragmaCall(name, arg, SlotOfSchema(ps.Schema)));
        bool returns = set ? pv.SetRow : pv.GetRow;
        bool answerAfterCommit = returns && name is "rekey" or "hexrekey";
        if (answerAfterCommit)
        {
            Emit(Op.Halt, 1); // commits the new key before the row is handed out
            Emit(Op.ResultRow, b, 1);
        }
        else if (returns)
        {
            if (name == "temp_store_directory" || (cipher && !set))
            {
                int skip = NewLabel();
                Emit(Op.IfNull, b, skip);
                Emit(Op.ResultRow, b, 1);
                Mark(skip);
            }
            else
            {
                Emit(Op.ResultRow, b, 1);
            }
        }

        Emit(Op.Halt);
        return new VmProgram
        {
            Code = Finish(),
            RegisterCount = _nReg + 1,
            ColumnNames = returns ? [pv.Col] : [],
            ResultBase = b,
            ResultCount = returns ? 1 : 0,
            ParameterNames = [],
            Encoding = _enc,
        };
    }
}
