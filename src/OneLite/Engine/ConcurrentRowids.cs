using System.Collections.Concurrent;
using RecursiveAI.OneLite.Storage.BTree;

namespace RecursiveAI.OneLite;

/// <summary>
/// Rowid allocation for concurrent transactions (docs/concurrency.md, "Rowids"). A concurrent transaction takes a rowid above the largest one in
/// its own view, from a block reserved in a process-wide hint, so that concurrent appenders in one process do not choose the same rowid. The
/// hint is only a hint: commit still rejects a rowid that is taken, which covers other processes, a live <c>sqlite3</c> writer and a restart.
/// </summary>
internal static class ConcurrentRowids
{
    private sealed class Hint
    {
        public long Next;
        public int Streak;
    }

    private static readonly ConcurrentDictionary<string, Dictionary<uint, Hint>> s_files = new(StringComparer.Ordinal);

    private static Hint Get(string file, uint root, out Dictionary<uint, Hint> table)
    {
        table = s_files.GetOrAdd(file, _ => []);
        if (!table.TryGetValue(root, out Hint? h))
        {
            h = new Hint();
            table[root] = h;
        }

        return h;
    }

    /// <summary>
    /// The next rowid for <paramref name="root"/>, or 0 if the table is so close to the top of the rowid range that the ordinary rule applies.
    /// </summary>
    /// <param name="file">The database's full path.</param>
    /// <param name="root">The table's root page.</param>
    /// <param name="treeMax">The largest rowid the transaction sees (its snapshot plus its own rows), or the AUTOINCREMENT counter if larger.</param>
    /// <param name="trace">The transaction's trace, which keeps its blocks.</param>
    internal static long Allocate(string file, uint root, long treeMax, ConcTrace trace)
    {
        if (treeMax > long.MaxValue - (1L << 24))
        {
            return 0;
        }

        trace.Blocks ??= [];
        trace.Blocks.TryGetValue(root, out RowidBlock b);
        if (b.Next < b.End && b.Next > treeMax)
        {
            long id = b.Next++;
            trace.Blocks[root] = b;
            return id;
        }

        Hint h = Get(file, root, out Dictionary<uint, Hint> table);
        int size = b.Size == 0 ? 1 : Math.Min(b.Size * 2, 64);
        long start;
        lock (table)
        {
            start = Math.Max(h.Next, treeMax + 1);
            h.Next = start + size;
        }

        trace.Blocks[root] = new RowidBlock { Next = start + 1, End = start + size, Size = size };
        return start;
    }

    /// <summary>A commit was refused because a rowid it inserted was taken: skip ahead of it, further each time in a row.</summary>
    internal static void NoteCollision(string file, ConcConflict c)
    {
        if (!c.RowidTaken)
        {
            return;
        }

        Hint h = Get(file, c.Root, out Dictionary<uint, Hint> table);
        lock (table)
        {
            long jump = Random.Shared.NextInt64(0, 1L << Math.Min(4 + h.Streak, 20));
            h.Next = Math.Max(h.Next, c.Rowid + 1) + jump;
            h.Streak++;
        }
    }

    /// <summary>A concurrent transaction on <paramref name="file"/> committed.</summary>
    internal static void NoteCommit(string file)
    {
        if (s_files.TryGetValue(file, out Dictionary<uint, Hint>? table))
        {
            lock (table)
            {
                foreach (Hint h in table.Values)
                {
                    h.Streak = 0;
                }
            }
        }
    }
}
