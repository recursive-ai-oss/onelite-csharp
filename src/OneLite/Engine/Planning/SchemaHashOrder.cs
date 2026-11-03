using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneLite.Planning;

/// <summary>
/// The order in which the reference walks the tables of a schema when it analyses all of them. It keeps the tables in a hash table keyed on the
/// lower-cased name and walks it from its head, so the order is not the creation order, and the rowids of the sqlite_stat1 rows made by a plain
/// ANALYZE follow it. Observed against the pinned shell (the order is not documented): a multiplicative hash of the name (the golden-ratio constant
/// 0x9e3779b1, applied after each byte is added), a new entry goes to the head of the list, or in front of the first entry already in its bucket,
/// and the table is rebuilt (the list walked head to tail, each entry re-inserted) at the 5th entry into 15 buckets and at the 31st into 64.
/// Up to four entries (sqlite_master counts as the first) that is just the reverse of creation order; with more, entries whose names fall into
/// one bucket sit together. The model reproduces the shell for every schema of up to three tables and many larger ones, but not all: some larger
/// schemas differ in the order of the rows (never in their content).
/// </summary>
internal static class SchemaHashOrder
{
    private const uint Multiplier = 0x9e3779b1;

    /// <summary>Orders items, given in creation order, as the reference's schema hash table lists them.</summary>
    internal static List<T> Order<T>(List<T> creationOrder, Func<T, string> name)
    {
        var list = new LinkedList<(T Item, uint Hash)>();
        var first = new Dictionary<uint, LinkedListNode<(T Item, uint Hash)>>();
        uint size = 0;
        int count = 0;

        void Insert(T item, uint hash)
        {
            if (size == 0)
            {
                list.AddFirst((item, hash));
                return;
            }

            uint bucket = hash % size;
            if (first.TryGetValue(bucket, out LinkedListNode<(T Item, uint Hash)>? head))
            {
                list.AddBefore(head, (item, hash));
            }
            else
            {
                first[bucket] = list.AddFirst((item, hash));
            }
        }

        void Rebuild(uint newSize)
        {
            var old = new List<(T Item, uint Hash)>(list);
            list.Clear();
            first.Clear();
            size = newSize;
            foreach ((T item, uint hash) in old)
            {
                Insert(item, hash);
            }
        }

        void Add(T item, string key)
        {
            count++;
            if (count == 5)
            {
                Rebuild(15);
            }
            else if (count == 31)
            {
                Rebuild(64);
            }

            Insert(item, Hash(key));
        }

        // the schema table is entered before any table is read
        Add(default!, "sqlite_master");
        foreach (T item in creationOrder)
        {
            Add(item, name(item));
        }

        var result = new List<T>(creationOrder.Count);
        foreach ((T item, uint _) in list)
        {
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static uint Hash(string name)
    {
        uint h = 0;
        foreach (byte c in Encoding.UTF8.GetBytes(name))
        {
            h = unchecked((h + (c is >= (byte)'A' and <= (byte)'Z' ? (uint)(c + 32) : c)) * Multiplier);
        }

        return h;
    }
}
