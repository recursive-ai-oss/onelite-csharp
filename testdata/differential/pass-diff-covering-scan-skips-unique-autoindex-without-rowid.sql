-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: select id, c1 from t0
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 CHAR(3), c1 UNIQUE, c2 DECIMAL(5,2)) WITHOUT ROWID
-- go
INSERT INTO t0(id,c1) VALUES(4,9),(48,1),(90,5)
-- go
-- run
select id, c1 from t0;
