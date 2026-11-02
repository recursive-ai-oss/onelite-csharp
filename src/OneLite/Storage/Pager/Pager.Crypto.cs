using System.Security.Cryptography;
using RecursiveAI.OneLite.Storage.Crypto;

namespace RecursiveAI.OneLite.Storage.Pager;

// Encrypted databases (docs/encryption.md). The page cache holds plaintext. A page is encoded when it is written to the database file, to a
// rollback journal record or to a WAL frame, and decoded when it is read from the database file or the WAL. The journal and WAL checksums run
// over the stored (encoded) bytes, and playback and checkpoint copy stored bytes without touching the codec: both observed on files the
// reference shell writes (tools/scripts/cipher_probe.py).
public sealed partial class Pager
{
    private CipherSettings? _cipher;
    private PageCodec? _codec;
    private CipherKey? _key;
    private Func<CipherKey>? _keyProvider;
    private byte[]? _cbuf;
    private byte[]? _ebuf;
    private byte[][] _runBufs = [];
    private byte[]? _plain1;

    // Rekey state (see Rekey): the codec the database file and WAL are written with while the old one still decodes unchanged pages and journals them.
    private PageCodec? _newCodec;
    private CipherSettings? _newSettings;
    private CipherKey? _newKey;
    private ulong[]? _rekeyed;
    private int _rkStep;
    private uint _rkPage;
    private PageFrame? _rkFrame;

    private void InitCipher(PagerOptions o)
    {
        if (o.Key == null && o.KeyProvider == null)
        {
            return;
        }

        _cipher = o.Cipher ?? CipherSettings.V4;
        _pageSize = _cipher.PageSize;

        // SQLCipher writes 80 reserved bytes whatever the digest (observed), which is also enough for SHA-1's 48.
        _reserved = Math.Max(80, _cipher.ReserveSize);
        _key = o.Key?.Clone();
        _keyProvider = o.KeyProvider;
        _cbuf = new byte[_pageSize];
        _ebuf = new byte[_pageSize];
    }

    /// <summary>
    /// Starts encryption on a pager that was opened without a key, as <c>PRAGMA key</c> does. It works only before anything has been read, so
    /// the file is still taken as unread: afterwards the key, page size and reserved bytes are those of <paramref name="settings"/>.
    /// </summary>
    /// <param name="key">The key. The pager keeps a copy.</param>
    /// <param name="settings">The cipher parameters the file is read (or created) with.</param>
    /// <returns>False if the pager already has a key or has read the file; nothing changes then.</returns>
    /// <exception cref="OneLiteException">SQLITE_MISUSE if the settings leave fewer than 480 usable bytes per page; SQLITE_ERROR where encryption is unsupported (the browser).</exception>
    public bool TryApplyKey(CipherKey key, CipherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(settings);
        Guard();
        if (_cipher != null || _keyProvider != null || _dbSize != 0 || _state != TxState.None || _wal != null)
        {
            return false;
        }

        if (!PageCodec.IsSupported)
        {
            throw new OneLiteException(ResultCode.Error, Msg(ResultCode.Error), "encryption is not supported in the browser build");
        }

        InstallCipherSettings(settings);
        _key = key.Clone();
        return true;
    }

    /// <summary>
    /// Changes the cipher parameters (page size, iterations, digests, plaintext header) of an encrypted pager that has not read the file yet,
    /// as <c>PRAGMA cipher_page_size</c>, <c>kdf_iter</c> and the like do after <c>PRAGMA key</c>.
    /// </summary>
    /// <param name="settings">The new parameters.</param>
    /// <returns>False if the pager is not encrypted or has read the file.</returns>
    public bool TryChangeCipherSettings(CipherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Guard();
        if (_cipher == null || _codec != null || _dbSize != 0 || _state != TxState.None)
        {
            return false;
        }

        InstallCipherSettings(settings);
        return true;
    }

    private void InstallCipherSettings(CipherSettings settings)
    {
        int reserved = Math.Max(80, settings.ReserveSize);
        if (settings.PageSize - reserved < 480)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "an encrypted database needs pages of at least 1024 bytes: 80 are reserved and SQLite needs 480 usable");
        }

        _cipher = settings;
        if (_pageSize != settings.PageSize)
        {
            _pageSize = settings.PageSize;
            DropAllFrames();
        }

        _reserved = reserved;
        _cbuf = new byte[_pageSize];
        _ebuf = new byte[_pageSize];
    }

    private static void ValidateCipherOptions(PagerOptions o)
    {
        if (o.Key == null && o.KeyProvider == null)
        {
            return;
        }

        if (!PageCodec.IsSupported)
        {
            throw new OneLiteException(ResultCode.Error, Msg(ResultCode.Error), "encryption is not supported in the browser build");
        }

        if (o.Key != null && o.KeyProvider != null)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "set a Key or a KeyProvider, not both");
        }

        CipherSettings c = o.Cipher ?? CipherSettings.V4;
        if (c.PageSize - Math.Max(80, c.ReserveSize) < 480)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "an encrypted database needs pages of at least 1024 bytes: 80 are reserved and SQLite needs 480 usable");
        }

        if (o.CipherSalt is { Length: not 0 and not CipherSettings.SaltSize })
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "CipherSalt is 16 bytes");
        }
    }

    /// <summary>True if this database is encrypted (the pager was opened with a key).</summary>
    public bool IsEncrypted => _cipher != null;

    /// <summary>The cipher parameters in force, or null for a plain database.</summary>
    public CipherSettings? CipherSettings => _cipher;

    /// <summary>A copy of the key the database is opened with (an attached database without its own KEY clause uses it), or null for a plain file.</summary>
    /// <returns>The copy; the caller disposes it.</returns>
    public CipherKey? CloneKey() => _key?.Clone();

    /// <summary>
    /// The 16-byte salt of the encrypted database, available once the first transaction has begun; empty for a plain database. A database with a
    /// plaintext header does not carry it in the file, so keep it (and pass it back as <see cref="PagerOptions.CipherSalt"/>).
    /// </summary>
    public ReadOnlySpan<byte> CipherSalt => _codec != null ? _codec.Salt : default;

    // The codec that decodes page pgno as it is stored right now.
    private PageCodec CodecForRead(uint pgno) => _newCodec != null && IsRekeyed(pgno) ? _newCodec : _codec!;

    private bool IsRekeyed(uint pgno) => _rekeyed != null && pgno / 64 < _rekeyed.Length && (_rekeyed[pgno / 64] & (1UL << (int)(pgno % 64))) != 0;

    // Page pgno is written in the database file or the WAL with the new key from here on (rekey only).
    private void MarkRekeyed(uint pgno)
    {
        int need = (int)(pgno / 64) + 1;
        if (_rekeyed == null || _rekeyed.Length < need)
        {
            Array.Resize(ref _rekeyed, Math.Max(need, (_rekeyed?.Length ?? 0) * 2));
        }

        _rekeyed[pgno / 64] |= 1UL << (int)(pgno % 64);
    }

    // Writing the database file or the WAL: the new key while a rekey is under way, else the only key.
    private void EncodeForWrite(uint pgno, byte[] plain, byte[] dest)
    {
        if (_newCodec != null)
        {
            MarkRekeyed(pgno);
            _newCodec.Encode(pgno, plain, dest);
            return;
        }

        _codec!.Encode(pgno, plain, dest);
    }

    // Journal records always carry the key the page is stored under now, so playback (which copies stored bytes) restores the old file whole.
    private void EncodeForJournal(uint pgno, ReadOnlySpan<byte> plain, Span<byte> dest) => _codec!.Encode(pgno, plain, dest);

    // Decodes the stored page in _cbuf into dest. Returns Error (with the codec's SQLITE_CORRUPT or SQLITE_NOTADB) if the MAC does not match.
    private PagerStatus DecodeStored(uint pgno, byte[] dest)
    {
        try
        {
            CodecForRead(pgno).Decode(pgno, _cbuf!, dest);
        }
        catch (OneLiteException e)
        {
            return FailWith(e);
        }

        return PagerStatus.Ok;
    }

    // True when the next Io(phase, ...) will consume a completion: the data of that request was already encoded and written.
    private bool Resuming(ResumeState phase) => _hasComp && _compPhase == phase;

    private byte[] RunBuffer(int i)
    {
        if (_runBufs.Length <= i)
        {
            Array.Resize(ref _runBufs, Math.Max(64, i + 1));
        }

        return _runBufs[i] ??= new byte[_pageSize];
    }

    // The codec exists as soon as there is something to say about it: for an existing file, once page 1 has been read (its first 16 bytes are the
    // salt); for a new file, at its first transaction. Derivation (PBKDF2) is the expensive part, so a codec is kept while the salt stays.
    private PagerStatus OpenCodecForFile(ReadOnlySpan<byte> page1Stored)
    {
        try
        {
            _key ??= _keyProvider!();
            CipherSettings s = _cipher!;
            if (_codec != null && s.PlaintextHeaderSize == 0 && !_codec.Salt.SequenceEqual(page1Stored[..CipherSettings.SaltSize]))
            {
                // Another process rekeyed the file with a new salt.
                _codec.Dispose();
                _codec = null;
            }

            _codec ??= PageCodec.OpenExisting(s, _key, page1Stored, _opts.CipherSalt);
        }
        catch (OneLiteException e)
        {
            return FailWith(e);
        }

        return PagerStatus.Ok;
    }

    private void OpenCodecForNewFile()
    {
        if (_codec != null)
        {
            return;
        }

        _key ??= _keyProvider!();
        _codec = PageCodec.CreateNew(_cipher!, _key, _opts.CipherSalt);
    }

    // ---- online rekey --------------------------------------------------------------------------------------------------------------
    //
    // A rekey is one ordinary write transaction in which every page of the database is rewritten under the new key. The pages in the rollback
    // journal (or the older WAL frames) stay under the old key, because the journal is written from the old codec, and playback and checkpoint
    // copy stored bytes. So the usual commit rule gives the guarantee: whichever way a crash falls, the file is entirely old-key (journal played
    // back, or WAL commit frame never published) or entirely new-key, with every committed transaction in it.

    /// <summary>
    /// Re-encrypts the whole database under a new key inside the open write transaction: every page is made writable (so its old form is
    /// journaled) and dirtied, and the new key takes over when the transaction commits. Roll back and the old key stays. Re-entrant like every
    /// pager call. The cache must be allowed to spill: a large database is rewritten through the usual spill path.
    /// </summary>
    /// <param name="newKey">The new key. The pager keeps a copy.</param>
    /// <param name="newSettings">New cipher parameters (digests, iterations), or null to keep the current ones. The page size and plaintext header size cannot change.</param>
    /// <returns>The status.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_MISUSE</c> outside a write transaction, on a plain database, with <c>journal_mode</c> OFF or MEMORY (a crash could leave a mixture of keys), or when the page size would change.</exception>
    public PagerStatus Rekey(CipherKey newKey, CipherSettings? newSettings = null)
    {
        ArgumentNullException.ThrowIfNull(newKey);
        Guard();
        if (_state != TxState.Write || _cipher == null || _codec == null)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "Rekey needs an encrypted database and an open write transaction");
        }

        if (_rkStep == 0)
        {
            newSettings ??= _cipher;
            if (newSettings.PageSize != _pageSize || newSettings.PlaintextHeaderSize != _cipher.PlaintextHeaderSize)
            {
                throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "a rekey cannot change the page size or the plaintext header size");
            }

            if (_wal == null && _journalMode is JournalMode.Off or JournalMode.Memory)
            {
                throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "a crash-safe rekey needs a rollback journal on disk or WAL mode");
            }

            if (_newCodec != null)
            {
                throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "a rekey is already under way in this transaction");
            }

            // Keep the salt unless the new key brings one: the file stays recognisable and a plaintext header keeps matching its stored salt.
            PageCodec nc = PageCodec.CreateNew(newSettings, newKey, _codec.Salt);
            if (_origSize == 0)
            {
                // Nothing of the database is on disk yet, so there is nothing to protect: the new key simply is the key.
                _codec.Dispose();
                _codec = nc;
                _cipher = newSettings;
                _key?.Dispose();
                _key = newKey.Clone();
                return PagerStatus.Ok;
            }

            _newCodec = nc;
            _newSettings = newSettings;
            _newKey = newKey.Clone();
            _rkPage = 1;
            _rkStep = 1;
        }

        while (_rkPage <= _origSize)
        {
            if (_rkPage == LockBytePage)
            {
                _rkPage++;
                continue;
            }

            if (_rkFrame == null)
            {
                PagerStatus g = GetCore(_rkPage, GetOptions.None, out PageFrame? f);
                if (g != PagerStatus.Ok)
                {
                    return g;
                }

                _rkFrame = f;
            }

            PagerStatus s = MakeWritable(_rkFrame!);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            _rkFrame!.Pins--;
            _rkFrame = null;
            _rkPage++;
        }

        _rkStep = 0;
        return PagerStatus.Ok;
    }

    // A rekey ends with its transaction. Committed: the new codec is the codec. Rolled back: the old one never stopped being right.
    private void EndRekey(bool committed)
    {
        if (_newCodec == null)
        {
            return;
        }

        if (_rkFrame != null)
        {
            _rkFrame.Pins = 0;
            _rkFrame = null;
        }

        if (committed)
        {
            _codec!.Dispose();
            _codec = _newCodec;
            _cipher = _newSettings;
            _key?.Dispose();
            _key = _newKey;
        }
        else
        {
            _newCodec.Dispose();
            _newKey?.Dispose();
        }

        _newCodec = null;
        _newSettings = null;
        _newKey = null;
        _rekeyed = null;
        _rkStep = 0;
    }

    /// <summary>Runs <see cref="Rekey"/> as its own transaction, inline: begins a write transaction, rewrites every page and commits. Throws the pager's error.</summary>
    /// <param name="newKey">The new key.</param>
    /// <param name="newSettings">New cipher parameters, or null to keep the current ones.</param>
    public void RekeySync(CipherKey newKey, CipherSettings? newSettings = null)
    {
        bool began = !InTransaction;
        if (began)
        {
            BeginReadSync();
        }

        try
        {
            BeginWriteSync();
            RunSync(() => Rekey(newKey, newSettings));
            CommitSync();
        }
        catch
        {
            if (InWriteTransaction)
            {
                RollbackSync();
            }

            throw;
        }
        finally
        {
            if (began && InTransaction && !InWriteTransaction)
            {
                EndReadSync();
            }
        }
    }

    /// <summary>Awaiting twin of <see cref="RekeySync"/>.</summary>
    /// <param name="newKey">The new key.</param>
    /// <param name="newSettings">New cipher parameters, or null to keep the current ones.</param>
    /// <param name="ct">Cancellation, observed at requests that are safe to abandon.</param>
    /// <returns>A task that finishes when the new key is committed.</returns>
    public async ValueTask RekeyAsync(CipherKey newKey, CipherSettings? newSettings = null, CancellationToken ct = default)
    {
        bool began = !InTransaction;
        if (began)
        {
            await BeginReadAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await BeginWriteAsync(ct).ConfigureAwait(false);
            await RunAsync(() => Rekey(newKey, newSettings), ct).ConfigureAwait(false);
            await CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (InWriteTransaction)
            {
                await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (began && InTransaction && !InWriteTransaction)
            {
                await EndReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void DisposeCipher()
    {
        _codec?.Dispose();
        _newCodec?.Dispose();
        _key?.Dispose();
        _newKey?.Dispose();
        _codec = null;
        _newCodec = null;
        _key = null;
        _newKey = null;
        foreach (byte[]? b in _runBufs)
        {
            if (b != null)
            {
                CryptographicOperations.ZeroMemory(b);
            }
        }

        if (_cbuf != null)
        {
            CryptographicOperations.ZeroMemory(_cbuf);
        }

        // The cache and the page-1 scratch hold decrypted pages; wipe them with the keys that produced them.
        if (_plain1 != null)
        {
            CryptographicOperations.ZeroMemory(_plain1);
        }

        foreach (PageFrame f in _frames)
        {
            CryptographicOperations.ZeroMemory(f.Data);
        }

        if (_ebuf != null)
        {
            CryptographicOperations.ZeroMemory(_ebuf);
        }
    }
}
