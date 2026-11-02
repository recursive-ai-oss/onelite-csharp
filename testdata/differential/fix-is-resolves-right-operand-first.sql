-- OneLite differential repro
-- status: pass
-- class: fixed:is-resolves-right-operand-first
-- engine: ours
-- families: affinity,datetime,expr,json,like,select
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (c1 IS c0) FROM v0
-- detail: error differs: ours parse error (1): no such column: c1; sqlite3 parse error (1): no such column: c0
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 FLOAT, c1 INTEGER);
-- go
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t0;
-- go
ALTER TABLE t0 RENAME COLUMN c0 TO c0r;
-- go
ALTER TABLE t0 RENAME COLUMN c1 TO c1r;
-- go
-- run
SELECT (c1 IS c0) FROM v0;
