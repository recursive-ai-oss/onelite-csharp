using System.Data.Common;
using System.Globalization;

namespace RecursiveAI.OneLite.Data;

/// <summary>How a connection opens its database (<c>Mode=</c> in the connection string).</summary>
public enum OneLiteOpenMode
{
    /// <summary>Open for reading and writing; create the file if it does not exist. The default.</summary>
    ReadWriteCreate,

    /// <summary>Open for reading and writing; the file must exist.</summary>
    ReadWrite,

    /// <summary>Open for reading only. Writes fail with SQLITE_READONLY.</summary>
    ReadOnly,

    /// <summary>A database that lives in memory only.</summary>
    Memory,
}

/// <summary>Whether in-memory connections share one database (<c>Cache=</c> in the connection string).</summary>
public enum OneLiteCacheMode
{
    /// <summary>The default: a named in-memory database is private to its connection.</summary>
    Default,

    /// <summary>A private database per connection.</summary>
    Private,

    /// <summary>Connections that use the same data source name in <see cref="OneLiteOpenMode.Memory"/> share one in-memory database.</summary>
    Shared,
}

#pragma warning disable CA1010 // the ADO.NET base class fixes the non-generic collection shape
/// <summary>
/// Builds and parses OneLite connection strings. Keys (case-insensitive): <c>Data Source</c> (also <c>Filename</c>), <c>Mode</c>, <c>Cache</c>,
/// <c>Password</c>, <c>Vfs</c>, <c>Foreign Keys</c>, <c>Default Timeout</c> (seconds, default 30, used as the busy timeout), <c>Pooling</c> (accepted, ignored).
/// </summary>
public sealed class OneLiteConnectionStringBuilder : DbConnectionStringBuilder
{
    private static readonly string[] s_dataSource = ["Data Source", "DataSource", "Filename"];

    /// <summary>Creates an empty builder.</summary>
    public OneLiteConnectionStringBuilder()
    {
    }

    /// <summary>Parses a connection string.</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <exception cref="ArgumentException">The string is malformed.</exception>
    public OneLiteConnectionStringBuilder(string connectionString) => ConnectionString = connectionString;

    private string? Get(params string[] keys)
    {
        foreach (string k in keys)
        {
            if (TryGetValue(k, out object? v) && v is not null)
            {
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private void Set(string key, string? value, params string[] aliases)
    {
        foreach (string a in aliases)
        {
            Remove(a);
        }

        if (value is not null)
        {
            this[key] = value;
        }
    }

    /// <summary>The database file, ":memory:", or (with <c>Mode=Memory</c>) a name for a shared in-memory database. Default: empty.</summary>
    public string DataSource
    {
        get => Get(s_dataSource) ?? string.Empty;
        set => Set("Data Source", value, s_dataSource);
    }

    /// <summary>The open mode. Default <see cref="OneLiteOpenMode.ReadWriteCreate"/>.</summary>
    /// <exception cref="ArgumentException">The stored text is not a mode.</exception>
    public OneLiteOpenMode Mode
    {
        get => Get("Mode") is { } s ? Enum.Parse<OneLiteOpenMode>(s, true) : OneLiteOpenMode.ReadWriteCreate;
        set => Set("Mode", value.ToString(), "Mode");
    }

    /// <summary>The cache mode. Default <see cref="OneLiteCacheMode.Default"/>.</summary>
    public OneLiteCacheMode Cache
    {
        get => Get("Cache") is { } s ? Enum.Parse<OneLiteCacheMode>(s, true) : OneLiteCacheMode.Default;
        set => Set("Cache", value.ToString(), "Cache");
    }

    /// <summary>The passphrase of an encrypted database (SQLCipher-compatible format), or a raw key written <c>x'64 hex digits'</c> (<c>x'96 hex digits'</c> adds the salt), or null for none.</summary>
    public string? Password
    {
        get => Get("Password");
        set => Set("Password", value, "Password");
    }

    /// <summary><c>Foreign Keys</c>: true or false to set <c>PRAGMA foreign_keys</c> when the connection opens; null leaves SQLite's default (off).</summary>
    public bool? ForeignKeys
    {
        get => Get("Foreign Keys") is { } s ? bool.Parse(s) : null;
        set => Set("Foreign Keys", value?.ToString(), "Foreign Keys");
    }

    /// <summary>Seconds a statement waits for a lock before failing with SQLITE_BUSY. Default 30.</summary>
    public int DefaultTimeout
    {
        get => Get("Default Timeout") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 30;
        set => Set("Default Timeout", value.ToString(CultureInfo.InvariantCulture), "Default Timeout");
    }

    /// <summary>The name of a storage backend registered with <see cref="OneLiteConnection.RegisterVfs"/>; null (the default) means local files.</summary>
    public string? Vfs
    {
        get => Get("Vfs");
        set => Set("Vfs", value, "Vfs");
    }

    /// <summary>Accepted for compatibility; connections are not pooled. Default true.</summary>
    public bool Pooling
    {
        get => Get("Pooling") is not { } s || bool.Parse(s);
        set => Set("Pooling", value.ToString(), "Pooling");
    }
}
#pragma warning restore CA1010
