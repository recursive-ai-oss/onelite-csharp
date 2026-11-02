using System.Data;
using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

/// <summary>
/// A command parameter. Its name may carry the SQL prefix (<c>@x</c>, <c>:x</c>, <c>$x</c>) or not; a parameter named <c>x</c> binds <c>@x</c>, <c>:x</c> and <c>$x</c>.
/// Values bind as <see cref="StatementValues.Bind(Statement, int, object?)"/> describes. Only input parameters exist.
/// </summary>
public sealed class OneLiteParameter : DbParameter
{
    private DbType? _dbType;
    private string _name = string.Empty;
    private string _sourceColumn = string.Empty;
    private object? _value;

    /// <summary>Creates an unnamed parameter with no value.</summary>
    public OneLiteParameter()
    {
    }

    /// <summary>Creates a named parameter with a value.</summary>
    /// <param name="name">The name, with or without a prefix.</param>
    /// <param name="value">The value.</param>
    public OneLiteParameter(string name, object? value)
    {
        _name = name;
        _value = value;
    }

    /// <inheritdoc />
    public override DbType DbType
    {
        get => _dbType ?? InferType(_value);
        set => _dbType = value;
    }

    /// <inheritdoc />
    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new ArgumentException("only input parameters are supported", nameof(value));
            }
        }
    }

    /// <inheritdoc />
    public override bool IsNullable { get; set; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ParameterName
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int Size { get; set; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc />
    public override object? Value
    {
        get => _value;
        set => _value = value;
    }

    /// <inheritdoc />
    public override void ResetDbType() => _dbType = null;

    private static DbType InferType(object? v) => v switch
    {
        null or DBNull => DbType.String,
        string => DbType.String,
        long => DbType.Int64,
        int => DbType.Int32,
        short => DbType.Int16,
        byte => DbType.Byte,
        sbyte => DbType.SByte,
        ushort => DbType.UInt16,
        uint => DbType.UInt32,
        ulong => DbType.UInt64,
        bool => DbType.Boolean,
        double => DbType.Double,
        float => DbType.Single,
        decimal => DbType.Decimal,
        byte[] => DbType.Binary,
        Guid => DbType.Guid,
        DateTime => DbType.DateTime,
        DateTimeOffset => DbType.DateTimeOffset,
        TimeSpan => DbType.Time,
        char => DbType.StringFixedLength,
        _ => DbType.Object,
    };

    /// <summary>The value to bind, converted for an explicit <see cref="DbType"/> when one was set.</summary>
    internal object? BindValue()
    {
        if (_value is null)
        {
            throw new InvalidOperationException("Value must be set.");
        }

        if (_value is double.NaN || _value is float.NaN)
        {
            throw new InvalidOperationException("Cannot store 'NaN' values.");
        }

        if (_value is DBNull || _dbType is null)
        {
            return _value;
        }

        try
        {
            return _dbType switch
            {
                DbType.Int64 => Convert.ToInt64(_value, System.Globalization.CultureInfo.InvariantCulture),
                DbType.Int32 => Convert.ToInt32(_value, System.Globalization.CultureInfo.InvariantCulture),
                DbType.Int16 => Convert.ToInt16(_value, System.Globalization.CultureInfo.InvariantCulture),
                DbType.Double => Convert.ToDouble(_value, System.Globalization.CultureInfo.InvariantCulture),
                DbType.String or DbType.AnsiString or DbType.StringFixedLength or DbType.AnsiStringFixedLength when _value is not string and not char =>
                    Convert.ToString(_value, System.Globalization.CultureInfo.InvariantCulture),
                _ => _value,
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return _value;
        }
    }
}
