-- OneLite differential repro
-- status: expected-fail
-- class: window-stage-tie-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT * FROM (SELECT id, dense_rank() OVER (ORDER BY c2), lag(c2, 2, c2) OVER (ORDER BY c1 DESC), dense_rank() OVER (ORDER BY c2 NULLS FIRST RANGE BETWEEN 0.5 FOLLOWING AND 0 FOLLOWING) FROM t0 ORDER BY id) WHERE id % 2 = 0 ORDER BY id
-- detail: row 1: ours (14, 1, 1000000000000000r, 1); sqlite3 (14, 1, '2024-02-29 12:30:45', 1)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 VARCHAR(10), c1 DECIMAL(5,2), c2 DOUBLE, c3 BOOLEAN, c4);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(8, '12:30:45', 9223372036854775807, '2024-02-29 12:30:45', -9223372036854775809, 'hello world');
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(14, '"s"', 2147483647, 123456789012345.6, 10, x'00000000');
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(16, 'é', 9223372036854775807, 1e15, -9223372036854775808, 'ABC');
-- go
-- run
SELECT * FROM (SELECT id, dense_rank() OVER (ORDER BY c2), lag(c2, 2, c2) OVER (ORDER BY c1 DESC), dense_rank() OVER (ORDER BY c2 NULLS FIRST RANGE BETWEEN 0.5 FOLLOWING AND 0 FOLLOWING) FROM t0 ORDER BY id) WHERE id % 2 = 0 ORDER BY id;
