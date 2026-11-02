-- OneLite differential repro
-- status: pass
-- class: ddl:drop-view-triggers
-- engine: ours
-- families: view,trigger
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT name FROM sqlite_master ORDER BY name
-- setup
CREATE TABLE lg(id INTEGER PRIMARY KEY, v);
CREATE TABLE t0(id INTEGER PRIMARY KEY, c1);
CREATE VIEW v0 AS SELECT id, c1 FROM t0;
CREATE TRIGGER vi0 INSTEAD OF INSERT ON v0 BEGIN INSERT INTO lg(v) VALUES(new.c1); END;
-- go
DROP VIEW v0;
-- run
SELECT name FROM sqlite_master ORDER BY name;
