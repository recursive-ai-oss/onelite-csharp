-- OneLite differential repro
-- status: pass
-- class: ddl-trigger-new-affinity
-- engine: ours
-- families: ddl,trigger
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT v, typeof(v) FROM lg ORDER BY id
-- setup
CREATE TABLE t(a NUMERIC, b REAL, c INT, d TEXT);
CREATE TABLE lg(id INTEGER PRIMARY KEY, v);
CREATE TRIGGER tb BEFORE INSERT ON t BEGIN INSERT INTO lg(v) VALUES(new.a); INSERT INTO lg(v) VALUES(new.b); INSERT INTO lg(v) VALUES(new.c); INSERT INTO lg(v) VALUES(new.d); END;
CREATE TRIGGER tu BEFORE UPDATE ON t BEGIN INSERT INTO lg(v) VALUES(new.a); INSERT INTO lg(v) VALUES(new.b); END;
-- go
INSERT INTO t VALUES(0.0, 3, '5', 7);
-- go
UPDATE t SET a = 1.0, b = 2;
-- go
-- run
SELECT v, typeof(v) FROM lg ORDER BY id;
