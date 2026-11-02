# Encrypted known-answer files

Written by `tools/scripts/gen_cipher_vectors.sh` with a SQLCipher shell (community edition 4.5.6). Each holds `vec(n, label)` with
the rows `(1,'row-one')` and `(2,'row-two')`. Passphrase `test123` unless noted.

| File | Settings |
|---|---|
| `v4-default.db` | defaults: 4096-byte pages, 256,000 iterations, SHA-512 |
| `v4-p1024.db` | 1024-byte pages, `kdf_iter` 4000 |
| `v3.db` | `cipher_compatibility = 3`: 1024-byte pages, 64,000 iterations, SHA-1 |
| `v4-sha256-sha1.db` | KDF SHA-256, HMAC SHA-1, 4000 iterations, 1024-byte pages |
| `raw.db` | raw key `x'2DD29CA8...6D99'`, 1024-byte pages |
| `raw-salt.db` | raw key and salt `x'98483C6E...01010101'`, 1024-byte pages |
| `plaintext-header.db` | 32-byte plaintext header, salt `01010101010101010101010101010101` kept outside, 4000 iterations, 1024-byte pages |
