#!/usr/bin/env bash
# Read and write interop of the SQLCipher-compatible page codec against a SQLCipher shell and sqlite3.
#
#   SQLCIPHER   command line of the SQLCipher shell (set LD_LIBRARY_PATH before when it needs one)
#   SQLITE3     the sqlite3 shell
#   TOOL        command that runs the OneLite.Cipher tool, e.g. "dotnet /path/OneLite.Cipher.dll"
#   WORK        scratch directory (a real file system)
#   CORPUS      directory of plaintext .db files for the write direction (optional)
#   ROWS        rows per generated table (default 600)
#
# Read direction: the shell creates an encrypted database; the tool decrypts it page by page; sqlite3's integrity_check must be ok and its
# .dump must equal the .dump of the shell's own sqlcipher_export to plaintext. A wrong key must fail with the shell's "file is not a database".
# Write direction: sqlite3 rebuilds each corpus file with the reserve the cipher needs; the tool encrypts it; the shell opens it with the key,
# integrity_check must be ok and its dump equal to the source's.
set -u
: "${SQLCIPHER:?}" "${SQLITE3:?}" "${TOOL:?}" "${WORK:?}"
ROWS=${ROWS:-600}
mkdir -p "$WORK"
pass=0; fail=0; skip=0
RES="$WORK/results.txt"; : > "$RES"
note() { echo "$*"; }
CUR=
ok() { pass=$((pass+1)); echo "ok $CUR" >> "$RES"; }
bad() { fail=$((fail+1)); echo "FAIL $CUR" >> "$RES"; note "FAIL $*"; }
skipped() { skip=$((skip+1)); echo "skip $CUR $*" >> "$RES"; }

# read_case NAME KEYTEXT "PRAGMAS" "TOOL OPTIONS" [wrongkey-pragmas-ok]
read_case() {
  local name=$1 key=$2 pragmas=$3 opts=$4
  CUR=$name
  local d="$WORK/$name"; rm -rf "$d"; mkdir -p "$d"
  local pre="PRAGMA key=\"$key\"; $pragmas"
  $SQLCIPHER "$d/enc.db" "$pre
CREATE TABLE t1(id INTEGER PRIMARY KEY, a TEXT, b BLOB, c REAL);
CREATE INDEX t1a ON t1(a);
CREATE TABLE t2(x, y);
CREATE VIEW v1 AS SELECT id, a FROM t1 WHERE id % 2 = 0;
WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM s WHERE i<$ROWS) INSERT INTO t1 SELECT i, 'row'||i, randomblob((i*37)%3000), i*1.5 FROM s;
INSERT INTO t2 VALUES(NULL, 'x'), (1, zeroblob(70000)), ('é', 2.5);
DELETE FROM t1 WHERE id % 5 = 0;" > "$d/create.log" 2>&1 || { bad "$name create: $(head -c 200 "$d/create.log")"; return; }
  $SQLCIPHER "$d/enc.db" "$pre
ATTACH DATABASE '$d/plain.db' AS p KEY '';
SELECT sqlcipher_export('p');" > "$d/export.log" 2>&1 || { bad "$name export: $(head -c 200 "$d/export.log")"; return; }
  # shellcheck disable=SC2086
  if ! $TOOL decrypt "$d/enc.db" "$d/dec.db" --key "$key" $opts > "$d/dec.log" 2>&1; then bad "$name decrypt: $(head -c 200 "$d/dec.log")"; return; fi
  local ic; ic=$($SQLITE3 "$d/dec.db" "PRAGMA integrity_check;" 2>&1)
  [ "$ic" = ok ] || { bad "$name integrity_check: $ic"; return; }
  $SQLITE3 "$d/plain.db" .dump > "$d/plain.dump" 2>&1
  $SQLITE3 "$d/dec.db" .dump > "$d/dec.dump" 2>&1
  cmp -s "$d/plain.dump" "$d/dec.dump" || { bad "$name dump differs"; return; }
  # wrong key: ours must say what the shell says (NOTADB, or a generic error when a plaintext header leaves the file parseable)
  local wrong="wrong-$key"
  case "$key" in x\'*) wrong="x'0000${key:6}";; esac
  local want="file is not a database" wantcode=26
  case "$opts" in *--plaintext-header*) want="database disk image is malformed"; wantcode=11;; esac
  # shellcheck disable=SC2086
  local wk; wk=$($TOOL check "$d/enc.db" --key "$wrong" $opts 2>&1 | head -1)
  case "$wk" in "error $wantcode $want"*) ;; *) bad "$name wrong key (ours): $wk"; return;; esac
  local sw; sw=$($SQLCIPHER "$d/enc.db" "PRAGMA key=\"$wrong\"; $pragmas SELECT count(*) FROM sqlite_master;" 2>&1 | head -1)
  # the plaintext-header wrong-key text differs by shell version (4.5.6: SQL logic error; 4.20.0: malformed); ours follows the newer one
  case "$sw" in *"$want"*|*"SQL logic error"*) ;; *) bad "$name wrong key (shell): $sw"; return;; esac
  ok
}

# write_case NAME KEYTEXT "PRAGMAS" "TOOL OPTIONS" RESERVE PAGESIZE SRC
write_case() {
  local name=$1 key=$2 pragmas=$3 opts=$4 reserve=$5 src=$7
  CUR=$name
  local d="$WORK/w-$name"; rm -rf "$d"; mkdir -p "$d"
  cp "$src" "$d/src.db"
  local sic; sic=$($SQLITE3 "$d/src.db" "PRAGMA integrity_check;" 2>&1 | head -1)
  [ "$sic" = ok ] || { skipped "source integrity_check: $sic"; rm -rf "$d"; return; }
  $SQLITE3 "$d/src.db" "PRAGMA journal_mode=DELETE;" > /dev/null 2>&1
  # rebuild with the reserve the cipher needs, same page size
  printf '.filectrl reserve_bytes %s\nVACUUM;\n' "$reserve" | $SQLITE3 "$d/src.db" > "$d/vac.log" 2>&1 || { bad "$name vacuum: $(head -c 200 "$d/vac.log")"; return; }
  # a 512-byte page cannot keep the 480 usable bytes sqlite needs once 80 are reserved, so VACUUM may raise the page size: read it afterwards
  local ps; ps=$($SQLITE3 "$d/src.db" "PRAGMA page_size;")
  $SQLITE3 "$d/src.db" .dump > "$d/src.dump" 2>&1
  # shellcheck disable=SC2086
  if ! $TOOL encrypt "$d/src.db" "$d/enc.db" --key "$key" --page-size "$ps" $opts > "$d/enc.log" 2>&1; then bad "$name encrypt: $(head -c 200 "$d/enc.log")"; return; fi
  local salt; salt=$(head -1 "$d/enc.log")
  local pre="PRAGMA key=\"$key\"; $pragmas PRAGMA cipher_page_size=$ps;"
  case "$opts" in *--plaintext-header*) pre="$pre PRAGMA cipher_salt=\"x'$salt'\";";; esac
  local ic; ic=$($SQLCIPHER "$d/enc.db" "$pre
PRAGMA integrity_check;" 2>&1 | tail -1)
  [ "$ic" = ok ] || { bad "$name shell integrity_check: $ic"; return; }
  $SQLCIPHER "$d/enc.db" "$pre
ATTACH DATABASE '$d/exp.db' AS p KEY '';
SELECT sqlcipher_export('p');" > "$d/export.log" 2>&1 || { bad "$name shell export: $(head -c 200 "$d/export.log")"; return; }
  $SQLITE3 "$d/exp.db" .dump > "$d/enc.dump" 2>&1
  cmp -s "$d/enc.dump" "$d/src.dump" || { bad "$name dump differs"; return; }
  ok
  rm -rf "$d"
}

mode=${1:-all}
H4="PRAGMA kdf_iter=256000;"
RAW="x'2DD29CA851E7B56E4697B0E1F08507293D761A05CE4D1B628663F411A8086D99'"
RAWS="x'98483C6EB40B6C31A448C22A66DED3B5E5E8D5119CAC8327B655C8B5C483648101010101010101010101010101010101'"
SALT="x'01010101010101010101010101010101'"
if [ "$mode" = all ] || [ "$mode" = read ]; then
  for ps in 1024 2048 4096 8192 16384 32768 65536; do
    read_case "v4-p$ps" "passphrase$ps" "PRAGMA cipher_page_size=$ps;" "--compat 4 --page-size $ps"
  done
  for ps in 1024 4096 8192; do
    read_case "v3-p$ps" "passphrase3$ps" "PRAGMA cipher_compatibility=3; PRAGMA cipher_page_size=$ps;" "--compat 3 --page-size $ps"
  done
  read_case "v4-unicode" "pässwörd✓" "" "--compat 4"
  read_case "v4-rawkey" "$RAW" "PRAGMA cipher_page_size=4096;" "--compat 4"
  read_case "v4-rawkey-salt" "$RAWS" "PRAGMA cipher_page_size=4096;" "--compat 4"
  read_case "v3-rawkey" "$RAW" "PRAGMA cipher_compatibility=3;" "--compat 3"
  read_case "v4-iter1000" "pw" "PRAGMA kdf_iter=1000;" "--compat 4 --kdf-iter 1000"
  read_case "mix-sha256-sha1" "pw" "PRAGMA kdf_iter=2000; PRAGMA cipher_kdf_algorithm=PBKDF2_HMAC_SHA256; PRAGMA cipher_hmac_algorithm=HMAC_SHA1;" "--compat 4 --kdf-iter 2000 --kdf sha256 --hmac sha1"
  read_case "mix-sha1-sha512" "pw" "PRAGMA kdf_iter=2000; PRAGMA cipher_kdf_algorithm=PBKDF2_HMAC_SHA1; PRAGMA cipher_hmac_algorithm=HMAC_SHA512;" "--compat 4 --kdf-iter 2000 --kdf sha1 --hmac sha512"
  read_case "mix-sha256-sha256" "pw" "PRAGMA kdf_iter=2000; PRAGMA cipher_kdf_algorithm=PBKDF2_HMAC_SHA256; PRAGMA cipher_hmac_algorithm=HMAC_SHA256;" "--compat 4 --kdf-iter 2000 --kdf sha256 --hmac sha256"
  read_case "v4-ptheader32" "pw" "PRAGMA kdf_iter=2000; PRAGMA cipher_plaintext_header_size=32; PRAGMA cipher_salt=\"$SALT\";" "--compat 4 --kdf-iter 2000 --plaintext-header 32 --salt 01010101010101010101010101010101"
  read_case "v4-ptheader48-p1024" "pw" "PRAGMA kdf_iter=2000; PRAGMA cipher_page_size=1024; PRAGMA cipher_plaintext_header_size=48; PRAGMA cipher_salt=\"$SALT\";" "--compat 4 --kdf-iter 2000 --page-size 1024 --plaintext-header 48 --salt 01010101010101010101010101010101"
  read_case "v4-ptheader32-rawsalt" "$RAWS" "PRAGMA cipher_plaintext_header_size=32;" "--compat 4 --plaintext-header 32"
fi
if [ "$mode" = all ] || [ "$mode" = write ]; then
  [ -n "${CORPUS:-}" ] || { note "write: no CORPUS"; }
  n=0
  for f in ${CORPUS:+$CORPUS}/*.db ${CORPUS:+$CORPUS}/*.sqlite; do
    [ -f "$f" ] || continue
    [ "$(wc -c < "$f" | tr -d " ")" -le 30000000 ] || continue
    n=$((n+1)); [ "$n" -le "${MAXFILES:-1000}" ] || break
    b=$(basename "$f"); b=${b%.*}
    write_case "$b-v4" "writepw" "" "--compat 4" 80 x "$f"
    write_case "$b-v3" "writepw" "PRAGMA cipher_compatibility=3;" "--compat 3" 80 x "$f"
    write_case "$b-v3-r48" "writepw" "PRAGMA cipher_compatibility=3;" "--compat 3" 48 x "$f"
    write_case "$b-raw" "$RAW" "" "--compat 4" 80 x "$f"
    write_case "$b-pth" "writepw" "PRAGMA cipher_plaintext_header_size=32;" "--compat 4 --plaintext-header 32" 80 x "$f"
  done
fi
note "pass=$pass fail=$fail skip=$skip"
[ "$fail" = 0 ]
