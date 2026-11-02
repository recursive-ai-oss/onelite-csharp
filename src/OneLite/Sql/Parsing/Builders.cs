using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql.Parsing;

/// <summary>
/// Reusable list builders, so that collecting the children of a node allocates only the final array of the tree. Builders nest (a list of
/// expressions inside a list of expressions), so each element type keeps a small stack of them per thread.
/// </summary>
internal static class Builders<T>
{
    private const int Depth = 16;

    [ThreadStatic]
    private static ImmutableArray<T>.Builder?[]? s_free;

    [ThreadStatic]
    private static int s_count;

    /// <summary>An empty builder; hand it back through <see cref="Finish"/>.</summary>
    internal static ImmutableArray<T>.Builder Rent()
    {
        if (s_count > 0 && s_free![--s_count] is { } b)
        {
            s_free[s_count] = null;
            return b;
        }

        return ImmutableArray.CreateBuilder<T>(4);
    }

    /// <summary>The builder's items as an immutable array (a fresh array, or the shared empty one), and the builder back to the pool.</summary>
    internal static ImmutableArray<T> Finish(ImmutableArray<T>.Builder b)
    {
        ImmutableArray<T> result = b.ToImmutable();
        b.Clear();
        if (b.Capacity <= 256)
        {
            s_free ??= new ImmutableArray<T>.Builder?[Depth];
            if (s_count < Depth)
            {
                s_free[s_count++] = b;
            }
        }

        return result;
    }
}
