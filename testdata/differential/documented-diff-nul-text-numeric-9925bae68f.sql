-- OneLite differential repro
-- status: pass
-- class: bug:nul-text-numeric
-- engine: ours
-- families: affinity,cast,expr,like,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (CAST(0 AS BOOLEAN) BETWEEN NULL AND (c2 || c0)) FROM t1
-- detail: row 0: ours (NULL); sqlite3 (0)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0, c1 VARCHAR(10), c2 DECIMAL(5,2), c3 UNSIGNED BIG INT, c4 UNSIGNED BIG INT) WITHOUT ROWID;
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(67, x'00000000', 'abc', '-5', 7, 24);
-- go
-- run
SELECT (CAST(0 AS BOOLEAN) BETWEEN NULL AND (c2 || c0)) FROM t1;
