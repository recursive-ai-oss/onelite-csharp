-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id FROM t2 UNION ALL SELECT id FROM t2 WHERE c0 > 1
-- detail: 
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 TEXT COLLATE RTRIM, c1 TEXT COLLATE RTRIM, c2 FLOAT) WITHOUT ROWID;
-- go
CREATE INDEX i0 ON t2(c0);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(80, '_x', '0x10', 1e15);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(81, ' a', '_x', NULL);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(79, 'zz', 'q', 1.5);
-- go
-- run
SELECT id FROM t2 UNION ALL SELECT id FROM t2 WHERE c0 > 1;
