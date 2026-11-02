using System;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

/// <summary>
/// A caller that repeats a longer sequence of calls after a suspension asks for a different page than the one whose read just completed.
/// The pager must finish the read into the cache and serve the page asked for, not hand out the other page's frame.
/// </summary>
public sealed class ReentryTests
{
    [Fact]
    public void AskingForAnotherPageAfterAColdReadCompletedServesThatPage()
    {
        MemoryVfs vfs = Kit.Prepare(8);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        p.BeginReadSync();

        Assert.Equal(PagerStatus.Pending, p.Get(5, GetOptions.None, out _));
        p.ExecuteSync();                       // the read of page 5 completes ...
        PagerStatus s = p.Get(3, GetOptions.None, out PageFrame? three);   // ... but the caller now asks for page 3
        if (s == PagerStatus.Pending)
        {
            p.ExecuteSync();
            s = p.Get(3, GetOptions.None, out three);
        }

        Assert.Equal(PagerStatus.Ok, s);
        Assert.Equal(3u, three!.PageNumber);
        PageFrame five = p.GetSync(5);
        Assert.Equal(5u, five.PageNumber);
        Assert.NotSame(three, five);
        Assert.Equal((byte)((5 * 31) & 0xFF), five.Data[0]);
        Assert.Equal((byte)((3 * 31) & 0xFF), three.Data[0]);
        p.Release(three);
        p.Release(five);
        p.EndReadSync();
    }
}
