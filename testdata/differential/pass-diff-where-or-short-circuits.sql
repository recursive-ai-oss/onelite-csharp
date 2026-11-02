-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id FROM t WHERE id > 0 OR 0 > abs(-9223372036854775808)
-- detail: 
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, c2 VARCHAR(10))
-- go
INSERT INTO t VALUES(9,'10'),(10,NULL)
-- go
-- run
SELECT id FROM t WHERE id > 0 OR 0 > abs(-9223372036854775808);
