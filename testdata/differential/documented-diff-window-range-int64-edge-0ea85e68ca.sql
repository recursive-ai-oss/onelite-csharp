-- OneLite differential repro
-- status: expected-fail
-- class: window-range-int64-edge
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id, last_value(c0) OVER (PARTITION BY c0 ORDER BY c0 RANGE BETWEEN UNBOUNDED PRECEDING AND 3 PRECEDING), cume_dist() OVER (ORDER BY c0 DESC) FROM t1
-- detail: row 0: ours (109, -9.223372036854776E+18r, 1r); sqlite3 (109, -9223372036854775808, 1r)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 NUMERIC, c1 CHAR(3));
-- go
INSERT INTO t1(id, c0, c1) VALUES(94, -9223372036854775809, NULL);
-- go
INSERT INTO t1(id, c0, c1) VALUES(109, -9223372036854775808, 'hello world');
-- go
-- run
SELECT id, last_value(c0) OVER (PARTITION BY c0 ORDER BY c0 RANGE BETWEEN UNBOUNDED PRECEDING AND 3 PRECEDING), cume_dist() OVER (ORDER BY c0 DESC) FROM t1;
