-- OneLite differential repro
-- status: pass
-- class: bug:r71-0637bbda1c
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT * FROM (SELECT id, group_concat(c0) OVER (ORDER BY id DESC ROWS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE NO OTHERS) FROM t2 WINDOW w1 AS (ORDER BY c0 COLLATE RTRIM DESC) ORDER BY id) WHERE id % 2 = 0 ORDER BY id
-- detail: row 0: ours (92, '2:30:45'); sqlite3 (92, '12:30:45')
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 CHAR(3), c1 DECIMAL(5,2));
-- go
INSERT INTO t2(id, c0, c1) VALUES(92, NULL, 0);
-- go
INSERT INTO t2(id, c0, c1) VALUES(93, '12:30:45', '2023-12-31T23:59:59');
-- go
INSERT INTO t2(id, c0, c1) VALUES(94, '', 2.5);
-- go
INSERT OR IGNORE INTO t2(id, c0, c1) VALUES(95, '9', '[1,2,3]');
-- go
-- run
SELECT * FROM (SELECT id, group_concat(c0) OVER (ORDER BY id DESC ROWS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE NO OTHERS) FROM t2 WINDOW w1 AS (ORDER BY c0 COLLATE RTRIM DESC) ORDER BY id) WHERE id % 2 = 0 ORDER BY id;
