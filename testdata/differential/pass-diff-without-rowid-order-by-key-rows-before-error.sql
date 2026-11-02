-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT NULL FROM t1 WHERE ((0 BETWEEN abs(c1) AND NULL) < 1) ORDER BY id
-- detail: 
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c1 DECIMAL(5,2)) WITHOUT ROWID;
-- go
INSERT INTO t1(id, c1) VALUES(4, 9223372036854775808);
-- go
INSERT INTO t1(id, c1) VALUES(8, -9223372036854775808);
-- go
-- run
SELECT NULL FROM t1 WHERE ((0 BETWEEN abs(c1) AND NULL) < 1) ORDER BY id;
