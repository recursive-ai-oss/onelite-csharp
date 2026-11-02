-- OneLite differential repro
-- status: pass
-- class: fixed:pragma-table-xinfo-strict-rowid-alias-notnull
-- engine: ours
-- families: expr,pragma
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: PRAGMA table_xinfo(t0)
-- detail: row 0: ours (0, 'id', 'INTEGER', 1, NULL, 1, 0); sqlite3 (0, 'id', 'INTEGER', 0, NULL, 1, 0)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 ANY, c1 INT) STRICT;
-- go
-- run
PRAGMA table_xinfo(t0);
