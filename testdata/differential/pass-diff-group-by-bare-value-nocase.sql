-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT c1, count(*), row_number() OVER (ORDER BY c1) FROM t0 GROUP BY c1 ORDER BY c1
-- detail: row 0: ours ('a', 2, 1); sqlite3 ('A', 2, 1)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 CHAR(3) COLLATE NOCASE, c2 UNSIGNED BIG INT, c3 CHAR(3) UNIQUE, c4 DECIMAL(5,2));
-- go
CREATE INDEX i0 ON t0(c1 COLLATE RTRIM);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(47, '2024-02-29', 'a', 7, -2147483648, 10);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(48, NULL, 'A', -100, '2023-12-31T23:59:59', -0.0);
-- go
-- run
SELECT c1, count(*), row_number() OVER (ORDER BY c1) FROM t0 GROUP BY c1 ORDER BY c1;
