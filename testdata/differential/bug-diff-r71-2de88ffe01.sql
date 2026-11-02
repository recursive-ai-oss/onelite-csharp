-- OneLite differential repro
-- status: pass
-- class: bug:r71-2de88ffe01
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(c3, '|') FILTER (WHERE c3 IS NOT NULL) OVER (ROWS BETWEEN 1.0 PRECEDING AND 0 PRECEDING EXCLUDE NO OTHERS), ntile(3) OVER (ORDER BY id), first_value(c1) OVER (ORDER BY c0 COLLATE BINARY DESC NULLS FIRST, c0 DESC, c2 DESC GROUPS 1 PRECEDING), c0 FROM t1 ORDER BY id DESC LIMIT 3
-- detail: row 0: ours (146, '-100', 3, 0r, '日本'); sqlite3 (146, '|-100', 3, 0r, '日本')
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 VARCHAR(10), c1 FLOAT, c2 REAL, c3);
-- go
INSERT INTO t1(id, c0, c1, c2, c3) VALUES(144, -2147483648, 2, 1e15, x'');
-- go
INSERT INTO t1(id, c0, c1, c2, c3) VALUES(145, x'00', 0.0, 9.25, x'');
-- go
INSERT INTO t1(id, c0, c1, c2, c3) VALUES(146, '日本', 1e18, '0', -100);
-- go
-- run
SELECT id, group_concat(c3, '|') FILTER (WHERE c3 IS NOT NULL) OVER (ROWS BETWEEN 1.0 PRECEDING AND 0 PRECEDING EXCLUDE NO OTHERS), ntile(3) OVER (ORDER BY id), first_value(c1) OVER (ORDER BY c0 COLLATE BINARY DESC NULLS FIRST, c0 DESC, c2 DESC GROUPS 1 PRECEDING), c0 FROM t1 ORDER BY id DESC LIMIT 3;
