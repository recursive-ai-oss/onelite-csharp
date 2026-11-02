-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: select,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT name, pagetype FROM dbstat('main') ORDER BY name, pageno
-- detail: 
-- setup
CREATE TABLE zz(y);
-- go
CREATE TABLE a(x);
-- go
CREATE INDEX b ON a(x);
-- go
INSERT INTO a VALUES(1),(2);
-- go
-- run
SELECT name, pagetype FROM dbstat('main') ORDER BY name, pageno;
