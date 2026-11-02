-- OneLite differential repro
-- status: pass
-- class: fixed:pragma-table-info-view-missing-table
-- engine: ours
-- families: expr,pragma
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: PRAGMA table_info(v0)
-- detail: ours: 0 rows; sqlite3: parse error (1): no such table: main.t1
-- setup
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t1;
-- go
-- run
PRAGMA table_info(v0);
