using System.Collections;
using System.Data;
using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

#pragma warning disable CA1010 // the ADO.NET base class fixes the non-generic collection shape
/// <summary>The parameters of a command or batch command.</summary>
public sealed class OneLiteParameterCollection : DbParameterCollection
{
    private readonly List<OneLiteParameter> _items = [];

    /// <inheritdoc />
    public override int Count => _items.Count;

    /// <inheritdoc />
    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    /// <summary>Gets a parameter by position.</summary>
    /// <param name="index">0-based position.</param>
    /// <returns>The parameter.</returns>
    public new OneLiteParameter this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>Gets a parameter by name (with or without prefix).</summary>
    /// <param name="parameterName">The name.</param>
    /// <returns>The parameter.</returns>
    /// <exception cref="ArgumentException">There is no such parameter.</exception>
    public new OneLiteParameter this[string parameterName]
    {
        get => _items[IndexOf(parameterName) is var i and >= 0 ? i : throw new ArgumentException($"parameter '{parameterName}' not found", nameof(parameterName))];
        set => _items[IndexOf(parameterName)] = value;
    }

    /// <summary>Adds a parameter.</summary>
    /// <param name="value">The parameter.</param>
    /// <returns>The parameter.</returns>
    public OneLiteParameter Add(OneLiteParameter value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _items.Add(value);
        return value;
    }

    /// <summary>Adds a named parameter with a value.</summary>
    /// <param name="parameterName">The name, with or without a prefix.</param>
    /// <param name="value">The value.</param>
    /// <returns>The new parameter.</returns>
    public OneLiteParameter AddWithValue(string parameterName, object? value) => Add(new OneLiteParameter(parameterName, value));

    /// <inheritdoc />
    public override int Add(object value)
    {
        int n = _items.Count;
        Add(Cast(value));
        return n;
    }

    /// <inheritdoc />
    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (object o in values)
        {
            Add(Cast(o));
        }
    }

    /// <inheritdoc />
    public override void Clear() => _items.Clear();

    /// <inheritdoc />
    public override bool Contains(object value) => value is OneLiteParameter p && _items.Contains(p);

    /// <inheritdoc />
    public override bool Contains(string value) => IndexOf(value) >= 0;

    /// <inheritdoc />
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    public override int IndexOf(object value) => value is OneLiteParameter p ? _items.IndexOf(p) : -1;

    /// <inheritdoc />
    public override int IndexOf(string parameterName)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        string bare = Strip(parameterName);
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(Strip(_items[i].ParameterName), bare, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));

    /// <inheritdoc />
    public override void Remove(object value) => _items.Remove(Cast(value));

    /// <inheritdoc />
    public override void RemoveAt(int index) => _items.RemoveAt(index);

    /// <inheritdoc />
    public override void RemoveAt(string parameterName)
    {
        int i = IndexOf(parameterName);
        if (i < 0)
        {
            throw new ArgumentException($"parameter '{parameterName}' not found", nameof(parameterName));
        }

        _items.RemoveAt(i);
    }

    /// <inheritdoc />
    protected override DbParameter GetParameter(int index) => _items[index];

    /// <inheritdoc />
    protected override DbParameter GetParameter(string parameterName) => this[parameterName];

    /// <inheritdoc />
    protected override void SetParameter(int index, DbParameter value) => _items[index] = Cast(value);

    /// <inheritdoc />
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = Cast(value);

    private static OneLiteParameter Cast(object value) =>
        value as OneLiteParameter ?? throw new InvalidCastException($"a {value?.GetType().ToString() ?? "null"} is not a OneLiteParameter");

    private static string Strip(string name) => name.Length > 0 && name[0] is '@' or ':' or '$' ? name[1..] : name;

    /// <summary>
    /// Binds the parameters to <paramref name="st"/>. A named SQL parameter (<c>@x</c>, <c>:x</c>, <c>$x</c>) takes the collection parameter with that name, with or without the prefix.
    /// <c>?NNN</c> takes the parameter named exactly <c>?NNN</c>; failing that (and for a plain <c>?</c>) the parameter at that position in the collection.
    /// </summary>
    /// <param name="st">The statement.</param>
    /// <param name="missing">Receives the names of parameters the statement has and the collection lacks.</param>
    internal void BindTo(Statement st, List<string> missing)
    {
        int positional = 0;
        for (int i = 1; i <= st.ParameterCount; i++)
        {
            string? name = st.ParameterName(i);
            if (name is null || (name.Length > 0 && name[0] == '?'))
            {
                int ix = name is null ? -1 : ExactIndex(name);
                int number = name is { Length: > 1 } && int.TryParse(name.AsSpan(1), out int n) ? n - 1 : positional;
                positional++;
                if (ix < 0 && number >= 0 && number < _items.Count)
                {
                    ix = number;
                }

                if (ix < 0)
                {
                    missing.Add(name ?? string.Empty);
                }
                else
                {
                    st.Bind(i, _items[ix].BindValue());
                }

                continue;
            }

            int at = IndexOf(name);
            if (at < 0)
            {
                missing.Add(name);
                continue;
            }

            st.Bind(i, _items[at].BindValue());
        }
    }

    private int ExactIndex(string name)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].ParameterName, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
#pragma warning restore CA1010
