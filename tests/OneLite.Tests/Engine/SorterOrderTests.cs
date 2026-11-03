using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// The in-memory sorter orders rows by the first key's cached number (integers, floats, the start of text and blobs) and settles ties with
/// the full comparison. Whatever shape the keys have, the order must be the one the general comparison gives, ties in insertion order.
/// </summary>
public sealed class SorterOrderTests
{
    private static Mem[] KeyColumn(string kind, Random rng, int n)
    {
        var keys = new Mem[n];
        for (int i = 0; i < n; i++)
        {
            keys[i] = kind switch
            {
                "int" => Mem.FromInt(rng.Next(-20, 20) * (rng.Next(4) == 0 ? 1_000_000_007L : 1)),
                "bigint" => Mem.FromInt(long.MaxValue - rng.Next(6)),
                "real" => Mem.FromReal(rng.Next(5) switch { 0 => 0.0, 1 => -0.0, 2 => rng.Next(-5, 5), _ => rng.Next(-1000, 1000) / 8.0 }),
                "intreal" => rng.Next(2) == 0
                    ? Mem.FromInt((1L << 53) + rng.Next(-2, 3))
                    : Mem.FromReal(rng.Next(3) == 0 ? 9007199254740992.0 : rng.Next(-3, 3)),
                "text" => Mem.FromTextBytes(Encoding.UTF8.GetBytes(Word(rng))),
                "blob" => Mem.FromBlob(Encoding.UTF8.GetBytes(Word(rng))),
                "nulls" => rng.Next(3) == 0 ? default : Mem.FromInt(rng.Next(0, 6)),
                "textnulls" => rng.Next(3) == 0 ? default : Mem.FromTextBytes(Encoding.UTF8.GetBytes(Word(rng))),
                _ => rng.Next(6) switch
                {
                    0 => default,
                    1 => Mem.FromInt(rng.Next(-2, 3)),
                    2 => Mem.FromReal(rng.Next(-2, 3) / 2.0),
                    3 => Mem.FromTextBytes(Encoding.UTF8.GetBytes(Word(rng))),
                    4 => Mem.FromBlob(Encoding.UTF8.GetBytes(Word(rng))),
                    _ => Mem.FromInt(long.MinValue + rng.Next(3)),
                },
            };
        }

        return keys;
    }

    // Words that share long starts and differ in length and in zero bytes, since the cached start is 16 bytes zero-padded.
    private static string Word(Random rng)
    {
        string[] starts = ["", "a", "ab", "abcdefghijklmnop", "abcdefghijklmnopq", "abcdefghijklmnopqrstu", "name-123456789-"];
        string s = starts[rng.Next(starts.Length)];
        int tail = rng.Next(4);
        for (int i = 0; i < tail; i++)
        {
            s += (char)rng.Next(0, 4) switch { '\0' => '\0', '\u0001' => 'a', '\u0002' => 'b', _ => 'z' };
        }

        return s;
    }

    private static int Cmp(Mem[] a, Mem[] b, SortTerm[] terms)
    {
        for (int i = 0; i < terms.Length; i++)
        {
            bool na = a[i].Kind == StorageClass.Null, nb = b[i].Kind == StorageClass.Null;
            int c;
            if (na || nb)
            {
                c = na && nb ? 0 : na == terms[i].NullsFirst ? -1 : 1;
            }
            else
            {
                c = Values.Compare(a[i], b[i], terms[i].Collation, TextEncoding.Utf8);
                if (terms[i].Descending)
                {
                    c = -c;
                }
            }

            if (c != 0)
            {
                return c < 0 ? -1 : 1;
            }
        }

        return 0;
    }

    [Theory]
    [InlineData("int")]
    [InlineData("bigint")]
    [InlineData("real")]
    [InlineData("intreal")]
    [InlineData("text")]
    [InlineData("blob")]
    [InlineData("nulls")]
    [InlineData("textnulls")]
    [InlineData("mixed")]
    public void TheOrderIsTheGeneralComparisonsWithTiesInInsertionOrder(string kind)
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var rng = new Random(seed * 7919);
            foreach (int n in new[] { 0, 1, 2, 31, 32, 33, 300, 2000 })
            {
                foreach (bool desc in new[] { false, true })
                {
                    foreach (bool nullsFirst in new[] { false, true })
                    {
                        foreach (int keys in new[] { 1, 2 })
                        {
                            CheckOnce(kind, rng, n, desc, nullsFirst, keys);
                        }
                    }
                }
            }
        }
    }

    private static void CheckOnce(string kind, Random rng, int n, bool desc, bool nullsFirst, int nKeys)
    {
        Mem[] first = KeyColumn(kind, rng, n);
        var terms = new SortTerm[nKeys];
        terms[0] = new SortTerm(desc, nullsFirst, CollKind.Binary);
        if (nKeys == 2)
        {
            terms[1] = new SortTerm(!desc, !nullsFirst, CollKind.Binary);
        }

        var rows = new List<Mem[]>();
        using var sorter = new RecordSorter(terms, TextEncoding.Utf8, false, long.MaxValue);
        for (int i = 0; i < n; i++)
        {
            Mem second = Mem.FromInt(rng.Next(0, 3));
            Mem[] row = nKeys == 2 ? [first[i], second, Mem.FromInt(i)] : [first[i], Mem.FromInt(i)];
            rows.Add(row);
            sorter.Add(row);
        }

        int[] expected = [.. Enumerable.Range(0, n).OrderBy(i => i, Comparer<int>.Create((x, y) => Cmp(rows[x], rows[y], terms)))];
        var got = new List<int>();
        var view = new RecordView();
        if (sorter.SortInMemory())
        {
            do
            {
                RecordSorter.SortRow r = sorter.Current;
                view.Parse(r.Buf, r.Off, r.Len);
                got.Add((int)view.GetInt64(nKeys == 2 ? 2 : 1));
            }
            while (sorter.NextInMemory());
        }

        Assert.True(expected.SequenceEqual(got), $"{kind} n={n} desc={desc} nullsFirst={nullsFirst} keys={nKeys}: order differs");
    }
}
