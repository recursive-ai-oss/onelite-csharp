# Encryption review

The project's own review (not an external audit) of OneLite's SQLCipher-compatible encryption: `Storage/Crypto/*`, `Storage/Pager/Pager.Crypto.cs` and the places where the pager encodes
and decodes pages (cache reads, spills, commits, journal records, WAL frames, playback, checkpoint). The reviewer had not written any of it.
Format description: [encryption.md](encryption.md).

Sources used: Zetetic's published "SQLCipher Design" and "SQLCipher API" pages, FIPS 197, NIST SP 800-38A, RFC 2104 and RFC 8018, and black-box runs of the
SQLCipher community shell (4.5.6 on the Linux worker; 4.20.0 behaviour is the one recorded in encryption.md). No SQLCipher source was read.

## Summary

| Question | Verdict |
|---|---|
| Format conformance | Conforms. Known-answer pages built by OpenSSL and Python's standard library decode; 422 of 422 interop cases against the SQLCipher shell pass. |
| IV freshness | Holds on every write path. Tested for the database file, journal records, WAL frames and rekey. |
| MAC before decrypt | Holds. Every decode checks the MAC first, in constant time, and hands back no plaintext on failure. Recovery paths never decrypt. |
| Key hygiene | Two findings, fixed (a stray copy of the passphrase, and decrypted pages left in the cache after close). Limits listed below. |
| Rekey crash safety | Held in 400 kills (200 rollback-journal, 200 WAL): every image opened under exactly one key with all acknowledged data. |
| Plaintext leakage | No plaintext in the database, journal or WAL at any instant. Sort spill found and fixed: encrypted databases sort in memory (below). |
| BCL-only primitives | Yes. |
| Browser | Reports encryption unsupported. |

Also found and fixed: the "WAL" variants of the encrypted pager tests did not use a WAL (below).

## Format conformance

Checked against the design page and the byte layout in encryption.md:

- PBKDF2-HMAC-SHA512 with 256,000 iterations and the file's 16-byte salt gives the AES-256 key (SQLCipher 3 settings: SHA-1, 64,000, 1024-byte pages, reserve 48).
- The page MAC key is PBKDF2 of the AES key over the salt with every byte XORed with `0x3a`, 2 iterations, using the KDF digest.
- A page is AES-256-CBC ciphertext without padding, then the 16-byte IV, then HMAC over ciphertext, IV and the 4-byte little-endian page number, then zeros.
  The codec needs 16 + the digest size rounded up to 16 bytes (80 for SHA-512, 48 for SHA-256 and SHA-1); the reference writes 80 in the database header for every digest and OneLite does the same.
- Page 1 carries the salt in its first 16 bytes and encrypts from byte 16; the plaintext-header variant keeps its first N bytes as they are.
- Raw keys `x'<64 hex>'` and `x'<96 hex>'` skip only the first PBKDF2 step.

Evidence:

- `EncryptionKnownAnswerTests` (new): three pages (SQLCipher 4 page 2 and page 1; SQLCipher 3 page 2) whose bytes were produced by `tools/scripts/gen_cipher_kat.py` with
  OpenSSL's `enc -aes-256-cbc -nopad` and Python's `hashlib.pbkdf2_hmac` and `hmac`, with no OneLite code involved. They decode to the expected plaintext, fail under
  another passphrase, another page number, or one flipped bit, and a re-encode carries a new IV.
- `PageCodecTests`: the NIST SP 800-38A and RFC 6070 vectors, seven files written by the SQLCipher shell (`testdata/cipher`) decoded with their MACs recomputed independently.
- `tools/scripts/cipher_worker.sh interop all` against SQLCipher 4.5.6: `passed 422 failed 0 {read 66, write 56, crash 300}` (300 crash images, 150 with a hot journal and 150
  with a WAL).

**Test vectors and terms.** Zetetic publishes no numeric test vectors: the design page has prose and one illustrative hexdump of a version-2-era file whose full
key material is not given, so it cannot be turned into a test. The known-answer pages above are our own, derived from the prose. Nothing from Zetetic's pages is copied into
the repository, so nothing is added to THIRD-PARTY-NOTICES. The page is marked "© Zetetic, LLC. All rights reserved"; the repository cites it and does not reproduce it.

**Authenticated and not.** The MAC covers the ciphertext, the IV and the page number. It does not cover the salt (page 1, bytes 0-15), the plaintext header of a
plaintext-header database, the zero padding after the MAC, or anything about the file as a whole. Consequences, all inherent in the format:

- An attacker who can write the file can replace a page with an older valid copy of the same page number (a rollback or replay of single pages) and the page will pass its MAC.
  The reference behaves the same. Whole-file freshness needs something outside the file.
- With a plaintext header the first N bytes can be changed. OneLite then checks the decrypted header against the settings (page size and reserved bytes) and reports
  corruption on mismatch, but fields outside those checks are the attacker's to change.
- Changing the salt makes every page fail its MAC; it cannot be used to forge anything.

SQLCipher 1 and 2 formats are not supported.

## IV freshness

`PageCodec.Encode` fills the IV from `RandomNumberGenerator` on every call; nothing else produces an IV. The pager calls `Encode` once for each write event:
a database page on spill or commit, a journal record, a WAL frame, and the in-memory-journal restore. The code never copies one encryption to a second place for
new content: playback and checkpoint copy stored bytes, which are copies of a single encryption, not a second encryption under the same IV. (A checkpointed page therefore
has the same IV as the WAL frame it came from; that is the same ciphertext, not a reuse.)

Tests (`EncryptionReviewTests`):

- `CodecGivesEveryEncodeOfTheSamePageAnotherIv`: 3,000 encodes of one page give 3,000 distinct IVs and bodies; page 1 too.
- `RewritingIdenticalContentUsesNewIvsInDatabaseJournalAndWal`: the same plaintext written three times. Rollback journal (persist mode, so the record bodies stay readable) and WAL (24 frames
  for three commits of eight pages): every IV in the database file, journal records and WAL frames is distinct from every other.
- `RekeyToTheSameKeyStillWritesNewIvs`: after a rekey every page's IV differs from before.

## MAC before decrypt

- `TryDecode` computes the HMAC over the stored bytes and compares with `CryptographicOperations.FixedTimeEquals` before it calls the cipher; on a mismatch it clears the
  caller's buffer and returns false. There is no early exit for an all-zero page (checked against the shell, encryption.md "All-zero pages").
- Reads of the database file, of WAL frames and of page 1 all go through `DecodeStored`. A failed MAC is `SQLITE_NOTADB` ("file is not a database") for page 1 and
  `SQLITE_CORRUPT` ("database disk image is malformed") naming the page for any other, as the shell reports.
- The WAL probe at open (`ResumeState.WalProbe`) only asks whether the file exists. Hot-journal playback, WAL recovery and checkpoint never decrypt: they validate the
  journal or WAL checksum chain, which runs over the stored bytes, and copy stored bytes. A forged journal can therefore put arbitrary bytes into the database file, but
  the next read of such a page fails its MAC, so it can cost availability and cannot make OneLite use attacker-chosen plaintext. A wrong key is not noticed by recovery; the first read afterwards fails.
- Tests: `AnyChangedBitFailsTheMacAndReleasesNoPlaintext` (a bit flipped in page 1's ciphertext, in a later page's ciphertext, in the IV and in the MAC; also the wrong page number; the output buffer is all zero after the failure),
  `DamagedStoredPagesAreCorruptNotExceptions` (a file cut mid-page; a zeroed page 1), the existing damaged-page, tamper, page-swap and zero-page tests, and the cipher-pager fuzz target.

## Key hygiene

Findings, both fixed:

1. `CipherKey.FromPassphrase(string)` encoded the string with `Encoding.UTF8.GetBytes`, which leaves an unpinned `byte[]` copy of the passphrase on the heap that is never wiped.
   It now encodes straight into the pinned array. Test: `ThePassphraseConstructorLeavesOnlyThePinnedCopy` (same bytes as the span overload, including multi-byte text and the empty string).
2. Closing an encrypted pager wiped the keys and the I/O scratch buffers, but not the cached decrypted pages or the page-1 scratch. `Pager.Dispose` now wipes them.
   Test: `ClosingAnEncryptedPagerWipesKeysAndCachedPlaintext`.

Checked and fine: `CipherKey` and `PageCodec` hold the passphrase, the AES key and the MAC key in pinned arrays and zero them on `Dispose` (`KeysAreZeroedOnDispose` reads the private arrays);
a codec whose construction fails disposes itself (a disposed key is refused); rekey disposes the old codec and key on commit and the new ones on rollback; `NoSecretAppearsInErrorTextOrToString` opens a database with a wrong passphrase and a wrong raw key and checks the exception text, `Detail` and `ToString()` for both, and `ToString` of `CipherKey` and `PagerOptions`.
Messages name pages and settings, never key material. The `onelite` command-line tool has no key option yet.

Limits (accurate as documented in encryption.md, plus):

- The runtime's `Aes`, `IncrementalHash` and `Rfc2898DeriveBytes` hold their own copies of key material that we can only dispose; nothing is `mlock`ed (the reference locks its memory when it can).
- A caller's `string` passphrase stays in managed memory until collected.
- The page cache holds decrypted pages by design. Cache frames are not pinned, so the garbage collector may have left older copies behind before the wipe at close. Swap files and core dumps are outside any guarantee.
- A pager in `journal_mode=MEMORY` keeps decrypted original pages in memory until the transaction ends.

## Rekey crash safety

The protocol (encryption.md, "Rekey") is a single ordinary write transaction in which every page is rewritten under the new key while the journal or older WAL frames keep the old key's pages;
the commit point is the usual one. It was read against atomiccommit and wal and has no step of its own that could leave a mixture: journal records are always written with the key the page is stored under,
playback copies stored bytes, and the new key becomes the codec only after the commit.

Evidence:

- `EncryptedPagerTests.CrashDuringRekeyOpensUnderExactlyOneKey` (every storage call of a rekey is a crash point; four crash policies; rollback and WAL, small caches). These WAL cases did not run in WAL
  mode before this review (see "Test defect found"); they do now, and pass.
- SIGKILL campaigns judged by the SQLCipher 4.5.6 shell (`cipher_worker.sh rekey`, seed 2302, 1024-byte pages, cache 50, 1,000 iterations):

  | Campaign | Kills | Result |
  |---|---|---|
  | rekey, rollback journal | 200 (hot journal at kill: 141) | 0 corruptions, 0 lost, 0 partial, 0 not-exactly-one-key |
  | rekey, WAL | 200 (hot WAL at kill: 159) | 0 corruptions, 0 lost, 0 partial, 0 not-exactly-one-key |
  | writer, rollback journal (seed 2301) | 200 (hot: 123) | 0 corruptions, 0 lost |
  | writer, WAL (seed 2301) | 200 (hot: 164) | 0 corruptions, 0 lost |

Residual: a reader holding the old key and an older WAL snapshot keeps reading its snapshot; its next transaction fails with `SQLITE_NOTADB`, as a wrong key does. A rekey keeps the salt unless the new
key brings one, which is fine for secrecy (the keys differ) but means a file's salt identifies the file across rekeys.

## Plaintext leakage

Where OneLite writes files for an encrypted database: the database file, the rollback journal and super-journal, the WAL and `-shm`. The pager creates no other file: no statement journal
(savepoints are kept in the page cache), no temporary database.

- `NoFileEverHoldsPlaintextOrThePassphrase`: a workload that spills (cache of 6 pages, 24 pages), grows the file and then rekeys, in delete, truncate, persist and WAL modes, with a crash image taken after every storage call that
  keeps everything written. No image of any file holds the canary text, the passphrase text or the `SQLite format 3` magic. It also asserts the journal (or the log) really appeared in the images.
- `NoFileOnDiskHoldsPlaintextWhileATransactionIsOpen`: the same on real files: while a spilling write transaction is open, after its commit and after close, every file in the directory.
- The `-shm` file holds the wal-index (page numbers and frame numbers, no page content). The journal and WAL headers, the super-journal's file names and the frame headers are plaintext, as in the reference.

**Sort spill (finding, fixed).** The reference leaves sorter and temporary files unencrypted and tells users to keep temporary storage in memory. OneLite's sorter writes
runs of whole rows to a temporary file once a sort passes its memory budget (the page-cache size, never less than 256 KiB). With the first merge of the SELECT surface that became a plaintext
leak for encrypted databases. Fix: `Connection.SortVfs`, the one place the engine gets a VFS for temporary files, is null whenever the connection's pager is encrypted (a key or a key provider),
so `ORDER BY`, `GROUP BY`, `DISTINCT` and compound sorts keep every row in memory, however large. Every other place in the engine that could write a temporary file was checked: no code other than
the sorter opens a temporary file (the aggregate sorters are memory-only by construction, and the pager has no statement journals or temporary databases).

**Limit: memory-only sorting for encrypted databases.** A sort over more rows than fit in memory uses memory for all of them; there is no external sort for an encrypted database. Encrypting spill runs
is a possible later feature.

Tests (`EncryptedSortTests`): a 3,500-row table of canary text (about 700 KB, past the 256 KiB sort budget), `ORDER BY`, `DISTINCT`, `GROUP BY` and `GROUP BY ... group_concat` at the smallest cache size.
The control run on a plain copy opens temporary files holding the canary (so the test can see a spill); the encrypted copy (SQLCipher 4 pages written by `CipherFile`) opens none. With the fix reverted the encrypted test
fails with four temporary files open after the first row of the four queries; with it the test passes.

## BCL-only primitives

Every use of `System.Security.Cryptography` under `src/` was listed: `Aes` (CBC, no padding, `EncryptCbc`/`DecryptCbc`), `IncrementalHash.CreateHMAC`, `Rfc2898DeriveBytes.Pbkdf2`, `RandomNumberGenerator`,
`CryptographicOperations.FixedTimeEquals` and `ZeroMemory` in the codec and key types; elsewhere only `RandomNumberGenerator` for `randomblob`, journal nonces and temporary file names. No `AesManaged`, no ECB,
no hand-written primitive, no `==` or `SequenceEqual` on a MAC.

## Browser

`PageCodec.IsSupported` is false when `OperatingSystem.IsBrowser()`; creating a codec, or opening a pager with a key, throws `SQLITE_ERROR`, "encryption is not supported in the browser build".
The browser build is compiled in the CI step "browser-wasm smoke".

## Test defect found

The encrypted pager tests that were meant to cover WAL mode opened the database with the `Wal` journal-mode option and then called `ChangeJournalMode(Wal)`. A pager opened with that option already reports WAL, so the
call was a no-op, the file stayed in rollback mode, and "WAL" cases (round trip, crash sweeps, rekey sweeps) were exercising the rollback journal; the round-trip check read the `-wal` file after close, when it no longer exists.
The helper now opens in rollback mode first, and `NoFileEverHoldsPlaintextOrThePassphrase` fails if the log or journal never appears. All of the affected tests pass in real WAL mode. The kill campaigns
use `mode=delete` and then `ChangeJournalMode(Wal)`, so they were not affected.

## Not covered

- SQLCipher 4.20.0 was not re-run in this review (its recorded behaviour is in encryption.md).
- Side channels other than the MAC comparison (cache timing in the BCL's AES, for example) are the platform's.
- `PRAGMA key`/`rekey` as SQL, browser encryption and performance are out of scope.
