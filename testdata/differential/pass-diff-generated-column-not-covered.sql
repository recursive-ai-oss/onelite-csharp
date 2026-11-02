-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: select g, count(*) from t
-- detail: 
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, b INTEGER, g AS (b*2) STORED)
-- go
CREATE INDEX ti ON t(g)
-- go
INSERT INTO t(id,b) VALUES(1,9),(2,1)
-- go
-- run
select g, count(*) from t;
