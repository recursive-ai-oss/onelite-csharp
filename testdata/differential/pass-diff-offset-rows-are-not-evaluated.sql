-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: select abs(v) from t limit 5 offset 1
-- detail: 
-- setup
create table t(id integer primary key, v)
-- go
insert into t values(1,-9223372036854775808),(2,5),(3,6)
-- go
-- run
select abs(v) from t limit 5 offset 1;
