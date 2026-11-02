-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: ddl,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT a, b, c, d FROM t0
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY);
-- go
INSERT INTO t0(id) VALUES(1);
-- go
ALTER TABLE t0 ADD COLUMN a DATETIME DEFAULT -9223372036854775808;
-- go
ALTER TABLE t0 ADD COLUMN b TEXT DEFAULT -9223372036854775808;
-- go
ALTER TABLE t0 ADD COLUMN c INTEGER DEFAULT - 9223372036854775808;
-- go
ALTER TABLE t0 ADD COLUMN d REAL DEFAULT -9223372036854775808;
-- go
-- run
SELECT a, b, c, d FROM t0;
