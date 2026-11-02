-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: join,subquery
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id FROM r ORDER BY maxy DESC, id LIMIT 2
-- detail: 
-- setup
CREATE VIRTUAL TABLE r USING rtree(id, minx, maxx, miny, maxy);
-- go
CREATE TABLE o(id INTEGER PRIMARY KEY, name TEXT, lim REAL);
-- go
INSERT INTO r VALUES(1,0,1,0,1),(2,2,3,2,3),(3,5,9,5,9),(4,0.5,6,0.5,6);
-- go
INSERT INTO o VALUES(1,'a',0.5),(2,'b',2.5),(3,'c',7),(4,'d',100);
-- go
-- run
SELECT id FROM r ORDER BY maxy DESC, id LIMIT 2;
