-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: select,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT t.name, s.pageno FROM (SELECT name FROM sqlite_schema WHERE type = 'table') AS t JOIN dbstat AS s ON s.name = t.name ORDER BY t.name
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
SELECT t.name, s.pageno FROM (SELECT name FROM sqlite_schema WHERE type = 'table') AS t JOIN dbstat AS s ON s.name = t.name ORDER BY t.name;
