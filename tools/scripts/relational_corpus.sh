#!/usr/bin/env bash
# Builds the small databases the relational differential run (querydiff fuzz --rel) draws on, with the pinned sqlite3 shell.
# Usage: relational_corpus.sh OUT_DIR [SQLITE3]
# Every value comes from arithmetic on the row number, so the files are the same on every run.
set -euo pipefail
OUT=${1:?usage: relational_corpus.sh OUT_DIR [SQLITE3]}
SQLITE3=${2:-${ONELITE_SQLITE3:-$HOME/onelite/oracles/sqlite-3.53.4/sqlite3}}
mkdir -p "$OUT"

make() { # name, page_size, encoding, rows
  local db="$OUT/$1.db"
  rm -f "$db"
  "$SQLITE3" "$db" <<SQL
PRAGMA page_size=$2;
PRAGMA encoding='$3';
CREATE TABLE t1(id INTEGER PRIMARY KEY, a INT, b INT, c TEXT, d REAL);
CREATE INDEX t1a ON t1(a);
CREATE INDEX t1bc ON t1(b, c);
CREATE TABLE t2(id INTEGER PRIMARY KEY, t1_id INT, v TEXT COLLATE NOCASE, w NUMERIC);
CREATE INDEX t2_t1 ON t2(t1_id);
CREATE INDEX t2_v ON t2(v);
CREATE TABLE t3(k TEXT PRIMARY KEY, n INT) WITHOUT ROWID;
CREATE TABLE t4(x, y, z);
CREATE TABLE t5(a INT, b INT);
WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<$4)
INSERT INTO t1 SELECT n, (n*7919)%101, CASE WHEN n%9=0 THEN NULL ELSE (n*31)%17 END,
  CASE WHEN n%13=0 THEN NULL ELSE char(97+(n%7))||(n%23) END, CASE WHEN n%10=0 THEN NULL ELSE n/7.0 END FROM s;
WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<$4*2)
INSERT INTO t2 SELECT n, CASE WHEN n%15=0 THEN NULL ELSE (n*13)%$4+1 END, CASE WHEN n%8=0 THEN NULL ELSE upper(char(97+(n%5)))||(n%11) END,
  CASE WHEN n%6=0 THEN NULL WHEN n%4=0 THEN (n%50)||'' ELSE n%50 END FROM s;
WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<300)
INSERT INTO t3 SELECT 'k'||(n*37%1000), n%19 FROM s GROUP BY 1;
WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<400)
INSERT INTO t4 SELECT CASE n%5 WHEN 0 THEN NULL WHEN 1 THEN n WHEN 2 THEN n/3.0 WHEN 3 THEN 'x'||n ELSE x'41'||hex(n) END,
  CASE n%4 WHEN 0 THEN n%10 WHEN 1 THEN 'y'||(n%10) WHEN 2 THEN NULL ELSE (n%10)+0.5 END, n%3 FROM s;
WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<25)
INSERT INTO t5 SELECT n%6, CASE WHEN n%7=0 THEN NULL ELSE n%4 END FROM s;
ANALYZE;
SQL
}

make rel-1024-utf8 1024 UTF-8 1500
make rel-4096-utf8 4096 UTF-8 1200
make rel-2048-utf16le 2048 UTF-16le 800
make rel-8192-utf16be 8192 UTF-16be 600
ls -l "$OUT"
