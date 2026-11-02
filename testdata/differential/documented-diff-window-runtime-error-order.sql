-- OneLite differential repro
-- status: expected-fail
-- class: runtime-error-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT c2, sum(c0), sum(sum(c0)) OVER (ORDER BY c2 DESC ROWS BETWEEN 0.5 PRECEDING AND '1' PRECEDING) FROM t2 GROUP BY c2 ORDER BY c2
-- detail: error differs: ours run error (1): integer overflow; sqlite3 run error (1): frame starting offset must be a non-negative integer
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT, c1 VARCHAR(10) UNIQUE, c2 VARCHAR(10) COLLATE NOCASE);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(39, 9223372036854775807, 'ABC', NULL);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(84, NULL, '[1,2,3]', 'it''s');
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(96, 2147483647, '1700000000', NULL);
-- go
-- run
SELECT c2, sum(c0), sum(sum(c0)) OVER (ORDER BY c2 DESC ROWS BETWEEN 0.5 PRECEDING AND '1' PRECEDING) FROM t2 GROUP BY c2 ORDER BY c2;
