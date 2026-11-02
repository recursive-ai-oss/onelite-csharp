-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, total(c3) FILTER (WHERE x5 IS NOT NULL) OVER (ORDER BY id COLLATE NOCASE DESC GROUPS UNBOUNDED PRECEDING), total(c1) OVER (ORDER BY x5 ROWS BETWEEN CURRENT ROW AND 3 FOLLOWING) FROM t0 ORDER BY id
-- detail: row 1: ours (33, 0r, 4.35r); sqlite3 (33, 0r, 4.349999999998545r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 DATETIME, c2 REAL, c3 NUMERIC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(5, NULL, 9223372036854775807, 65536, 0);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(33, 'é', 4.35, 123456789012345.6, 1.5);
-- go
ALTER TABLE t0 ADD COLUMN x5 TEXT;
-- go
-- run
SELECT id, total(c3) FILTER (WHERE x5 IS NOT NULL) OVER (ORDER BY id COLLATE NOCASE DESC GROUPS UNBOUNDED PRECEDING), total(c1) OVER (ORDER BY x5 ROWS BETWEEN CURRENT ROW AND 3 FOLLOWING) FROM t0 ORDER BY id;
