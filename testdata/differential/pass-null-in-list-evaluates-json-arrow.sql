-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,json
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id, NULL IN (c4 ->> 'bad', 1) FROM t0
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c4);
-- go
INSERT INTO t0 VALUES(45, x'00');
-- go
INSERT INTO t0 VALUES(46, 'abc');
-- go
INSERT INTO t0 VALUES(47, '{"a":1}');
-- go
-- run
SELECT id, NULL IN (c4 ->> 'bad', 1) FROM t0;
