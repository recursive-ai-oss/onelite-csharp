namespace RecursiveAI.OneLite;

/// <summary>One mapped member of a row type, as the generator saw it.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Member">The C# property name.</param>
/// <param name="SqlType">The STRICT column type: INTEGER, REAL, TEXT or BLOB.</param>
/// <param name="NotNull">True if the C# type cannot hold NULL.</param>
/// <param name="IsKey">True if the property is part of the primary key.</param>
/// <param name="Required">True if reading a row without this column is an error (a constructor parameter with no default, or a <c>required</c> member).</param>
/// <param name="CanRead">True if a read row can set it (a constructor parameter or a settable property).</param>
public readonly record struct RowColumn(string Name, string Member, string SqlType, bool NotNull, bool IsKey, bool Required, bool CanRead);

/// <summary>Maps rows to <typeparamref name="T"/> and binds <typeparamref name="T"/> to statement parameters. The build generates these for types marked <see cref="OneLiteRowAttribute"/>.</summary>
/// <typeparam name="T">The row type.</typeparam>
public interface IRowMapper<T>
{
    /// <summary>The table name.</summary>
    string TableName { get; }

    /// <summary>The mapped members in declaration order.</summary>
    IReadOnlyList<RowColumn> Columns { get; }

    /// <summary>True when the key is a single integer property (a rowid alias): an insert with 0 there lets the database choose the key.</summary>
    bool HasAutoKey { get; }

    /// <summary>Finds each member's column in the statement's result.</summary>
    /// <param name="statement">The statement whose result columns are matched.</param>
    /// <returns>The result ordinal per member, or -1 where the result has no such column.</returns>
    /// <exception cref="OneLiteMappingException">A required member has no column.</exception>
    int[] Resolve(Statement statement);

    /// <summary>Builds the object from the current row.</summary>
    /// <param name="statement">A statement positioned on a row.</param>
    /// <param name="ordinals">The result of <see cref="Resolve"/>.</param>
    /// <returns>The object.</returns>
    /// <exception cref="OneLiteMappingException">A value cannot be converted; names the column and the member.</exception>
    T Read(Statement statement, int[] ordinals);

    /// <summary>Binds every mapped member, in <see cref="Columns"/> order, to consecutive parameters.</summary>
    /// <param name="statement">The statement.</param>
    /// <param name="firstIndex">The 1-based index of the first parameter.</param>
    /// <param name="value">The object.</param>
    void Bind(Statement statement, int firstIndex, T value);

    /// <summary>The value of the key member (for <c>Table&lt;T&gt;.GetAsync</c>), as a bindable object; null if the type has no single key.</summary>
    /// <param name="value">The object.</param>
    /// <returns>The key value.</returns>
    object? KeyOf(T value);
}

/// <summary>A row could not be mapped. The message names the column, the member and the type.</summary>
public sealed class OneLiteMappingException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="typeName">The row type.</param>
    /// <param name="column">The column name.</param>
    /// <param name="member">The member name.</param>
    /// <param name="problem">What was wrong.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public OneLiteMappingException(string typeName, string column, string member, string problem, Exception? inner = null)
        : base($"cannot map column '{column}' to {typeName}.{member}: {problem}", inner)
    {
        TypeName = typeName;
        Column = column;
        Member = member;
    }

    /// <summary>The row type.</summary>
    public string TypeName { get; }

    /// <summary>The column name.</summary>
    public string Column { get; }

    /// <summary>The member name.</summary>
    public string Member { get; }
}

/// <summary>The registry the generated mappers add themselves to when their assembly loads.</summary>
public static class RowMappers
{
    private static class Holder<T>
    {
        internal static IRowMapper<T>? Mapper;
    }

    /// <summary>Registers a mapper. Generated code calls this from a module initializer.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="mapper">The mapper.</param>
    public static void Register<T>(IRowMapper<T> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        Holder<T>.Mapper = mapper;
    }

    internal static IRowMapper<T>? Find<T>() => Holder<T>.Mapper;

    /// <summary>The mapper for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <returns>The mapper.</returns>
    /// <exception cref="InvalidOperationException">The type has no generated mapper: mark it with <see cref="OneLiteRowAttribute"/>.</exception>
    public static IRowMapper<T> Get<T>() =>
        Holder<T>.Mapper ?? throw new InvalidOperationException($"{typeof(T)} has no row mapper: mark the type with [OneLiteRow] (the build then generates one)");
}

/// <summary>Helpers the generated mappers call. Public only because generated code lives in the user's assembly.</summary>
public static class RowReader
{
    /// <summary>Matches the members to the result columns by name, ignoring case.</summary>
    /// <param name="statement">The statement.</param>
    /// <param name="columns">The members.</param>
    /// <param name="typeName">The row type's name, for messages.</param>
    /// <returns>The ordinals, -1 for a missing optional member.</returns>
    /// <exception cref="OneLiteMappingException">A required member has no column.</exception>
    public static int[] Resolve(Statement statement, IReadOnlyList<RowColumn> columns, string typeName)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var ord = new int[columns.Count];
        for (int i = 0; i < ord.Length; i++)
        {
            ord[i] = -1;
            for (int c = 0; c < statement.ColumnCount; c++)
            {
                if (string.Equals(statement.ColumnName(c), columns[i].Name, StringComparison.OrdinalIgnoreCase))
                {
                    ord[i] = c;
                    break;
                }
            }

            if (ord[i] < 0 && columns[i].Required)
            {
                throw new OneLiteMappingException(typeName, columns[i].Name, columns[i].Member, "the result has no such column");
            }
        }

        return ord;
    }

    /// <summary>Reads a column that cannot be NULL.</summary>
    /// <typeparam name="T">The member type.</typeparam>
    /// <param name="st">The statement.</param>
    /// <param name="ordinal">The result ordinal.</param>
    /// <param name="typeName">The row type.</param>
    /// <param name="column">The column name the member maps to (the message names the result's own spelling of it).</param>
    /// <param name="member">The member name.</param>
    /// <returns>The value.</returns>
    public static T Get<T>(Statement st, int ordinal, string typeName, string column, string member)
    {
        ArgumentNullException.ThrowIfNull(st);
        try
        {
            return st.GetFieldValue<T>(ordinal);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            throw new OneLiteMappingException(typeName, st.ColumnName(ordinal), member, e.Message, e);
        }
    }

    /// <summary>Reads a nullable reference-type column.</summary>
    /// <typeparam name="T">The member type.</typeparam>
    /// <param name="st">The statement.</param>
    /// <param name="ordinal">The result ordinal.</param>
    /// <param name="typeName">The row type.</param>
    /// <param name="column">The column name.</param>
    /// <param name="member">The member name.</param>
    /// <returns>The value, or null for NULL.</returns>
    public static T? GetRef<T>(Statement st, int ordinal, string typeName, string column, string member)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(st);
        return st.IsNull(ordinal) ? null : Get<T>(st, ordinal, typeName, column, member);
    }

    /// <summary>Reads a nullable value-type column.</summary>
    /// <typeparam name="T">The member's underlying type.</typeparam>
    /// <param name="st">The statement.</param>
    /// <param name="ordinal">The result ordinal.</param>
    /// <param name="typeName">The row type.</param>
    /// <param name="column">The column name.</param>
    /// <param name="member">The member name.</param>
    /// <returns>The value, or null for NULL.</returns>
    public static T? GetNullable<T>(Statement st, int ordinal, string typeName, string column, string member)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(st);
        return st.IsNull(ordinal) ? null : Get<T>(st, ordinal, typeName, column, member);
    }
}
