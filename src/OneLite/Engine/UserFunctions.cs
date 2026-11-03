using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite;

/// <summary>
/// A scalar SQL function written in C# (c3ref create_function). It receives the arguments of one call and returns the result.
/// Text and blob values reference buffers that are valid only during the call; copy what you keep. Throw
/// <see cref="OneLiteException"/> to fail the statement with that code and message; any other exception fails it with SQLITE_ERROR.
/// </summary>
/// <param name="args">The argument values, in the database's text encoding.</param>
/// <returns>The result value.</returns>
public delegate Mem UserScalarFunction(ReadOnlySpan<Mem> args);

/// <summary>The running state of one group of a user-defined aggregate function (c3ref create_function: <see cref="Accumulate"/> is xStep and <see cref="GetResult"/> is xFinal).</summary>
public interface IUserAggregate
{
    /// <summary>Adds one row to the group.</summary>
    /// <param name="args">The argument values of the row.</param>
    void Accumulate(ReadOnlySpan<Mem> args);

    /// <summary>Ends the group and returns the aggregate's value (the value for an empty group is the implementation's choice).</summary>
    /// <returns>The result value.</returns>
    Mem GetResult();
}

/// <summary>Conversions of <see cref="Mem"/> values with the rules of <c>sqlite3_value_int64</c>, <c>sqlite3_value_double</c> and <c>sqlite3_value_text</c> (datatype3).</summary>
public static class MemConversions
{
    /// <summary>The value as a 64-bit integer: reals truncate, text and blobs use their numeric prefix, NULL is 0.</summary>
    /// <param name="m">The value.</param>
    /// <returns>The integer.</returns>
    public static long ToInt64(in Mem m) => m.Kind == StorageClass.Integral ? m.IntValue : Values.ToInt(m);

    /// <summary>The value as a double, converted as <c>sqlite3_value_double</c> does.</summary>
    /// <param name="m">The value.</param>
    /// <returns>The double.</returns>
    public static double ToDouble(in Mem m) => m.Kind == StorageClass.Real ? m.RealValue : Values.ToReal(m);

    /// <summary>The value as UTF-8 text or blob bytes; numbers are rendered the way SQLite renders them; NULL gives an empty array.</summary>
    /// <param name="m">The value.</param>
    /// <returns>A new array holding the bytes.</returns>
    public static byte[] ToBytes(in Mem m)
    {
        if (m.Kind is StorageClass.Text or StorageClass.Blob)
        {
            return m.Bytes.ToArray();
        }

        if (m.Kind == StorageClass.Null)
        {
            return [];
        }

        var buf = new byte[Values.MaxRealText];
        int n = m.Kind == StorageClass.Integral ? Values.FormatInt(m.IntValue, buf) : Values.FormatReal(m.RealValue, buf);
        return buf.AsSpan(0, n).ToArray();
    }
}

public sealed partial class Connection
{
    private readonly Dictionary<(string Name, int Args), FuncDef> _userFunctions = [];

    /// <summary>Registers, replaces or (with a null <paramref name="function"/>) removes a scalar function on this connection, like <c>sqlite3_create_function</c>.
    /// A user function hides a built-in of the same name and argument count. Statements prepared earlier keep the function they were compiled with.</summary>
    /// <param name="name">The function name; ASCII case-insensitive.</param>
    /// <param name="argumentCount">The number of arguments, or -1 for any number.</param>
    /// <param name="function">The implementation, or null to remove the function.</param>
    /// <param name="deterministic">True if the function always returns the same result for the same arguments (SQLITE_DETERMINISTIC); only such functions may appear in a partial index's WHERE clause. Default false.</param>
    /// <exception cref="OneLiteException">SQLITE_MISUSE for an empty name or an argument count below -1 or above 127.</exception>
    public void CreateFunction(string name, int argumentCount, UserScalarFunction? function, bool deterministic = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || argumentCount is < -1 or > 127)
        {
            throw new OneLiteException(ResultCode.Misuse, "bad parameter or other API misuse");
        }

        (string, int) key = (name.ToUpperInvariant(), argumentCount);
        if (function is null)
        {
            _userFunctions.Remove(key);
        }
        else
        {
            _userFunctions[key] = new FuncDef(name, argumentCount < 0 ? 0 : argumentCount, argumentCount < 0 ? int.MaxValue : argumentCount, FuncId.Length) { User = function, Deterministic = deterministic };
        }

        _programs.Clear();
    }

    private readonly Dictionary<(string Name, int Args), Func<IUserAggregate>> _userAggregates = [];

    /// <summary>Registers, replaces or (with a null <paramref name="factory"/>) removes an aggregate function on this connection, like <c>sqlite3_create_function</c> with xStep and xFinal.
    /// <paramref name="factory"/> is called once for every group of every statement that uses the aggregate.</summary>
    /// <param name="name">The function name; ASCII case-insensitive.</param>
    /// <param name="argumentCount">The number of arguments, or -1 for any number.</param>
    /// <param name="factory">Makes the state of one group, or null to remove the aggregate.</param>
    /// <exception cref="OneLiteException">SQLITE_MISUSE for an empty name or an argument count below -1 or above 127.</exception>
    public void CreateAggregate(string name, int argumentCount, Func<IUserAggregate>? factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || argumentCount is < -1 or > 127)
        {
            throw new OneLiteException(ResultCode.Misuse, "bad parameter or other API misuse");
        }

        (string, int) key = (name.ToUpperInvariant(), argumentCount);
        if (factory is null)
        {
            _userAggregates.Remove(key);
        }
        else
        {
            _userAggregates[key] = factory;
        }

        _programs.Clear();
    }

    internal Func<IUserAggregate>? FindUserAggregate(string name, int argumentCount)
    {
        if (_userAggregates.Count == 0)
        {
            return null;
        }

        string up = name.ToUpperInvariant();
        return _userAggregates.TryGetValue((up, argumentCount), out Func<IUserAggregate>? d) || _userAggregates.TryGetValue((up, -1), out d) ? d : null;
    }

    /// <summary>Finds a user function for a call with <paramref name="argumentCount"/> arguments: an exact count first, then a variadic one.</summary>
    internal FuncDef? FindUserFunction(string name, int argumentCount)
    {
        if (_userFunctions.Count == 0)
        {
            return null;
        }

        string up = name.ToUpperInvariant();
        return _userFunctions.TryGetValue((up, argumentCount), out FuncDef? d) || _userFunctions.TryGetValue((up, -1), out d) ? d : null;
    }

    /// <summary>True when a user function registered under <paramref name="name"/> is not deterministic (or the name is a built-in that can change between calls).</summary>
    internal bool IsNonDeterministic(string name)
    {
        string up = name.ToUpperInvariant();
        foreach (KeyValuePair<(string Name, int Args), FuncDef> kv in _userFunctions)
        {
            if (kv.Key.Name == up && !kv.Value.Deterministic)
            {
                return true;
            }
        }

        return up is "RANDOM" or "RANDOMBLOB" or "CHANGES" or "TOTAL_CHANGES" or "LAST_INSERT_ROWID";
    }

    /// <summary>True when some user function (any argument count) is registered under <paramref name="name"/>.</summary>
    internal bool HasUserFunctionNamed(string name)
    {
        string up = name.ToUpperInvariant();
        foreach ((string n, int _) in _userFunctions.Keys)
        {
            if (n == up)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs a user function and stores its result in <paramref name="dst"/> (the register's own buffer is <paramref name="own"/>).</summary>
    internal static void CallUser(UserScalarFunction f, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        Mem res;
        try
        {
            res = f(args);
        }
        catch (OneLiteException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new OneLiteException(ResultCode.Error, e.Message, e);
        }

        switch (res.Kind)
        {
            case StorageClass.Integral:
                Values.SetInt(ref dst, res.IntValue);
                break;
            case StorageClass.Real:
                Values.SetReal(ref dst, res.RealValue);
                break;
            case StorageClass.Text:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, res.Bytes);
                break;
            case StorageClass.Blob:
                Values.SetBytes(ref dst, ref own, StorageClass.Blob, res.Bytes);
                break;
            default:
                Values.SetNull(ref dst);
                break;
        }
    }

    private volatile bool _interrupt;

    internal bool InterruptRequested => _interrupt;

    /// <summary>Makes the statement that is running on this connection stop at its next row or loop step with SQLITE_INTERRUPT (c3ref interrupt).
    /// Safe to call from another thread. A call while nothing is running is ignored by the next statement that starts.</summary>
    public void Interrupt() => _interrupt = true;

    internal OneLiteException TakeInterrupt()
    {
        _interrupt = false;
        return new OneLiteException(ResultCode.Interrupt, "interrupted");
    }

    /// <summary>Forgets an interrupt that was requested while nothing was running, so it does not stop the next statement.</summary>
    public void ClearInterrupt() => _interrupt = false;

    // A user collation gets a slot for as long as the connection lives; CollKind values from 3 up index this list. A name keeps its slot when it is
    // registered again, so compiled programs that refer to the slot see the new comparison.
    private readonly List<(string Name, CollationFunction? Compare)> _userCollations = [];

    /// <summary>Registers a collating sequence on this connection (c3ref create_collation); null removes it. Registering a name again replaces the comparison.
    /// The built-in BINARY, NOCASE and RTRIM cannot be replaced.</summary>
    /// <param name="name">The collation name; ASCII case-insensitive.</param>
    /// <param name="comparison">Compares two UTF-8 strings: negative, zero or positive.</param>
    /// <exception cref="OneLiteException">SQLITE_MISUSE for an empty name or a built-in name.</exception>
    public void CreateCollation(string name, CollationFunction? comparison)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Equals("BINARY", StringComparison.OrdinalIgnoreCase) || name.Equals("NOCASE", StringComparison.OrdinalIgnoreCase) || name.Equals("RTRIM", StringComparison.OrdinalIgnoreCase))
        {
            throw new OneLiteException(ResultCode.Misuse, "bad parameter or other API misuse");
        }

        for (int i = 0; i < _userCollations.Count; i++)
        {
            if (string.Equals(_userCollations[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _userCollations[i] = (_userCollations[i].Name, comparison);
                _programs.Clear();
                return;
            }
        }

        if (comparison is not null)
        {
            if (_userCollations.Count >= 250)
            {
                throw new OneLiteException(ResultCode.Misuse, "too many collations");
            }

            _userCollations.Add((name, comparison));
        }

        _programs.Clear();
    }

    /// <summary>The CollKind slot of a registered user collation, or -1.</summary>
    internal int UserCollationSlot(string name)
    {
        for (int i = 0; i < _userCollations.Count; i++)
        {
            if (_userCollations[i].Compare is not null && string.Equals(_userCollations[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 3;
            }
        }

        return -1;
    }

    internal CollationFunction? UserCollation(string name)
    {
        int slot = UserCollationSlot(name);
        return slot < 0 ? null : (a, b) => _userCollations[slot - 3].Compare!(a, b);
    }

    internal int CompareUserCollation(int slot, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        CollationFunction? f = (uint)(slot - 3) < (uint)_userCollations.Count ? _userCollations[slot - 3].Compare : null;
        if (f is null)
        {
            throw new OneLiteException(ResultCode.ErrorMissingCollSeq, "no such collation sequence: " + (slot - 3 < _userCollations.Count ? _userCollations[slot - 3].Name : slot.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return f(a, b);
    }

    /// <summary>Called when a transaction is about to commit; return true to turn the commit into a rollback (c3ref commit_hook).</summary>
    public Func<bool>? CommitHook { get; set; }

    /// <summary>Called after a transaction rolls back (c3ref commit_hook, rollback_hook).</summary>
    public Action? RollbackHook { get; set; }

    /// <summary>Called after a row of a rowid table is inserted, updated or deleted (c3ref update_hook): operation (18 INSERT, 9 DELETE, 23 UPDATE), schema name, table name, rowid.</summary>
    public Action<int, string, string, long>? UpdateHook { get; set; }
}
