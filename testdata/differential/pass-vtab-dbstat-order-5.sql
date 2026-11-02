-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: select,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT name, count(*) FROM dbstat GROUP BY name ORDER BY name
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
SELECT name, count(*) FROM dbstat GROUP BY name ORDER BY name;
