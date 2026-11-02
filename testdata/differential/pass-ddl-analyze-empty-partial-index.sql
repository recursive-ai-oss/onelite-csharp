-- OneLite differential repro
-- status: pass
-- class: ddl-analyze-empty-partial-index
-- engine: ours
-- families: ddl,trigger
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT tbl, idx, stat FROM sqlite_stat1 ORDER BY 1, 2
-- setup
CREATE TABLE t(a, b UNIQUE);
CREATE INDEX i ON t(a, b) WHERE a > 1;
CREATE INDEX j ON t(a);
-- go
ANALYZE;
-- go
-- run
SELECT tbl, idx, stat FROM sqlite_stat1 ORDER BY 1, 2;
