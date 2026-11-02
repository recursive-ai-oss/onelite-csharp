-- OneLite differential repro
-- status: pass
-- class: bug:r71-b10cc1549e
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, max(g0) OVER (PARTITION BY g0 ORDER BY c4 DESC, c1, c3 DESC ROWS BETWEEN CURRENT ROW AND 3 FOLLOWING EXCLUDE NO OTHERS), cume_dist() OVER (ORDER BY id DESC), rank() OVER (ORDER BY c4) FROM t0 ORDER BY id
-- detail: row 0: ours (3, 0, 1r, 2); sqlite3 (3, 0r, 1r, 2)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 CHAR(3) COLLATE NOCASE, c2 FLOAT, c3 DECIMAL(5,2), c4 BLOB, g0 GENERATED ALWAYS AS (c2 * 2) VIRTUAL);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(3, '00', 'A', -0.0, 2, 'NULL');
-- go
INSERT OR REPLACE INTO t0(id, c0, c1, c2, c3, c4) VALUES(218, 2147483647, '2024-02-29', 'abc', '3.0', '2024-02-29 12:30:45');
-- go
-- run
SELECT id, max(g0) OVER (PARTITION BY g0 ORDER BY c4 DESC, c1, c3 DESC ROWS BETWEEN CURRENT ROW AND 3 FOLLOWING EXCLUDE NO OTHERS), cume_dist() OVER (ORDER BY id DESC), rank() OVER (ORDER BY c4) FROM t0 ORDER BY id;
