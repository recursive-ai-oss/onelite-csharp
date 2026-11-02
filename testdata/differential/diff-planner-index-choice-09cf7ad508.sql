-- OneLite differential repro
-- status: expected-fail
-- class: planner-index-choice
-- engine: ours
-- families: cast,compound,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT NULL FROM t2 UNION SELECT c1 FROM t2 WHERE c2
-- detail: row 0: ours (NULL); sqlite3 (-9223372036854775808)
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 REAL, c1 NUMERIC, c2 BLOB, c3 BLOB);
-- go
CREATE INDEX i2 ON t2(c2 DESC) WHERE c2 IS NOT NULL;
-- go
INSERT OR REPLACE INTO t2(id, c0, c1, c2, c3) VALUES(43, 90.25, -9223372036854775809, 2, '2024-02-29 12:30:45');
-- go
INSERT INTO t2(id, c0, c1, c2, c3) VALUES(76, -2147483648, -9223372036854775808, 9007199254740993, '0x10');
-- go
-- run
SELECT NULL FROM t2 UNION SELECT c1 FROM t2 WHERE c2;
