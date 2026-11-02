-- OneLite differential repro
-- status: pass
-- class: bug:window-desc-index-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, first_value(c4) OVER w1 FROM t0 WINDOW w1 AS (PARTITION BY c2 ) ORDER BY id
-- detail: row 0: ours (3, 69.625r); sqlite3 (3, 21.375r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INT, c1 DECIMAL(5,2), c2 UNSIGNED BIG INT, c3 FLOAT, c4 DOUBLE);
-- go
CREATE INDEX i1 ON t0(c2 DESC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(3, 39, -1, 100, 10, 69.625);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(115, 9223372036854775807, 1e18, 100, 2.5, 21.375);
-- go
-- run
SELECT id, first_value(c4) OVER w1 FROM t0 WINDOW w1 AS (PARTITION BY c2 ) ORDER BY id;
