-- OneLite differential repro
-- status: pass
-- class: fixed:pragma-table-xinfo-view-missing-table
-- engine: ours
-- families: expr,pragma
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: PRAGMA table_xinfo(v0)
-- detail: ours: 0 rows; sqlite3: parse error (1): no such table: main.t2
-- setup
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t2;
-- go
-- run
PRAGMA table_xinfo(v0);
