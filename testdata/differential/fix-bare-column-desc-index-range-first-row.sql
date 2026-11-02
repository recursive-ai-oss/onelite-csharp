-- OneLite differential repro
-- status: pass
-- class: fixed:bare-column-desc-index-range-first-row
-- engine: ours
-- families: affinity,aggregate,expr
-- utf16: true
-- oracle: diff
-- ordered: false
-- query: SELECT min((id - (c0 = 2)), c1), sum(DISTINCT id) FROM t0 WHERE (c1 >= 0)
-- detail: row 0: ours (0, 10); sqlite3 (2, 10)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 NUMERIC, c1);
-- go
CREATE INDEX i0 ON t0(c1 DESC);
-- go
INSERT INTO t0(id, c0, c1) VALUES(3, -9223372036854775809, 0);
-- go
INSERT INTO t0(id, c0, c1) VALUES(7, 65536, 2);
-- go
-- run
SELECT min((id - (c0 = 2)), c1), sum(DISTINCT id) FROM t0 WHERE (c1 >= 0);
