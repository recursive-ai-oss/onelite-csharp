-- OneLite differential repro
-- status: pass
-- class: timediff-range
-- engine: ours
-- families: datetime,expr,select,string
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT timediff('2023-12-31T23:59:59', '9999-12-31') FROM t2
-- detail: row 0: ours ('-7975-11-29 00:00:01.000'); sqlite3 ('-7975-11-30 00:00:01.000')
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 INTEGER, c1 REAL, c2 DECIMAL(5,2));
-- go
INSERT OR IGNORE INTO t2(id, c0, c1, c2) VALUES(122, 9223372036854775807, 1e-300, 0.0);
-- go
-- run
SELECT timediff('2023-12-31T23:59:59', '9999-12-31') FROM t2;
