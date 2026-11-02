-- OneLite differential repro
-- status: pass
-- class: bug:r71-fa3678f0cf
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(c0) OVER (), group_concat(c0) OVER (ORDER BY id RANGE BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING), min(c0) OVER (ROWS BETWEEN 3 PRECEDING AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS) FROM t1 ORDER BY id
-- detail: row 0: ours (79, '0,0.0', '0,0.0', -0r); sqlite3 (79, '0,0.0', '0,0.0', 0)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0, c1 TEXT, c2 FLOAT);
-- go
INSERT INTO t1(id, c0, c1, c2) VALUES(79, 0, '+7', 67.875);
-- go
INSERT INTO t1(id, c0, c1, c2) VALUES(129, -0.0, '00', 1e15);
-- go
-- run
SELECT id, group_concat(c0) OVER (), group_concat(c0) OVER (ORDER BY id RANGE BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING), min(c0) OVER (ROWS BETWEEN 3 PRECEDING AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS) FROM t1 ORDER BY id;
