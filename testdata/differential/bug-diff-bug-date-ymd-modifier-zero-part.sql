-- OneLite differential repro
-- status: pass
-- class: bug:date-ymd-modifier-zero-part
-- engine: ours
-- families: cast,datetime,expr,view
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT datetime('0000-01-01', '-0001-00-00', 'start of year') FROM t2
-- detail: row 0: ours (NULL); sqlite3 ('-0001-01-01 00:00:00')
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 REAL, c1 FLOAT);
-- go
INSERT INTO t2(id, c0, c1) VALUES(40, -1e300, 80.5);
-- go
-- run
SELECT datetime('0000-01-01', '-0001-00-00', 'start of year') FROM t2;
