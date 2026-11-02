-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, row_number() OVER () FROM t0
-- detail: row 0: ours (66, 1); sqlite3 (37, 1)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 CHAR(3) COLLATE RTRIM, c1 DECIMAL(5,2))
-- go
CREATE INDEX i0 ON t0(c1 COLLATE RTRIM)
-- go
CREATE INDEX i1 ON t0(c0)
-- go
INSERT INTO t0 VALUES(37,'2023-12-31T23:59:59','2024-02-29')
-- go
INSERT INTO t0 VALUES(66,'9',-9223372036854775809)
-- go
-- run
SELECT id, row_number() OVER () FROM t0;
