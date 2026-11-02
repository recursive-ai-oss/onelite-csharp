-- OneLite differential repro
-- status: pass
-- class: bug:window-nulls-first-lead-ties
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(id, '|') FILTER (WHERE c1 IS NOT NULL) OVER (PARTITION BY id ORDER BY id ROWS BETWEEN CURRENT ROW AND CURRENT ROW), lead(id, 1.5, 'd') OVER (), lead(c2) OVER (ORDER BY c2 DESC NULLS FIRST) FROM t2 ORDER BY id
-- detail: row 0: ours (41, '41', 'd', -1E+300r); sqlite3 (41, '41', 'd', NULL)
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 DATETIME, c1 NUMERIC, c2 FLOAT);
-- go
CREATE INDEX i0 ON t2(c2);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(41, 'a', 2147483647, -1e300);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(42, -1e300, 'xyz', -1e300);
-- go
-- run
SELECT id, group_concat(id, '|') FILTER (WHERE c1 IS NOT NULL) OVER (PARTITION BY id ORDER BY id ROWS BETWEEN CURRENT ROW AND CURRENT ROW), lead(id, 1.5, 'd') OVER (), lead(c2) OVER (ORDER BY c2 DESC NULLS FIRST) FROM t2 ORDER BY id;
