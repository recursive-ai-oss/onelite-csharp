using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// OneLite and a live <c>sqlite3</c> process on one file in rollback-journal mode: whoever cannot get the lock it needs at commit gets
/// SQLITE_BUSY and nothing is stored (lockingv3). Both directions, with the shell as reader and as writer.
/// </summary>
public sealed class CrossProcessContentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-xproc-" + Guid.NewGuid().ToString("N"));

    public CrossProcessContentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>A running <c>sqlite3</c> shell fed one script at a time; each call returns what it printed (standard output, then standard error).</summary>
    private sealed class Shell : IDisposable
    {
        private readonly Process _p;
        private readonly StringBuilder _err = new();

        public Shell(string db)
        {
            var psi = new ProcessStartInfo(OraclePaths.Cli, db)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            _p = Process.Start(psi)!;
            _p.ErrorDataReceived += (_, e) =>
            {
                lock (_err)
                {
                    _err.AppendLine(e.Data);
                }
            };
            _p.BeginErrorReadLine();
            _ = Run(".bail off");
        }

        public string Run(string script, bool expectError = false)
        {
            _p.StandardInput.WriteLine(script);
            _p.StandardInput.WriteLine(".print @@done");
            _p.StandardInput.Flush();
            var sb = new StringBuilder();
            for (string? line; (line = _p.StandardOutput.ReadLine()) != null && line != "@@done";)
            {
                sb.AppendLine(line);
            }

            // Standard error arrives on its own thread: wait for the message a refused statement prints.
            for (int i = 0; i < 100 && expectError; i++)
            {
                lock (_err)
                {
                    if (_err.Length > 0)
                    {
                        break;
                    }
                }

                System.Threading.Thread.Sleep(30);
            }

            System.Threading.Thread.Sleep(50);
            lock (_err)
            {
                sb.Append(_err);
                _err.Clear();
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            _p.StandardInput.Close();
            if (!_p.WaitForExit(5000))
            {
                _p.Kill();
            }

            _p.Dispose();
        }
    }

    private string MakeDb(string mode)
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "uses POSIX record locks shared with the shell");
        string path = Path.Combine(_dir, mode + ".db");
        using var db = Database.Open(path);
        using Connection c = db.Connect();
        foreach (string sql in new[] { "PRAGMA journal_mode=" + mode, "CREATE TABLE t(x INTEGER)", "INSERT INTO t VALUES (1),(2),(3)" })
        {
            using Statement s = c.Prepare(sql);
            while (s.Step())
            {
            }
        }

        return path;
    }

    private static long Count(Connection c)
    {
        using Statement s = c.Prepare("SELECT count(*) FROM t");
        s.Step();
        return s.GetInt64(0);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("truncate")]
    [InlineData("persist")]
    public void OneLiteWriteIsBusyWhileTheShellReads(string mode)
    {
        string path = MakeDb(mode);
        using var shell = new Shell(path);
        Assert.Contains("3", shell.Run("BEGIN; SELECT count(*) FROM t;"), StringComparison.Ordinal);

        using var db = Database.Open(path);
        using Connection c = db.Connect();
        foreach (string sql in new[] { "INSERT INTO t VALUES (9)", "UPDATE t SET x = x + 10 WHERE x = 1", "DELETE FROM t WHERE x = 2" })
        {
            using Statement s = c.Prepare(sql);
            var e = Assert.Throws<OneLiteException>(() => s.Step());
            Assert.Equal(ResultCode.Busy, e.Code);
            Assert.Equal("database is locked", e.Message);
        }

        Assert.Contains("3", shell.Run("COMMIT; SELECT count(*) FROM t;"), StringComparison.Ordinal);
        Assert.Equal(3, Count(c));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("persist")]
    public void ShellWriteIsBusyWhileOneLiteReads(string mode)
    {
        string path = MakeDb(mode);
        using var db = Database.Open(path);
        using Connection c = db.Connect();
        using Statement reading = c.Prepare("SELECT x FROM t");
        Assert.True(reading.Step());

        using var shell = new Shell(path);
        string outcome = shell.Run("INSERT INTO t VALUES (9);", true);
        Assert.Contains("database is locked", outcome, StringComparison.Ordinal);
        reading.Reset();
        Assert.Equal(3, Count(c));
        Assert.DoesNotContain("locked", shell.Run("INSERT INTO t VALUES (9);"), StringComparison.Ordinal);
        Assert.Equal(4, Count(c));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("persist")]
    public void WritersExcludeEachOtherAcrossProcesses(string mode)
    {
        string path = MakeDb(mode);
        using var shell = new Shell(path);
        Assert.DoesNotContain("locked", shell.Run("BEGIN IMMEDIATE; INSERT INTO t VALUES (20);"), StringComparison.Ordinal);

        using var db = Database.Open(path);
        using Connection c = db.Connect();
        using (Statement s = c.Prepare("INSERT INTO t VALUES (9)"))
        {
            Assert.Equal(ResultCode.Busy, Assert.Throws<OneLiteException>(() => s.Step()).Code);
        }

        Assert.DoesNotContain("locked", shell.Run("COMMIT;"), StringComparison.Ordinal);
        using (Statement s = c.Prepare("INSERT INTO t VALUES (9)"))
        {
            s.Step();
        }

        Assert.Equal(5, Count(c));

        using (Statement s = c.Prepare("BEGIN IMMEDIATE"))
        {
            s.Step();
        }

        Assert.Contains("database is locked", shell.Run("INSERT INTO t VALUES (21);", true), StringComparison.Ordinal);
        using (Statement s = c.Prepare("COMMIT"))
        {
            s.Step();
        }

        Assert.Equal(5, Count(c));
    }
}
