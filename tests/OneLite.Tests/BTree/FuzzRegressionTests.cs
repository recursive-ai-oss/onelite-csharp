using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Fuzz;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// Inputs the mutation fuzzer found that once escaped as something other than a typed corruption error (an index out of range in a
/// write on a damaged page, a damaged freelist, an image shorter than a page). Each must now run to the end of the target.
/// </summary>
public sealed class FuzzRegressionTests
{
    public static IEnumerable<object[]> Inputs()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "btree-fuzz");
        foreach (string f in Directory.GetFiles(dir).Order(StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetFileName(f) };
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void DamagedImageGivesOnlyTypedErrors(string name)
    {
        BTreeTarget.Run(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "btree-fuzz", name)));
    }

    public static IEnumerable<object[]> PagerInputs()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "pager-btree-fuzz");
        foreach (string f in Directory.GetFiles(dir).Order(StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetFileName(f) };
        }
    }

    /// <summary>Database image plus journal, run through the B-tree on the pager. The first input claims an overflow chain longer than the database; deleting that row once tried to reserve (and journal) a million pages.</summary>
    [Theory]
    [MemberData(nameof(PagerInputs))]
    public void DamagedFilesThroughThePagerGiveOnlyTypedErrors(string name)
    {
        PagerBTreeTarget.Run(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "pager-btree-fuzz", name)));
    }
}
