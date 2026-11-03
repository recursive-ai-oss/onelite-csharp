-- OneLite differential repro
-- status: expected-fail
-- class: planner-index-choice
-- engine: ours
-- families: aggregate,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT json_group_array(id) FROM v0 WHERE (id BETWEEN 0 AND '')
-- detail: row 0: ours ('[168,167]'); sqlite3 ('[167,168]')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DATETIME, c1 BOOLEAN, c2 DECIMAL(5,2), c3 TEXT, c4 DOUBLE);
-- go
CREATE INDEX i0 ON t0(c1);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(167, '2460000.5', 1e-300, 123456789012345.6, '"s"', 26);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(168, 1.5, 0, -1e300, '[1,2,3]', -0.5);
-- go
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t0;
-- go
-- run
SELECT json_group_array(id) FROM v0 WHERE (id BETWEEN 0 AND '');
