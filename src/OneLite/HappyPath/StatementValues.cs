using System.Globalization;

namespace RecursiveAI.OneLite;

/// <summary>
/// Converts between .NET values and statement parameters and columns. The happy path and the ADO.NET provider share these rules, which follow
/// the ADO.NET conventions SQLite providers use: <c>DateTime</c> as ISO-8601 text, <c>Guid</c> as upper-case text, <c>bool</c> as 0 or 1,
/// <c>decimal</c> as invariant text, enums as integers.
/// </summary>
public static class StatementValues
{
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";
    private const string DateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";

    /// <summary>Binds a .NET value to a parameter.</summary>
    /// <param name="statement">The statement.</param>
    /// <param name="index">1-based parameter index.</param>
    /// <param name="value">null or <see cref="DBNull"/> bind NULL; integers, <c>bool</c> and enums bind integers; <c>float</c>/<c>double</c> bind reals;
    /// <c>string</c>, <c>char</c>, <c>decimal</c>, <c>Guid</c>, <c>DateTime</c>, <c>DateTimeOffset</c>, <c>TimeSpan</c>, <c>DateOnly</c> and <c>TimeOnly</c> bind text;
    /// <c>byte[]</c>, <c>ReadOnlyMemory&lt;byte&gt;</c> and <c>Memory&lt;byte&gt;</c> bind blobs.</param>
    /// <exception cref="NotSupportedException">The value's type has no parameter form.</exception>
    /// <exception cref="OneLiteException">SQLITE_RANGE for a bad index.</exception>
    public static void Bind(this Statement statement, int index, object? value)
    {
        ArgumentNullException.ThrowIfNull(statement);
        switch (value)
        {
            case null:
            case DBNull:
                statement.BindNull(index);
                break;
            case string s:
                statement.Bind(index, s);
                break;
            case long l:
                statement.Bind(index, l);
                break;
            case int i:
                statement.Bind(index, (long)i);
                break;
            case bool b:
                statement.Bind(index, b ? 1L : 0L);
                break;
            case double d:
                statement.Bind(index, d);
                break;
            case float f:
                statement.Bind(index, (double)f);
                break;
            case short sh:
                statement.Bind(index, (long)sh);
                break;
            case byte by:
                statement.Bind(index, (long)by);
                break;
            case sbyte sb:
                statement.Bind(index, (long)sb);
                break;
            case ushort us:
                statement.Bind(index, (long)us);
                break;
            case uint ui:
                statement.Bind(index, (long)ui);
                break;
            case ulong ul:
                statement.Bind(index, unchecked((long)ul));
                break;
            case byte[] blob:
                statement.Bind(index, blob);
                break;
            case ReadOnlyMemory<byte> rom:
                statement.Bind(index, rom.Span);
                break;
            case Memory<byte> mem:
                statement.Bind(index, mem.Span);
                break;
            case char c:
                statement.Bind(index, c.ToString());
                break;
            case decimal m:
                statement.Bind(index, (m / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)); // drops trailing zeros: 12.50 is stored as 12.5
                break;
            case Guid g:
                statement.Bind(index, g.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());
                break;
            case DateTime dt:
                statement.Bind(index, dt.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                statement.Bind(index, dto.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture));
                break;
            case TimeSpan ts:
                statement.Bind(index, ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case DateOnly dateOnly:
                statement.Bind(index, dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly timeOnly:
                statement.Bind(index, timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                break;
            case Enum e:
                statement.Bind(index, Convert.ToInt64(e, CultureInfo.InvariantCulture));
                break;
            default:
                throw new NotSupportedException($"a parameter of type {value.GetType()} is not supported");
        }
    }

    /// <summary>The current row's column as a boxed .NET value: <c>long</c>, <c>double</c>, <c>string</c>, <c>byte[]</c>, or <see cref="DBNull"/>.</summary>
    /// <param name="statement">A statement positioned on a row.</param>
    /// <param name="ordinal">0-based column index.</param>
    /// <returns>The value.</returns>
    public static object GetValue(this Statement statement, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement.ColumnType(ordinal) switch
        {
            ColumnType.Integral => statement.GetInt64(ordinal),
            ColumnType.Real => statement.GetDouble(ordinal),
            ColumnType.Text => statement.GetString(ordinal)!,
            ColumnType.Blob => statement.GetBlob(ordinal).ToArray(),
            _ => DBNull.Value,
        };
    }

    /// <summary>The current row's column as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A numeric type, <c>bool</c>, <c>char</c>, <c>string</c>, <c>byte[]</c>, <c>decimal</c>, <c>Guid</c>, <c>DateTime</c>,
    /// <c>DateTimeOffset</c>, <c>TimeSpan</c>, <c>DateOnly</c>, <c>TimeOnly</c>, <c>object</c>, or <c>Nullable&lt;&gt;</c> of those.</typeparam>
    /// <param name="statement">A statement positioned on a row.</param>
    /// <param name="ordinal">0-based column index.</param>
    /// <returns>The value; integers convert as the C API does (a real truncates, text uses its numeric prefix).</returns>
    /// <exception cref="InvalidCastException">The column is NULL and <typeparamref name="T"/> cannot hold NULL, or <typeparamref name="T"/> is not supported.</exception>
    /// <exception cref="FormatException">Text that is not a valid <c>Guid</c>, <c>DateTime</c> or <c>decimal</c> and so on.</exception>
    public static T GetFieldValue<T>(this Statement statement, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (typeof(T) == typeof(object))
        {
            return (T)GetValue(statement, ordinal);
        }

        Type? under = Nullable.GetUnderlyingType(typeof(T));
        if (statement.IsNull(ordinal))
        {
            if (under is not null || !typeof(T).IsValueType && typeof(T) != typeof(string) && typeof(T) != typeof(byte[]))
            {
                return default!;
            }

            throw new InvalidCastException($"The data is NULL at ordinal {ordinal}. This method can't be called on NULL values. Check using IsDBNull before calling.");
        }

        if (under is not null)
        {
            return (T)ConvertTo(statement, ordinal, under);
        }

        if (typeof(T) == typeof(long))
        {
            return (T)(object)statement.GetInt64(ordinal);
        }

        if (typeof(T) == typeof(int))
        {
            return (T)(object)unchecked((int)statement.GetInt64(ordinal));
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)statement.GetString(ordinal)!;
        }

        if (typeof(T) == typeof(double))
        {
            return (T)(object)statement.GetDouble(ordinal);
        }

        return (T)ConvertTo(statement, ordinal, typeof(T));
    }

    private static object ConvertTo(Statement st, int i, Type t)
    {
        if (t == typeof(long))
        {
            return st.GetInt64(i);
        }

        if (t == typeof(int))
        {
            return unchecked((int)st.GetInt64(i));
        }

        if (t == typeof(bool))
        {
            return st.GetInt64(i) != 0;
        }

        if (t == typeof(short))
        {
            return unchecked((short)st.GetInt64(i));
        }

        if (t == typeof(byte))
        {
            return unchecked((byte)st.GetInt64(i));
        }

        if (t == typeof(sbyte))
        {
            return unchecked((sbyte)st.GetInt64(i));
        }

        if (t == typeof(ushort))
        {
            return unchecked((ushort)st.GetInt64(i));
        }

        if (t == typeof(uint))
        {
            return unchecked((uint)st.GetInt64(i));
        }

        if (t == typeof(ulong))
        {
            return unchecked((ulong)st.GetInt64(i));
        }

        if (t == typeof(double))
        {
            return st.GetDouble(i);
        }

        if (t == typeof(float))
        {
            return (float)st.GetDouble(i);
        }

        if (t == typeof(string))
        {
            return st.GetString(i)!;
        }

        if (t == typeof(byte[]))
        {
            return st.GetBlob(i).ToArray();
        }

        if (t == typeof(char))
        {
            string s = st.GetString(i)!;
            return s.Length > 0 ? s[0] : throw new InvalidCastException("empty text cannot be a char");
        }

        if (t == typeof(decimal))
        {
            return st.ColumnType(i) == ColumnType.Text
                ? decimal.Parse(st.GetString(i)!, NumberStyles.Float, CultureInfo.InvariantCulture)
                : (decimal)st.GetDouble(i);
        }

        if (t == typeof(Guid))
        {
            return st.ColumnType(i) == ColumnType.Blob ? new Guid(st.GetBlob(i)) : Guid.Parse(st.GetString(i)!);
        }

        if (t == typeof(DateTime))
        {
            return st.ColumnType(i) == ColumnType.Text
                ? DateTime.Parse(st.GetString(i)!, CultureInfo.InvariantCulture)
                : DateTime.FromOADate(0).AddDays(st.GetDouble(i) - 2415018.5);
        }

        if (t == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(st.GetString(i)!, CultureInfo.InvariantCulture);
        }

        if (t == typeof(TimeSpan))
        {
            return TimeSpan.Parse(st.GetString(i)!, CultureInfo.InvariantCulture);
        }

        if (t == typeof(DateOnly))
        {
            return DateOnly.Parse(st.GetString(i)!, CultureInfo.InvariantCulture);
        }

        if (t == typeof(TimeOnly))
        {
            return TimeOnly.Parse(st.GetString(i)!, CultureInfo.InvariantCulture);
        }

        if (t.IsEnum)
        {
            return Enum.ToObject(t, st.GetInt64(i));
        }

        throw new InvalidCastException($"a column cannot be read as {t}");
    }
}
