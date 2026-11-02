-- OneLite differential repro
-- status: expected-fail
-- class: window-stage-tie-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, row_number() OVER (ORDER BY c1 DESC NULLS LAST), count(c2) OVER (PARTITION BY c0 ORDER BY id GROUPS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), lag(c3) OVER (ORDER BY c1 DESC RANGE BETWEEN CURRENT ROW AND CURRENT ROW), c2 FROM t0 ORDER BY id
-- detail: row 0: ours (19, 2, 1, NULL, 10); sqlite3 (19, 2, 1, 2460000.5r, 10)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INT, c1 VARCHAR(10), c2, c3 NUMERIC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(19, 48, 'a', 10, ('a' || 'b' || char(0) || 'c' || 'd'));
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(64, 1, 'a', 'a%b', '2460000.5');
-- go
-- run
SELECT id, row_number() OVER (ORDER BY c1 DESC NULLS LAST), count(c2) OVER (PARTITION BY c0 ORDER BY id GROUPS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), lag(c3) OVER (ORDER BY c1 DESC RANGE BETWEEN CURRENT ROW AND CURRENT ROW), c2 FROM t0 ORDER BY id;
