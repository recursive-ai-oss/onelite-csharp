-- OneLite differential repro
-- status: expected-fail
-- class: planner-index-choice
-- engine: ours
-- families: aggregate,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT json_group_array(id) FROM v0 WHERE (c1 >= 0)
-- detail: row 0: ours ('[10,212]'); sqlite3 ('[212,10]')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DOUBLE, c1 DECIMAL(5,2));
-- go
CREATE INDEX i2 ON t0(c1) WHERE c1 IS NOT NULL;
-- go
INSERT INTO t0(id, c0, c1) VALUES(10, 2.5, 'it''s');
-- go
INSERT INTO t0(id, c0, c1) VALUES(212, '1700000000', 100.0);
-- go
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t0;
-- go
-- run
SELECT json_group_array(id) FROM v0 WHERE (c1 >= 0);
