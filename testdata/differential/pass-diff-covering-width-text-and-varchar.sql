-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: select id, count(*) from t
-- detail: 
-- setup
CREATE TABLE t(id integer primary key, a TEXT, b VARCHAR(10))
-- go
CREATE INDEX ib ON t(b)
-- go
CREATE INDEX ia ON t(a)
-- go
INSERT INTO t VALUES(1,'a','z'),(2,'z','a')
-- go
-- run
select id, count(*) from t;
