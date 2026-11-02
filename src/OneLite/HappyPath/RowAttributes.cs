namespace RecursiveAI.OneLite;

/// <summary>
/// Marks a class, struct or record that OneLite maps to and from rows. The build generates the mapper (no reflection, so it is safe for trimming and NativeAOT):
/// result columns are matched to constructor parameters and settable properties by name, ignoring case.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class OneLiteRowAttribute : Attribute
{
    /// <summary>The table <c>Table&lt;T&gt;</c> uses. Default: the type name, lower-cased, with an "s" appended (<c>User</c> becomes <c>users</c>).</summary>
    public string? TableName { get; set; }
}

/// <summary>Marks the property that is the table's primary key. Several properties together make a composite key. Without any, a property named <c>Id</c> is the key.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class OneLiteKeyAttribute : Attribute
{
}

/// <summary>Names the column a property maps to, when it differs from the property name.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class OneLiteColumnAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="name">The column name.</param>
    public OneLiteColumnAttribute(string name) => Name = name;

    /// <summary>The column name.</summary>
    public string Name { get; }
}

/// <summary>Leaves a property out of the mapping.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class OneLiteIgnoreAttribute : Attribute
{
}
