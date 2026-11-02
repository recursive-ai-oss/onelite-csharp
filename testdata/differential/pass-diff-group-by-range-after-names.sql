-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT max(round((c0 IS TRUE))) FROM v0 GROUP BY 0
-- detail: 
-- setup
create table t0(id integer primary key, a)
-- go
create view v0 as select id, a as x from t0
-- go
-- run
SELECT max(round((c0 IS TRUE))) FROM v0 GROUP BY 0;
