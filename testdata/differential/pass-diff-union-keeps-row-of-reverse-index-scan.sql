-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT c1 FROM t0 UNION SELECT 1
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 BLOB);
-- go
CREATE INDEX i0 ON t0(c1 DESC);
-- go
INSERT INTO t0(id, c0, c1) VALUES(45, 'abd', 0);
-- go
INSERT INTO t0(id, c0, c1) VALUES(47, '+7', 0.0);
-- go
INSERT INTO t0(id, c0, c1) VALUES(48, 'x', 5);
-- go
INSERT INTO t0(id, c0, c1) VALUES(49, 'y', 5.0);
-- go
-- run
SELECT c1 FROM t0 UNION SELECT 1;
