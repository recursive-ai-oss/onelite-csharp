-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT total(c1), sum(c1), avg(c1) FROM t2
-- detail: 
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c1 DECIMAL(5,2));
-- go
INSERT INTO t2(id, c1) VALUES(3, 9223372036854775807);
-- go
INSERT INTO t2(id, c1) VALUES(46, -9223372036854775809);
-- go
INSERT INTO t2(id, c1) VALUES(90, 4.35);
-- go
-- run
SELECT total(c1), sum(c1), avg(c1) FROM t2;
