-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: ddl,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT x5 FROM t2
-- detail: 
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c1 DOUBLE);
-- go
INSERT INTO t2(id, c1) VALUES(57, 1e18);
-- go
ALTER TABLE t2 ADD COLUMN x5 BOOLEAN DEFAULT -9223372036854775809;
-- go
-- run
SELECT x5 FROM t2;
