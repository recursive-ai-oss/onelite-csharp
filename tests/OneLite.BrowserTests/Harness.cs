using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>A failed check inside a test.</summary>
internal sealed class CheckFailed(string message) : Exception(message);

/// <summary>The few assertions the browser tests use; xUnit does not run inside a page.</summary>
internal static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new CheckFailed(what);
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailed($"{what}: expected {expected}, got {actual}");
        }
    }

    public static void BytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what)
    {
        if (expected.Length != actual.Length)
        {
            throw new CheckFailed($"{what}: expected {expected.Length} bytes, got {actual.Length}");
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                throw new CheckFailed($"{what}: first difference at byte {i} (expected {expected[i]}, got {actual[i]})");
            }
        }
    }

    public static void BytesEqual(byte[] expected, byte[] actual, string what) => BytesEqual(expected.AsSpan(), actual.AsSpan(), what);

    public static async Task<T> Throws<T>(Func<Task> body, string what)
        where T : Exception
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (T e)
        {
            return e;
        }
        catch (Exception e)
        {
            throw new CheckFailed($"{what}: expected {typeof(T).Name}, got {e.GetType().Name}: {e.Message}");
        }

        throw new CheckFailed($"{what}: expected {typeof(T).Name}, nothing was thrown");
    }
}

/// <summary>One named test. <see cref="Context"/> says where it can run: "page" (main thread), "worker", or "any".</summary>
internal sealed record TestCase(string Name, string Context, Func<Task> Body);

internal static class Registry
{
    private static readonly List<TestCase> s_all = [];

    public static IReadOnlyList<TestCase> All => s_all;

    public static void Add(string name, string context, Func<Task> body) => s_all.Add(new TestCase(name, context, body));
}
