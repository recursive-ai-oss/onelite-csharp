-- OneLite differential repro
-- status: pass
-- class: bug:window-two-windows-index-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, first_value(c2) OVER (PARTITION BY c2, c0  GROUPS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING), row_number() OVER (ORDER BY c0 DESC, c1), row_number() OVER (ORDER BY c0 DESC) FROM t0 ORDER BY id
-- detail: row 0: ours (10, 10, 2, 1); sqlite3 (10, 10, 2, 2)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INTEGER UNIQUE, c1 DECIMAL(5,2), c2 DATETIME);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(10, NULL, 100.0, 10);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(11, NULL, 7, '2024-02-29 12:30:45');
-- go
-- run
SELECT id, first_value(c2) OVER (PARTITION BY c2, c0  GROUPS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING), row_number() OVER (ORDER BY c0 DESC, c1), row_number() OVER (ORDER BY c0 DESC) FROM t0 ORDER BY id;
