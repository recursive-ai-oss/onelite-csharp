-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT 9 IN (SELECT c FROM t), 9 NOT IN (SELECT c FROM t), 1 IN (SELECT c FROM t), 9 IN (SELECT c FROM t WHERE id > 0)
-- detail: 
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, c, d);
-- go
INSERT INTO t VALUES(1, 1, 1), (2, NULL, 2), (3, 3, 3);
-- go
CREATE INDEX i ON t(c);
-- go
CREATE INDEX j ON t(c DESC, d);
-- go
-- run
SELECT 9 IN (SELECT c FROM t), 9 NOT IN (SELECT c FROM t), 1 IN (SELECT c FROM t), 9 IN (SELECT c FROM t WHERE id > 0);
