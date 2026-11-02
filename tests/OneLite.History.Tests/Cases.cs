using System.Collections.Generic;
using System.Linq;

namespace RecursiveAI.OneLite.History.Tests;

/// <summary>A synthetic history with the verdicts it must get.</summary>
/// <param name="Name">Case name.</param>
/// <param name="Build">Builds the history.</param>
/// <param name="Valid">Expected validity at read-committed, snapshot-isolation, serializable and strict-serializable.</param>
/// <param name="Expect">Anomaly classes that must be reported (empty for a clean history).</param>
internal sealed record Case(string Name, Func<History> Build, bool[] Valid, AnomalyClass[] Expect, bool RealtimeVersions = false);

/// <summary>Hand-written histories, one per anomaly class and level boundary (Adya; Berenson et al.; Fekete et al.; Kingsbury and Alvaro).</summary>
internal static class Cases
{
    private const long Far = 1_000_000;

    // A transaction that overlaps every other concurrent-style transaction (no real-time order between them).
    internal static Txn C(int id, params Mop[] m) => new(id, id, id, Far + id, TxnOutcome.Ok, m);

    internal static Txn Failed(int id, params Mop[] m) => new(id, id, id, Far + id, TxnOutcome.Fail, m);

    internal static Txn Unknown(int id, params Mop[] m) => new(id, id, id, Txn.NeverCompleted, TxnOutcome.Info, m);

    internal static Txn At(int id, long t0, long t1, params Mop[] m) => new(id, id, t0, t1, TxnOutcome.Ok, m);

    internal static Mop R(int k, params int[] l) => Mop.ReadList(k, l);

    internal static Mop A(int k, int v) => Mop.AppendList(k, v);

    internal static Mop Rr(int k, int v) => Mop.ReadRegister(k, v);

    internal static Mop W(int k, int v) => Mop.WriteRegister(k, v);

    private static History L(params Txn[] t) => new(HistoryModel.ListAppend, t.ToList());

    private static History G(params Txn[] t) => new(HistoryModel.Register, t.ToList());

    private static readonly bool[] None = [true, true, true, true];
    private static readonly bool[] Nothing = [false, false, false, false];
    private static readonly bool[] FromSi = [true, false, false, false];
    private static readonly bool[] FromSer = [true, true, false, false];
    private static readonly bool[] OnlyStrict = [true, true, true, false];

    private const int X = 1;
    private const int Y = 2;
    private const int Z = 3;
    private const int V = 4;

    public static readonly Case[] All =
    [
        // ---- list-append ----
        new("list clean", () => L(C(1, A(X, 1)), C(2, R(X, 1), A(X, 2)), C(3, R(X, 1, 2))), None, []),
        new("list G0 write cycle", () => L(C(1, A(X, 1), A(Y, 1)), C(2, A(X, 2), A(Y, 2)), C(3, R(X, 1, 2), R(Y, 2, 1))), Nothing, [AnomalyClass.G0]),
        new("list G1a aborted read", () => L(Failed(1, A(X, 1)), C(2, R(X, 1))), Nothing, [AnomalyClass.G1a]),
        new("list G1b intermediate read", () => L(C(1, A(X, 1), A(X, 2)), C(2, R(X, 1)), C(3, R(X, 1, 2))), Nothing, [AnomalyClass.G1b]),
        new("list G1c circular information flow", () => L(C(1, A(X, 1), R(Y, 1)), C(2, A(Y, 1), R(X, 1))), Nothing, [AnomalyClass.G1c]),
        new("list G-single read skew", () => L(C(1, A(X, 1), A(Y, 1)), C(2, R(X), R(Y, 1)), C(3, R(X, 1), R(Y, 1))), FromSi, [AnomalyClass.GSingle]),
        new("list lost update", () => L(C(0, A(X, 1)), C(1, R(X, 1), A(X, 2)), C(2, R(X, 1), A(X, 3)), C(3, R(X, 1, 2, 3))), FromSi, [AnomalyClass.LostUpdate, AnomalyClass.GSingle]),
        new("list lost update, unobserved order", () => L(C(0, A(X, 1)), C(1, R(X, 1), A(X, 2)), C(2, R(X, 1), A(X, 3))), FromSi, [AnomalyClass.LostUpdate]),
        new("list write skew G2-item", () => L(C(1, R(X), A(Y, 1)), C(2, R(Y), A(X, 1)), C(3, R(X, 1), R(Y, 1))), FromSer, [AnomalyClass.G2Item]),
        new("list G-nonadjacent", () => L(C(1, R(Y), R(V, 1)), C(2, A(Y, 1), A(Z, 1)), C(3, R(Z, 1), R(X)), C(4, A(X, 1), A(V, 1)), C(5, R(Y, 1), R(Z, 1), R(X, 1), R(V, 1))), FromSi, [AnomalyClass.GNonadjacent]),
        new("list real-time violation", () => L(At(1, 0, 10, A(X, 1)), At(2, 20, 30, R(X)), At(3, 40, 50, R(X, 1))), OnlyStrict, [AnomalyClass.GSingleRealtime]),
        new("list internal inconsistency", () => L(C(1, R(X), A(X, 1), R(X)), C(2, R(X, 1))), Nothing, [AnomalyClass.Internal]),
        new("list incompatible order", () => L(C(1, R(X, 1, 2)), C(2, R(X, 2, 1)), C(3, A(X, 1)), C(4, A(X, 2))), Nothing, [AnomalyClass.IncompatibleOrder]),
        new("list garbage read", () => L(C(1, R(X, 9))), Nothing, [AnomalyClass.GarbageRead]),
        new("list duplicate elements", () => L(C(1, A(X, 1)), C(2, R(X, 1, 1))), Nothing, [AnomalyClass.DuplicateElements]),
        new("list non-repeatable read", () => L(C(1, R(X), R(X, 1)), C(2, A(X, 1))), FromSi, [AnomalyClass.NonRepeatableRead]),
        new("list unknown outcome, observed", () => L(Unknown(1, A(X, 1)), C(2, R(X, 1))), None, []),
        new("list unknown outcome, unobserved", () => L(Unknown(1, A(X, 1)), C(2, R(X))), None, []),
        new("list own appends then read", () => L(C(1, A(X, 1), R(X, 1), A(X, 2), R(X, 1, 2)), C(2, R(X, 1, 2))), None, []),
        new("list failed transaction unobserved", () => L(Failed(1, A(X, 1)), C(2, R(X))), None, []),

        // ---- register ----
        new("register clean", () => G(C(1, W(X, 5)), C(2, Rr(X, 5), W(X, 6)), C(3, Rr(X, 6))), None, []),
        new("register G1a aborted read", () => G(Failed(1, W(X, 5)), C(2, Rr(X, 5))), Nothing, [AnomalyClass.G1a]),
        new("register G1b intermediate read", () => G(C(1, W(X, 5), W(X, 6)), C(2, Rr(X, 5))), Nothing, [AnomalyClass.G1b]),
        new("register garbage read", () => G(C(1, Rr(X, 77))), Nothing, [AnomalyClass.GarbageRead]),
        new("register G0 write cycle", () => G(C(1, W(X, 1), Rr(Y, 2), W(Y, 3)), C(2, W(Y, 2), Rr(X, 1), W(X, 4))), Nothing, [AnomalyClass.G0]),
        new("register G1c circular information flow", () => G(C(1, W(X, 1), Rr(Y, 2)), C(2, W(Y, 2), Rr(X, 1))), Nothing, [AnomalyClass.G1c]),
        new("register G-single read skew", () => G(C(1, W(X, 1), W(Y, 1)), C(2, Rr(X, 0), Rr(Y, 1))), FromSi, [AnomalyClass.GSingle]),
        new("register lost update", () => G(C(0, W(X, 1)), C(1, Rr(X, 1), W(X, 2)), C(2, Rr(X, 1), W(X, 3))), FromSi, [AnomalyClass.LostUpdate]),
        new("register write skew G2-item", () => G(C(1, Rr(X, 0), W(Y, 1)), C(2, Rr(Y, 0), W(X, 1)), C(3, Rr(X, 1), Rr(Y, 1))), FromSer, [AnomalyClass.G2Item]),
        new("register real-time violation", () => G(At(1, 0, 10, W(X, 1)), At(2, 20, 30, Rr(X, 0))), OnlyStrict, [AnomalyClass.GSingleRealtime]),
        new("register stale read ordered by real-time versions", () => G(At(1, 0, 10, W(X, 1)), At(2, 20, 30, W(X, 2)), At(3, 40, 50, Rr(X, 1))), OnlyStrict, [AnomalyClass.GSingleRealtime], RealtimeVersions: true),
        new("register internal inconsistency", () => G(C(1, W(X, 1), Rr(X, 2)), C(2, W(X, 2))), Nothing, [AnomalyClass.Internal]),
        new("register non-repeatable read", () => G(C(1, Rr(X, 0), Rr(X, 1)), C(2, W(X, 1))), FromSi, [AnomalyClass.NonRepeatableRead]),
        new("register unknown outcome, observed", () => G(Unknown(1, W(X, 1)), C(2, Rr(X, 1))), None, []),
    ];

    public static IEnumerable<string> Names => All.Select(c => c.Name);
}
