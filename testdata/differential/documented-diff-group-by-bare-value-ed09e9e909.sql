-- OneLite differential repro
-- status: expected-fail
-- class: group-by-bare-value
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT c1, sum(c1), sum(sum(c1)) OVER (ORDER BY c1 ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t0 GROUP BY c1 ORDER BY c1
-- detail: row 0: ours ('a', 0r, 0r); sqlite3 ('a ', 0r, 0r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BOOLEAN, c1 CHAR(3) COLLATE RTRIM, c2 DATETIME, c3 CHAR(3) COLLATE BINARY UNIQUE);
-- go
CREATE INDEX i0 ON t0(c1 DESC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(158, '2023-12-31T23:59:59', 'a', '_x', '+7');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(165, ' 12 ', 'a ', '2024-02-29 12:30:45', '2023-12-31T23:59:59');
-- go
-- run
SELECT c1, sum(c1), sum(sum(c1)) OVER (ORDER BY c1 ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM t0 GROUP BY c1 ORDER BY c1;
