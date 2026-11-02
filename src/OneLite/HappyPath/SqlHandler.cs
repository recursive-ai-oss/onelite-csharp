using System.Runtime.CompilerServices;
using System.Text;

namespace RecursiveAI.OneLite;

/// <summary>An SQL statement with its bound values. Built by <see cref="SqlHandler"/>; every value in an interpolated string is a parameter, never text.</summary>
internal readonly struct Query
{
    internal Query(string sql, object?[] args, Action<Statement>? binder = null)
    {
        Sql = sql;
        Args = args;
        Binder = binder;
    }

    internal string Sql { get; }

    internal object?[] Args { get; }

    /// <summary>Extra binding after <see cref="Args"/> (used by <c>Table&lt;T&gt;</c>).</summary>
    internal Action<Statement>? Binder { get; }
}

/// <summary>
/// The compiler builds this from an interpolated string passed to a happy-path method.
/// <b><c>$"..."</c> binds parameters:</b> each <c>{value}</c> becomes a numbered parameter (<c>?1</c>, <c>?2</c>, ...) and the value is bound, so
/// <c>QueryAsync&lt;User&gt;($"select * from users where name = {name}")</c> is safe against SQL injection. Only the literal parts of the string are SQL.
/// A hole stands for one value (a number, string, blob, <c>Guid</c>, date and so on, or null); it cannot be a table or column name or an SQL fragment.
/// </summary>
[InterpolatedStringHandler]
public ref struct SqlHandler
{
    private readonly StringBuilder _sql;
    private readonly List<object?> _args;

    /// <summary>Called by the compiler.</summary>
    /// <param name="literalLength">The length of the literal parts.</param>
    /// <param name="formattedCount">The number of holes.</param>
    public SqlHandler(int literalLength, int formattedCount)
    {
        _sql = new StringBuilder(literalLength + (formattedCount * 3));
        _args = new List<object?>(formattedCount);
    }

    /// <summary>Called by the compiler for the literal text.</summary>
    /// <param name="value">SQL text.</param>
    public void AppendLiteral(string value) => _sql.Append(value);

    /// <summary>Called by the compiler for a hole: the value becomes a parameter.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value to bind.</param>
    public void AppendFormatted<T>(T value)
    {
        _args.Add(value);
        _sql.Append('?').Append(_args.Count);
    }

    /// <summary>Called by the compiler for a hole with a format string. The format is not applied: the value is bound as it is.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value to bind.</param>
    /// <param name="format">Ignored.</param>
    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value);

    internal Query Build() => new(_sql.ToString(), _args.ToArray());
}
