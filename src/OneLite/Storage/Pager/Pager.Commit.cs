using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Pager;

public sealed partial class Pager
{
    private int _cmStep;
    private int _cmPhase;
    private int _cmWrite;
    private int _cmFinish;
    private int _wIdx;
    private bool _cmNothing;
    private uint _cmNewCounter;
    private PageFrame? _cmFrame;
    private static readonly Comparison<PageFrame> ByPage = static (a, b) => a.PageNumber.CompareTo(b.PageNumber);

    /// <summary>True if the write transaction has anything to commit (a modified page or a database file already written to).</summary>
    public bool HasChanges => _dirty.Count != 0 || _dbModified || _jfile != null;

    /// <summary>True if the write transaction has changed a page (a PERSIST journal that is merely open from an earlier transaction does not count).</summary>
    public bool HasPageChanges => _dirty.Count != 0 || _dbModified;

    /// <summary>
    /// Commits the write transaction (atomiccommit §3). Order, as observed on the reference (docs/durability.md): stamp page 1; make the
    /// journal durable; take EXCLUSIVE; write the dirty pages ascending; truncate if the file shrank; sync the database; retire the journal
    /// (the commit point); drop to SHARED. Afterwards the pager is in a read transaction.
    /// </summary>
    /// <returns>The status. On <see cref="PagerStatus.Error"/> nothing is committed and <see cref="Rollback"/> restores the old state.</returns>
    public PagerStatus Commit()
    {
        Guard();
        if (_state != TxState.Write && _ccStep == 0)
        {
            return PagerStatus.Ok;
        }

        if (_wal != null)
        {
            return _concurrent || _ccStep != 0 ? CommitConcurrent() : CommitWal();
        }

        if (_cmPhase == 0)
        {
            PagerStatus s = CommitPrepare(null);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            _cmPhase = 1;
        }

        if (_cmPhase == 1)
        {
            PagerStatus s = CommitWrite();
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            _cmPhase = 2;
        }

        PagerStatus f = CommitFinish();
        if (f != PagerStatus.Ok)
        {
            return f;
        }

        _cmPhase = 0;
        return PagerStatus.Ok;
    }

    /// <summary>
    /// First half of a multi-file commit (atomiccommit §5.3): stamps page 1 and makes the journal durable, with the super-journal's name
    /// recorded in it. Followed by <see cref="WriteDatabase"/> and, once the super-journal is deleted, <see cref="FinishCommit"/>.
    /// </summary>
    /// <param name="superJournal">The full path of the super-journal, or null for an ordinary commit.</param>
    /// <returns>The status.</returns>
    public PagerStatus PrepareCommit(string? superJournal)
    {
        Guard();
        if (_wal != null)
        {
            // A WAL commit is one append of frames; the commit point is the wal-index update inside it (wal), so the other phases are empty.
            return _state == TxState.Write ? CommitWal() : PagerStatus.Ok;
        }

        return _state == TxState.Write ? CommitPrepare(superJournal) : PagerStatus.Ok;
    }

    /// <summary>Second step of a multi-file commit: takes EXCLUSIVE, writes and syncs the database.</summary>
    /// <returns>The status.</returns>
    public PagerStatus WriteDatabase()
    {
        Guard();
        return _state == TxState.Write && _wal == null ? CommitWrite() : PagerStatus.Ok;
    }

    /// <summary>Last step of a multi-file commit: retires the journal and drops to SHARED.</summary>
    /// <returns>The status.</returns>
    public PagerStatus FinishCommit()
    {
        Guard();
        return _state == TxState.Write && _wal == null ? CommitFinish() : PagerStatus.Ok;
    }

    /// <summary>The full path of this pager's journal (the name a super-journal lists).</summary>
    public string JournalFullPath => _vfs.FullPathname(_journalPath);

    /// <summary>The full path of the database file.</summary>
    public string FullPath => _vfs.FullPathname(_path);

    internal IVfs Vfs => _vfs;

    private string? _identity;

    /// <summary>
    /// Names this database file's identity for in-process hints that must not outlive the file: the VFS instance, the full path and the time the file
    /// was created. A file that is deleted and created again, or replaced by a copy, has a new creation time, so state kept for the old file is not
    /// used for the new one. Worked out once per pager: a file cannot be replaced under an open handle.
    /// </summary>
    internal string Identity
    {
        get
        {
            if (_identity is null)
            {
                string path = FullPath;
                long born = 0;
                try
                {
                    if (_vfs is not MemoryVfs && File.Exists(path))
                    {
                        born = File.GetCreationTimeUtc(path).Ticks;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                _identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_vfs).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + born.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + path;
            }

            return _identity;
        }
    }

    private PagerStatus CommitPrepare(string? superName)
    {
        for (; ; )
        {
            switch (_cmStep)
            {
                case 0:
                    _cmNothing = _dirty.Count == 0 && !_dbModified && _jfile == null;
                    if (_cmNothing)
                    {
                        return PagerStatus.Ok;
                    }

                    _cmStep = 10;
                    break;
                case 10:
                {
                    // Commit.AutoVacuumRelocate: in a full auto-vacuum database the free pages are moved to the end and cut off.
                    PagerStatus s = AutoVacuumCommit();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _cmStep = 1;
                    break;
                }
                case 1:
                {
                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _cmFrame = f;
                    _cmStep = 2;
                    break;
                }

                case 2:
                {
                    PagerStatus s = MakeWritable(_cmFrame!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    // fileformat2 §1.3: bump the change counter, record the size, and set version-valid-for to the new counter.
                    Span<byte> p1 = _cmFrame!.Data;
                    _cmNewCounter = unchecked(DatabaseHeader.GetUInt32(p1, DatabaseHeader.ChangeCounterOffset) + 1);
                    DatabaseHeader.SetUInt32(p1, DatabaseHeader.ChangeCounterOffset, _cmNewCounter);
                    DatabaseHeader.SetUInt32(p1, DatabaseHeader.DatabaseSizeOffset, _dbSize);
                    DatabaseHeader.SetUInt32(p1, DatabaseHeader.VersionValidForOffset, _cmNewCounter);
                    _cmFrame.Pins--;
                    _cmFrame = null;
                    _cmStep = 3;
                    break;
                }

                default:
                {
                    PagerStatus s = SyncJournal(superName);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _cmStep = 0;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    private PagerStatus CommitWrite()
    {
        if (_cmNothing)
        {
            // Observed on the reference: a write transaction that changed nothing still needs the EXCLUSIVE lock to commit, so it is BUSY
            // while another connection is reading (lockingv3: the lock is taken before the commit looks at what changed).
            return TakeLock(LockLevel.Exclusive);
        }

        for (; ; )
        {
            switch (_cmWrite)
            {
                case 0:
                {
                    PagerStatus s = TakeLock(LockLevel.Exclusive);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _dirty.Sort(ByPage);
                    while (_dirty.Count > 0 && _dirty[^1].PageNumber > _dbSize)
                    {
                        PageFrame gone = _dirty[^1];
                        _dirty.RemoveAt(_dirty.Count - 1);
                        gone.Dirty = false;
                    }

                    _wIdx = 0;
                    _cmWrite = 1;
                    break;
                }

                case 1:
                {
                    if (_wIdx >= _dirty.Count)
                    {
                        _cmWrite = 2;
                        break;
                    }

                    // Consecutive page numbers go out as one gather write.
                    int end = _wIdx + 1;
                    while (end < _dirty.Count && end - _wIdx < 64 && _dirty[end].PageNumber == _dirty[end - 1].PageNumber + 1)
                    {
                        end++;
                    }

                    int n = end - _wIdx;
                    if (_runBuffer.Length < n)
                    {
                        _runBuffer = new ReadOnlyMemory<byte>[64];
                    }

                    for (int i = 0; i < n; i++)
                    {
                        if (_cipher is null)
                        {
                            _runBuffer[i] = _dirty[_wIdx + i].Data;
                        }
                        else
                        {
                            byte[] enc = RunBuffer(i);
                            if (!Resuming(ResumeState.CommitWriteDb))
                            {
                                EncodeForWrite(_dirty[_wIdx + i].PageNumber, _dirty[_wIdx + i].Data, enc);
                            }

                            _runBuffer[i] = enc;
                        }
                    }

                    _dbModified = true;
                    if (!Io(ResumeState.CommitWriteDb, new IoRequest { Kind = IoKind.WriteRun, File = _dbFile, Offset = (long)(_dirty[_wIdx].PageNumber - 1) * _pageSize, Run = _runBuffer, RunCount = n }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "writing database pages failed");
                    }

                    _fileBytes = Math.Max(_fileBytes, (long)_dirty[end - 1].PageNumber * _pageSize);
                    _wIdx = end;
                    break;
                }

                case 2:
                    if (_fileBytes > (long)_dbSize * _pageSize)
                    {
                        if (!Io(ResumeState.CommitTruncateDb, new IoRequest { Kind = IoKind.Truncate, File = _dbFile, Offset = (long)_dbSize * _pageSize }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "truncating the database failed");
                        }

                        _fileBytes = (long)_dbSize * _pageSize;
                    }

                    _cmWrite = 3;
                    break;
                default:
                    if (_sync != SynchronousMode.Off)
                    {
                        if (!Io(ResumeState.CommitSyncDb, new IoRequest { Kind = IoKind.Sync, File = _dbFile, Arg = (int)SyncKind() }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "syncing the database failed");
                        }
                    }

                    _cmWrite = 0;
                    return PagerStatus.Ok;
            }
        }
    }

    private PagerStatus CommitFinish()
    {
        for (; ; )
        {
            switch (_cmFinish)
            {
                case 0:
                    if (!_cmNothing && (_jfile != null || _fjStep != 0))
                    {
                        // The commit point: the journal stops being valid (atomiccommit §3.11).
                        PagerStatus s = FinalizeJournal(ref _jfile, _jTrailerDone);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }
                    }

                    if (!_cmNothing)
                    {
                        foreach (PageFrame f in _dirty)
                        {
                            f.Dirty = false;
                        }

                        _changeCounter = _cmNewCounter;
                        _hasHeader = true;
                        _origSize = _dbSize;
                        EndRekey(committed: true);
                    }

                    ResetWriteState();
                    _cmNothing = false;
                    _cmFinish = 1;
                    break;
                default:
                    if (_lock > LockLevel.Shared)
                    {
                        if (!Io(ResumeState.CommitUnlock, new IoRequest { Kind = IoKind.Unlock, File = _dbFile, Arg = (int)LockLevel.Shared }))
                        {
                            return PagerStatus.Pending;
                        }

                        _lock = LockLevel.Shared;
                    }

                    _cmFinish = 0;
                    _state = TxState.Read;
                    return PagerStatus.Ok;
            }
        }
    }

    internal static byte[] SuperJournalContent(IEnumerable<string> journalPaths)
    {
        var ms = new MemoryStream();
        foreach (string p in journalPaths)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(p);
            ms.Write(b, 0, b.Length);
            ms.WriteByte(0);
        }

        return ms.ToArray();
    }

    internal static string NewSuperJournalName(string mainFullPath)
    {
        uint r = unchecked((uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
        return mainFullPath + "-mj" + r.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
}
