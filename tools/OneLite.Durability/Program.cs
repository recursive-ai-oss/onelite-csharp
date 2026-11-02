using System;
using System.IO;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Durability;
using RecursiveAI.OneLite.Oracle;

// onelite-durability: process-kill campaign against a pluggable writer (see docs/interop-and-durability.md).
//   kill [--mode wal|delete] [--sync full|normal] [--kills N] [--seed S] [--episode K] [--max-delay-ms MS]
//        [--writer "CMD {db} {ledger} {journal} {sync}"] [--json FILE] [--work DIR]
//        [--fault ledger-early]   (built-in writer with a deliberate bug, to prove the check fails)
//   writer DB LEDGER MODE SYNC        (internal) the built-in writer
//   engine-writer DB LEDGER MODE SYNC [ps=N] [av=0|1|2] [cache=N] [enc=E]   the same workload as SQL on OneLite's engine, with savepoints and statement rollbacks
//   engine-coexist [--commits N] [--journal delete|wal] [--sync full|normal] [--ps N]   OneLite SQL and sqlite3 processes write one file at once while readers check it
//   conc-kill [--kills N] [--writers W] [--seed S] [--max-delay-ms MS] [--sync full|normal] [--work DIR]   kill several BEGIN CONCURRENT writer processes at once, again and again; judged by the shell
//   conc-coexist [--commits N] [--writers W] [--sync full|normal]   BEGIN CONCURRENT writers and a sqlite3 process with plain BEGIN write one file at once
//   coexist [--commits N] [--journal M] [--ps N] [--av N]   OneLite and a sqlite3 process alternate commits on one file
//   cipher-kill [--kills N] [--seed S] [--episode K] [--max-delay-ms MS] [--json F] name=value...   kill an encrypted writer, judged by the SQLCipher
//        shell named in ONELITE_SQLCIPHER (settings: key= compat= ps= iter= kdf= hmac= mode= sync= cache= av=)
//   rekey-kill   (same options)   kill a process that rekeys the database again and again; the file must open under exactly one key
//   btree-writer DB LEDGER MODE SYNC [ps=N] [av=0|1|2] [cache=N] [enc=E]   the same workload on OneLite's own pager and B-tree
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: durability kill [options] | writer DB LEDGER MODE SYNC");
    return 2;
}
try
{
    switch (args[0])
    {
        case "kill": return Campaign.Run(args[1..]);
        case "writer": return Writer.Run(args[1..]);
        case "btree-writer": return BTreeWriter.Run(args[1..]);
        case "engine-writer": return EngineWriter.Run(args[1..]);
        case "conc-kill": return ConcKill.Run(args[1..]);
        case "conc-coexist": return ConcKill.Coexist(args[1..]);
        case "conc-writer": return ConcKill.Writer(args[1..]);
        case "coexist": return Coexist.Run(args[1..]);
        case "engine-coexist": return EngineCoexist.Run(args[1..]);
        case "cipher-writer": return CipherWriters.Run(args[1..]);
        case "cipher-build": return CipherWriters.Build(args[1..]);
        case "rekey-writer": return CipherWriters.Rekey(args[1..]);
        case "cipher-kill": return CipherCampaign.Run(args[1..], rekey: false);
        case "rekey-kill": return CipherCampaign.Run(args[1..], rekey: true);
        default:
            Console.Error.WriteLine("unknown command " + args[0]);
            return 2;
    }
}
catch (OneLiteException e)
{
    Console.Error.WriteLine("error: " + e.Message + " [" + e.ExtendedCode + ": " + e.Detail + "]");
    Console.Error.WriteLine(e.StackTrace);
    return 1;
}
catch (Exception e) when (e is SqliteException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}
