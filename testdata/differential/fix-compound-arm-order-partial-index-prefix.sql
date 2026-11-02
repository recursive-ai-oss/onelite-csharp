-- OneLite differential repro
-- status: pass
-- class: fixed:compound-arm-order-partial-index-prefix
-- engine: ours
-- families: compound,expr,math,string
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT c1, (c1 = NULL) FROM t0 EXCEPT SELECT NULL, NULL FROM t0
-- detail: row 0: ours ('ABC', NULL); sqlite3 ('abc', NULL)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 FLOAT, c1 VARCHAR(10) COLLATE NOCASE, c2 DATETIME);
-- go
CREATE INDEX i0 ON t0(c1 DESC);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(4, '2024-02-29 12:30:45', 'ABC', 3.14159);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(24, -9223372036854775809, 'abc', 9223372036854775808);
-- go
-- run
SELECT c1, (c1 = NULL) FROM t0 EXCEPT SELECT NULL, NULL FROM t0;
