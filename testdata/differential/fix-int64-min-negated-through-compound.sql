-- OneLite differential repro
-- status: pass
-- class: fixed:int64-min-negated-through-compound
-- engine: ours
-- families: affinity,compound,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT c0, (NULL IS NOT id) FROM t1 EXCEPT SELECT NULL, NULL FROM t2
-- detail: row 0: ours (-9.223372036854776E+18r, 1); sqlite3 (-9223372036854775808, 1)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 DECIMAL(5,2), c1);
-- go
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0, c1 UNSIGNED BIG INT, c2 VARCHAR(10) COLLATE BINARY, c3 FLOAT);
-- go
CREATE INDEX i1 ON t1(c0 DESC);
-- go
INSERT INTO t1(id, c0, c1) VALUES(47, -9223372036854775809, ('a' || 'b' || char(0) || 'c' || 'd'));
-- go
INSERT INTO t1(id, c0, c1) VALUES(124, -9223372036854775808, '');
-- go
-- run
SELECT c0, (NULL IS NOT id) FROM t1 EXCEPT SELECT NULL, NULL FROM t2;
