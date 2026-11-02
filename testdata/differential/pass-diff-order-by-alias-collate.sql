-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT a || '' AS x, c FROM t ORDER BY x COLLATE NOCASE, c
-- detail: 
-- setup
CREATE TABLE t(a TEXT, b TEXT COLLATE NOCASE, c);
-- go
INSERT INTO t VALUES('b','B',1),('A','a',2),('a','A',3),('B','b',4);
-- go
-- run
SELECT a || '' AS x, c FROM t ORDER BY x COLLATE NOCASE, c;
