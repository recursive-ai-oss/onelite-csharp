# Encryption: the SQLCipher-compatible page codec

Status: the page codec, key derivation and a whole-file transform are verified against files written by SQLCipher, and the pager uses them: a database opened with
a key is encrypted in the database file, the rollback journal and the WAL, and can be rekeyed online (see [In the pager](#in-the-pager)). The SQL surface
(`PRAGMA key`, `rekey`, `cipher_*`, `Password=`) is not built yet; keys are given through `PagerOptions` / `DatabaseOptions`.

The format is SQLCipher's, from Zetetic's published design and API documentation (the "SQLCipher Design" and "SQLCipher API" pages). The pages leave several
byte-level facts unsaid; those were settled by writing databases with the reference shell (community editions 4.5.6 and 4.20.0) and inspecting the bytes, and
are marked **observed** below. The primitives are FIPS 197 (AES), NIST SP 800-38A (CBC), RFC 2104 (HMAC) and RFC 8018 (PBKDF2), all from the .NET base class
library (`Aes`, `IncrementalHash` HMAC, `Rfc2898DeriveBytes.Pbkdf2`, `RandomNumberGenerator`, `CryptographicOperations`). Nothing is hand-rolled.

## Page layout

For page size `P` and reserve `R` (`R = 16 + hmacSize`, rounded up to a multiple of 16: 80 for SHA-512, 48 for SHA-256 and SHA-1), a stored page is:

| Bytes | Content |
|---|---|
| `[0, P-R)` | AES-256-CBC ciphertext, no padding |
| `[P-R, P-R+16)` | the IV, fresh from `RandomNumberGenerator` on every write |
| `[P-R+16, P-R+16+hmacSize)` | HMAC of `ciphertext ‖ IV ‖ pageNumber` (page number: 4 bytes, little-endian) |
| rest | zero padding, not authenticated |

- The plaintext page the caller hands over has `P` bytes; its last `R` bytes are not stored, and `Decode` returns them zeroed.
- The database header's reserved-bytes field (fileformat2 §1.3.8, offset 20) must be **at least** `R`, so no cell reaches the IV and MAC. **Observed:** SQLCipher writes
  80 there for every digest, including SHA-1 (where `R` is 48), and opens files whose header says 48 or 80.
- **Page 1** holds the 16-byte salt where the plaintext has `"SQLite format 3\0"`. Its ciphertext and MAC cover bytes `[16, …)`, so the salt is not authenticated
  (Zetetic's forum notes this; it is the reference behaviour). Decoding restores the magic string.
- **Plaintext header** (`cipher_plaintext_header_size = N`, N a multiple of 16, 16 ≤ N < P-R): the first `N` bytes of page 1 are stored as they are, the ciphertext and MAC start at `N`,
  and the salt is not in the file: the application keeps it (`PRAGMA cipher_salt`) or supplies it inside a 96-hex raw key. **Observed:** the MAC starts at `N`.
- **All-zero pages** fail their MAC like any other damaged page. **Observed** (`tools/scripts/cipher_zero_probe.py`, SQLCipher 4.5.6 and 4.20.0): with a page of a small
  database overwritten by zeros or by random bytes, the shell's `cipher_integrity_check` reports "HMAC verification failed for page N" for both, a zeroed free-list trunk or table
  root is "unable to get the page" or malformed exactly as with random bytes, and `VACUUM` fails the same way. There is no zero-page exemption. A page past the end of the file is
  not a page of the file: the pager reads it as zeros without calling the codec, which is what SQLite does for a short file.
- The MAC is checked first, in constant time (`CryptographicOperations.FixedTimeEquals`), before anything is decrypted. The page number is part of the MAC, so
  pages cannot be swapped.

## Keys

- **Passphrase:** its UTF-8 bytes go through PBKDF2 (`kdf_iter` iterations, the KDF digest, the file's 16-byte salt) to a 32-byte AES key.
- **Raw key** `x'<64 hex>'`: the 32 bytes are the AES key; no PBKDF2 step. The salt is read from the file (or generated for a new file).
- **Raw key with salt** `x'<96 hex>'`: the first 32 bytes are the key, the last 16 the salt.
- **MAC key:** PBKDF2 of the AES key, with salt = file salt with every byte XORed with `0x3a`, 2 iterations, 32 bytes. **Observed:** the digest of this step is the
  *KDF* digest (`cipher_kdf_algorithm`), not the HMAC digest; the two only differ when they are set apart, and a file with KDF SHA-256 and HMAC SHA-1 settles it.
- Text parsing follows `PRAGMA key`: `x'` + 64 or 96 hex digits + `'` is raw; anything else, including malformed hex, is a passphrase.

## Parameters

Not stored in the file, so the opener must know them (as with SQLCipher's pragmas).

| Setting | v4 default (`cipher_compatibility = 4`) | v3 (`= 3`) | Range |
|---|---|---|---|
| page size | 4096 | 1024 | power of two, 512 to 65536 |
| KDF iterations | 256,000 | 64,000 | ≥ 1 |
| KDF digest | SHA-512 | SHA-1 | SHA-1, SHA-256, SHA-512 |
| HMAC digest | SHA-512 | SHA-1 | same |
| plaintext header | 0 | 0 | 0 or multiple of 16 |

SQLCipher 1 and 2 formats (no HMAC, 4,000 iterations) are not supported. A 512-byte page cannot hold the 480 usable bytes SQLite needs once 80 bytes are reserved, so
the reference (and SQLite) raise such a database to 1024-byte pages; the codec itself accepts 512.

## Errors

- A wrong key, a wrong setting or a file that is not encrypted fails page 1: `SQLITE_NOTADB`, "file is not a database" (identical to the reference).
- With a plaintext header the file still parses, and the reference reports `SQLITE_CORRUPT` "database disk image is malformed" (4.20.0; 4.5.6 says "SQL logic error"). We follow 4.20.0.
- A later page whose MAC fails: `SQLITE_CORRUPT`, "database disk image is malformed", with the page number in `Detail`. **Observed** as the reference's behaviour.
- Wrong buffer sizes or page 0 are programmer errors (`ArgumentException`), not file errors.
- The browser build has no BCL AES: `PageCodec.IsSupported` is false there and creating a codec throws `SQLITE_ERROR` "encryption is not supported in the browser build".

## Key handling and its limit

`CipherKey` and `PageCodec` keep key bytes (passphrase or raw key, the AES key, the MAC key) in arrays on the pinned object heap and zero them on `Dispose`.
The `Aes` object and the HMAC object receive their own copy of the key inside the runtime; disposing them is all we can do. A caller's `string` passphrase, the
runtime's copies before pinning and any swap or core dump are outside this guarantee: managed memory cannot promise that no copy of a secret exists.

## In the pager

Code: `Storage/Pager/Pager.Crypto.cs`, plus the seam sites listed below. Without a key none of it runs: each seam is one `_cipher is null` branch.

**Options** (`PagerOptions`, also `StorageEngine` and, read-only, `DatabaseOptions.Key` / `Cipher`):

| Option | Meaning |
|---|---|
| `Key` | a `CipherKey`: passphrase, raw key `x'64hex'`, raw key with salt `x'96hex'` (`CipherKey.Parse` reads the `PRAGMA key` text). The pager keeps its own pinned copy; the caller may dispose theirs. |
| `KeyProvider` | a callback that returns the key the first time it is needed (once per pager). The pager owns the key it returns. Set `Key` or `KeyProvider`, not both. |
| `Cipher` | `CipherSettings`: page size, KDF iterations, KDF and HMAC digests, plaintext header size. Default `CipherSettings.V4`; `ForCompatibility(3)` is `cipher_compatibility = 3`. These are not stored in the file. |
| `CipherSalt` | the 16-byte salt of a database with a plaintext header (the file does not carry it); `Pager.CipherSalt` gives it for a new one. |

With a key, `PageSize` and `ReservedBytes` of the options are ignored: the page size is the cipher's and a new file's header reserve is 80 (what SQLCipher writes for every digest). A page size
below 1024 is refused (`SQLITE_MISUSE`) because 480 usable bytes must remain. `TrySetPageSize` returns false. The browser build reports `SQLITE_ERROR` "encryption is not supported in the browser build" at open.

**Where the codec runs.** The page cache holds plaintext; the stored form exists only in scratch buffers.

| Site | Action |
|---|---|
| read a page from the database file or a WAL frame (`GetCore`) | read into a scratch page, `Decode` into the frame; a short read is zero-padded and then fails its MAC |
| first read of page 1 (`BeginRead`) | read the whole stored page 1; the codec is made from its first 16 bytes (the salt) and the key, so the first `Decode` proves the key; the header is read from the decoded page |
| spill and commit write of the database file | `Encode` into a scratch page (spill) or a pool of pages (a gather write of up to 64 pages) |
| rollback journal record | the record holds the encoded page and its checksum runs over those stored bytes |
| WAL frame | the frame holds the encoded page and the WAL checksums run over those stored bytes |
| hot-journal playback, WAL recovery, checkpoint | none: they copy and checksum stored bytes, so recovery needs no key |
| `journal_mode = MEMORY` rollback | the in-memory original is encoded when it is written back |

**Journal and WAL, observed** (`tools/scripts/cipher_probe.py`, 4.5.6): a journal left hot by a killed shell has the usual header (sector 512) and records of `page number ‖ P stored bytes ‖ checksum`; the checksum
matches the fileformat2 algorithm over the stored bytes and the record is not the plaintext. A WAL left by a killed shell has an ordinary header (checksum valid) and frames whose cumulative checksum
matches over the stored bytes. The journal header, the super-journal trailer and the frame headers stay plaintext. A journal record carries a fresh IV, so it differs from the database file's copy of the same page.

**New and existing files.** A zero-length file is a new database: the codec gets a random salt (or the key's or `CipherSalt`) at the first transaction. A file with content: page 1 must decode under the key,
else `SQLITE_NOTADB` "file is not a database" (wrong key, wrong settings, or an unencrypted file); with a plaintext header the file parses, so it is `SQLITE_CORRUPT` "database disk image is malformed" (4.20.0).
After decoding, the header's page size must equal the cipher's and its reserve must be at least the cipher's reserve, else `SQLITE_CORRUPT`. A later page that fails its MAC is `SQLITE_CORRUPT` naming the page in `Detail`.
If another process changes the file's salt (a rekey), the next transaction derives a new codec from the new salt and the same key.

## Rekey (online, crash-safe)

`Pager.Rekey(newKey, newSettings?)` (re-entrant, inside a write transaction), `RekeySync` and `RekeyAsync` (their own transaction). The page size and plaintext header size cannot change; digests and iterations can.
The salt is kept unless the new key brings one, so the file stays recognisable and a plaintext header keeps matching its salt.

Protocol: a rekey is one ordinary write transaction in which every page of the database is made writable (so its old form is journaled) and dirtied. While it is under way

- pages are **written to the database file and to the WAL with the new key** (spill and commit);
- **journal records are written with the old key** (the journal holds the old file, which is what playback restores);
- pages already written with the new key are tracked in a bit set, so a page that was spilled and is read again in the same transaction decodes with the new key;
- at commit the new codec becomes the codec (`EndRekey`), at rollback it is discarded.

Crash safety needs nothing beyond the usual commit rules, because playback and checkpoint copy stored bytes. Rollback mode: before the journal is retired the file is the old file plus new-key pages that the
hot journal overwrites with old-key pages (all of them were journaled), so recovery returns the old key's file entire; after the commit point it is the new file. WAL mode: the rekey commit's frames are either
published (new key; older frames of the same pages are shadowed, and a checkpoint that stops half way leaves the WAL intact over a mixed database file) or never published (old key). Either way the file opens
under exactly one key and holds every committed transaction. `journal_mode = OFF` and `MEMORY` are refused (`SQLITE_MISUSE`): their recovery promises nothing. A rekey of a database that has no page on disk yet just
replaces the key. A reader that holds the old key and an older WAL snapshot keeps reading its snapshot; its next transaction fails with `SQLITE_NOTADB`, as a wrong key does.

Tests: `EncryptedPagerTests.CrashDuringRekeyOpensUnderExactlyOneKey` (every storage call of a rekey is a crash point under drop-unsynced, keep-all, reordered and torn-sector policies; rollback and WAL modes, small caches that force spills), and the
SIGKILL campaign `rekey-kill` in `tools/OneLite.Durability`, judged by the SQLCipher shell.

## ATTACH with a different key

Each attached database is its own pager with its own `Key`; the pager has no connection-wide key. `MultiCommit.CommitSync` commits encrypted and plain databases together through the super-journal
(its content is file names, not data). The SQL `ATTACH ... KEY` form belongs to the engine.

## Limits

- Managed memory cannot promise there are no copies of a key: see Key handling. The page cache holds plaintext by design; so do the scratch buffers (the cache and the scratch buffers are zeroed when the pager closes).
- The runtime's `Aes.EncryptCbc` / `DecryptCbc` allocate 64 bytes per call (the HMAC allocates nothing), so a read that misses the cache allocates 64 bytes; a cached read allocates nothing
  (`EncryptedPagerTests`).
- Opening a database with a PBKDF2 count of 256,000 spends tens of milliseconds in the first `BeginRead` on the calling thread, also under the async driver.
- A wrong key on a hot-journal or WAL database is not noticed by recovery (it copies stored bytes); the first read after recovery fails.
- **Sorting is memory-only.** Sort runs hold whole rows and temporary files are not encrypted, so a connection to an encrypted database never spills a sort to a temporary file (`ORDER BY`, `GROUP BY`, `DISTINCT`); a very large sort uses a lot of memory.
- Browser: unsupported.

## Whole-file transform

`CipherFile.Encrypt` and `CipherFile.Decrypt` convert a whole database page by page. Encrypting needs a plaintext database that already reserves the bytes (header offset 20 ≥ `R`) and
whose page size equals the setting; build one with `.filectrl reserve_bytes 80` followed by `VACUUM` in the `sqlite3` shell. `tools/OneLite.Cipher` wraps both.

## Checks

- `tests/OneLite.Tests/Crypto`: SP 800-38A and RFC 6070 vectors; seven oracle-written files in `testdata/cipher` (`tools/scripts/gen_cipher_vectors.sh`) decoded and their MACs recomputed independently;
  wrong key, tamper, page-swap, zero page, random pages, round trips for every digest pair and page sizes 512 to 65536.
- `tests/OneLite.Tests/Crypto/EncryptedPagerTests.cs`: round trips in every journal mode, stored bytes hold no plaintext, wrong key / unencrypted file / damaged page errors, raw key, V3 settings, plaintext header with salt, key provider,
  crash sweeps of rollback and WAL commits, rekey sweeps, rollback of a rekey, ATTACH-style two keys, allocation.
- `tools/scripts/cipher_pager_interop.py`: interop through the pager both ways and SQLCipher crash images; `OneLite.Durability cipher-kill` / `rekey-kill`: SIGKILL campaigns judged by the SQLCipher shell.
- `tools/scripts/cipher_interop.sh`: read interop (SQLCipher creates, we decrypt, `integrity_check` ok, `.dump` equal to `sqlcipher_export`) and write interop (we encrypt, SQLCipher opens, `integrity_check` ok, dump equal to the source), against both oracles.
- `fuzz/OneLite.Fuzz/CipherFuzz.cs`: `OneLite.Fuzz cipher SECONDS [SEED]`; `CipherPagerFuzz.cs`: `OneLite.Fuzz cipher-pager SECONDS [SEED]` mutates encrypted file sets (database, hot journal, WAL, cut-off rekey) and opens them with the key: only typed errors allowed.

See [encryption-review.md](encryption-review.md) for the independent review of this path (findings, evidence and residual risks).
`EncryptionReviewTests` (IV freshness, plaintext and passphrase scans over crash images and real files, MAC-first, key wiping) and `EncryptionKnownAnswerTests` (pages built with OpenSSL and Python by `tools/scripts/gen_cipher_kat.py`) add to the checks above.

Files that touch crypto: `src/OneLite/Storage/Crypto/{CipherSettings,CipherKey,PageCodec,CipherFile}.cs` and `src/OneLite/Storage/Pager/Pager.Crypto.cs`. No unsafe code, no native calls.
