#!/usr/bin/env bash
# Regenerates testdata/cipher/*.db with a SQLCipher shell (set SQLCIPHER; set LD_LIBRARY_PATH if it needs one).
# Each file holds one table, vec(n, label), with the rows (1,'row-one') and (2,'row-two'). The keys and settings are listed in
# testdata/cipher/README.md. The salt and IVs are random, so regenerating changes the bytes but not what the tests check.
set -eu
: "${SQLCIPHER:?}"
OUT=${1:-testdata/cipher}
mkdir -p "$OUT"
ROWS="CREATE TABLE vec(n INTEGER PRIMARY KEY, label TEXT); INSERT INTO vec VALUES(1,'row-one'),(2,'row-two');"
mk() { rm -f "$OUT/$1.db"; $SQLCIPHER "$OUT/$1.db" "$2 $ROWS" > /dev/null; }
mk v4-default "PRAGMA key='test123';"
mk v4-p1024 "PRAGMA key='test123'; PRAGMA cipher_page_size=1024; PRAGMA kdf_iter=4000;"
mk v3 "PRAGMA key='test123'; PRAGMA cipher_compatibility=3;"
mk v4-sha256-sha1 "PRAGMA key='test123'; PRAGMA kdf_iter=4000; PRAGMA cipher_kdf_algorithm=PBKDF2_HMAC_SHA256; PRAGMA cipher_hmac_algorithm=HMAC_SHA1; PRAGMA cipher_page_size=1024;"
mk raw "PRAGMA key=\"x'2DD29CA851E7B56E4697B0E1F08507293D761A05CE4D1B628663F411A8086D99'\"; PRAGMA cipher_page_size=1024;"
mk raw-salt "PRAGMA key=\"x'98483C6EB40B6C31A448C22A66DED3B5E5E8D5119CAC8327B655C8B5C483648101010101010101010101010101010101'\"; PRAGMA cipher_page_size=1024;"
mk plaintext-header "PRAGMA key='test123'; PRAGMA kdf_iter=4000; PRAGMA cipher_page_size=1024; PRAGMA cipher_plaintext_header_size=32; PRAGMA cipher_salt=\"x'01010101010101010101010101010101'\";"
ls -l "$OUT" | tail -n +2
