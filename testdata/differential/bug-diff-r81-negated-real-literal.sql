-- OneLite differential repro
-- status: pass
-- class: bug:r81-negated-real-literal
-- engine: ours
-- families: collation,expr,math,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT atan2(0, (- c2)) FROM t0
-- detail: row 0: ours (3.141592653589793r); sqlite3 (0r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DOUBLE, c1 TEXT, c2 FLOAT) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(4, NULL, 65536, 0.0);
-- go
-- run
SELECT atan2(0, (- c2)) FROM t0;
