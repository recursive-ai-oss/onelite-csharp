-- OneLite differential repro
-- status: pass
-- class: bug:window-generated-column-index-unused-2
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, lead(g0, 2) OVER () FROM t0 ORDER BY id
-- detail: row 0: ours (7, NULL); sqlite3 (7, -4294967296r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DOUBLE, c1 INTEGER, c2, c3 TEXT, g0 GENERATED ALWAYS AS (c0 * 2) STORED);
-- go
CREATE INDEX i0 ON t0(g0 COLLATE RTRIM);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(7, 4.35, 4.35, x'616263', NULL);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(8, 1e-300, -16, 0.1, 'true');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(40, -2147483648, ' 12 ', 'Hello World', -30.75);
-- go
-- run
SELECT id, lead(g0, 2) OVER () FROM t0 ORDER BY id;
