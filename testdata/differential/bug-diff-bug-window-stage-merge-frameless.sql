-- OneLite differential repro
-- status: pass
-- class: bug:window-stage-merge-frameless
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, row_number() OVER (ROWS BETWEEN 1 FOLLOWING AND 1 FOLLOWING), percent_rank() OVER (PARTITION BY c3, id ORDER BY c3), lag(c2) OVER () FROM t0 ORDER BY id
-- detail: row 0: ours (6, 2, 0r, NULL); sqlite3 (6, 2, 0r, 'Hello World')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DATETIME, c1 DOUBLE, c2 BLOB, c3 VARCHAR(10) COLLATE NOCASE);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(6, 'true', 1e300, '{"a":1,"b":[1,2,3],"c":{"d":null}}', 'ABC');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(7, ' a', 123456789012345.6, 'Hello World', '0');
-- go
-- run
SELECT id, row_number() OVER (ROWS BETWEEN 1 FOLLOWING AND 1 FOLLOWING), percent_rank() OVER (PARTITION BY c3, id ORDER BY c3), lag(c2) OVER () FROM t0 ORDER BY id;
