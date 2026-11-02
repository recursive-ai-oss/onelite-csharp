-- OneLite differential repro
-- status: pass
-- class: bug:quote-text-with-nul
-- engine: ours
-- families: expr,function,select
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT quote(c2) FROM t1
-- detail: row 0: ours (''abcd''); sqlite3 (''ab'')
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 REAL UNIQUE, c1 VARCHAR(10), c2, c3 NUMERIC, c4 REAL UNIQUE);
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(94, 1e15, 'hello world', ('a' || 'b' || char(0) || 'c' || 'd'), 9223372036854775807, 123456789012345.6);
-- go
-- run
SELECT quote(c2) FROM t1;
