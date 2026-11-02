-- OneLite differential repro
-- status: pass
-- class: bug:added-column-default-real-text
-- engine: ours
-- families: ddl,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT x3 FROM t0
-- detail: row 0: ours ('1.0e+300'); sqlite3 ('1e300')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 REAL);
-- go
INSERT INTO t0(id, c0) VALUES(2, 3.5);
-- go
ALTER TABLE t0 ADD COLUMN x3 CHAR(3) DEFAULT 1e300;
-- go
-- run
SELECT x3 FROM t0;
