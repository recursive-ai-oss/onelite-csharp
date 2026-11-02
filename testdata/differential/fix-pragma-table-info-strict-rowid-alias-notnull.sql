-- OneLite differential repro
-- status: pass
-- class: fixed:pragma-table-info-strict-rowid-alias-notnull
-- engine: ours
-- families: expr,pragma
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: PRAGMA table_info(t1)
-- detail: row 0: ours (0, 'id', 'INTEGER', 1, NULL, 1); sqlite3 (0, 'id', 'INTEGER', 0, NULL, 1)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 REAL, c1 INTEGER, c2 REAL, c3 BLOB) STRICT;
-- go
-- run
PRAGMA table_info(t1);
