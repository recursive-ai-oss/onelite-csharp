-- OneLite differential repro
-- status: pass
-- class: bug:r71-e5fa000040
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT * FROM (SELECT id, lead(c1) OVER (ORDER BY c0), min(id) OVER (RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW), group_concat(c1) OVER (ORDER BY c0 DESC GROUPS BETWEEN CURRENT ROW AND 1 FOLLOWING EXCLUDE NO OTHERS) FROM t0 ORDER BY id) WHERE id % 2 = 0 ORDER BY id
-- detail: row 0: ours (20, 0, 20, '0'); sqlite3 (20, 0, 20, ',0')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INTEGER, c1 BOOLEAN, c2 CHAR(3));
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(20, 42, '', ('t' || 'a' || 'b' || char(9) || 'h' || 'e' || 'r' || 'e'));
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(21, 44, 0, '+7');
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(23, -100, '0', NULL);
-- go
-- run
SELECT * FROM (SELECT id, lead(c1) OVER (ORDER BY c0), min(id) OVER (RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW), group_concat(c1) OVER (ORDER BY c0 DESC GROUPS BETWEEN CURRENT ROW AND 1 FOLLOWING EXCLUDE NO OTHERS) FROM t0 ORDER BY id) WHERE id % 2 = 0 ORDER BY id;
