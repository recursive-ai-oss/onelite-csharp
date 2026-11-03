using RecursiveAI.OneLite.Planning;

namespace RecursiveAI.OneLite;

/// <summary>What <c>sqlite3_table_column_metadata</c> reports about a table column.</summary>
/// <param name="DeclaredType">The type as written in CREATE TABLE (empty if none).</param>
/// <param name="Collation">The column's collating sequence (BINARY unless the definition names another).</param>
/// <param name="NotNull">True if the column has a NOT NULL constraint.</param>
/// <param name="PrimaryKey">True if the column is part of the primary key.</param>
/// <param name="AutoIncrement">True if the column is an INTEGER PRIMARY KEY declared AUTOINCREMENT.</param>
public readonly record struct ColumnMetadata(string DeclaredType, string Collation, bool NotNull, bool PrimaryKey, bool AutoIncrement);

public sealed partial class Connection
{
    /// <summary>Describes a column of a table in the main or temp database (c3ref table_column_metadata).</summary>
    /// <param name="database">"main", "temp" or null to search main then temp.</param>
    /// <param name="table">The table name.</param>
    /// <param name="column">The column name, or one of rowid, _rowid_, oid for the rowid.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="OneLiteException">SQLITE_ERROR "no such table column: table.column" when there is no such column; the schema cannot be read.</exception>
    public ColumnMetadata GetColumnMetadata(string? database, string table, string column)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);
        EnsureCatalogSync();
        Catalog cat = _catalog ?? throw new OneLiteException(ResultCode.Error, "no such table column: " + table + "." + column);
        TableInfo? t = null;
        if (database is null || database.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            cat.MainTables.TryGetValue(table, out t);
        }

        if (t is null && (database is null || database.Equals("temp", StringComparison.OrdinalIgnoreCase)))
        {
            cat.TempTables.TryGetValue(table, out t);
        }

        if (t is not null)
        {
            for (int i = 0; i < t.Columns.Length; i++)
            {
                ColumnInfo c = t.Columns[i];
                if (string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
                {
                    bool auto = i == t.RowidAlias && cat.Rows.Exists(r => string.Equals(r.Name, table, StringComparison.OrdinalIgnoreCase) && r.Sql is { } sql && sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase));
                    return new ColumnMetadata(c.TypeText, c.CollationName ?? "BINARY", c.NotNull, c.PrimaryKey, auto);
                }
            }

            if (!t.WithoutRowid && (column.Equals("rowid", StringComparison.OrdinalIgnoreCase) || column.Equals("_rowid_", StringComparison.OrdinalIgnoreCase) || column.Equals("oid", StringComparison.OrdinalIgnoreCase)))
            {
                return new ColumnMetadata("INTEGER", "BINARY", false, true, false);
            }
        }

        throw new OneLiteException(ResultCode.Error, "no such table column: " + table + "." + column);
    }
}
