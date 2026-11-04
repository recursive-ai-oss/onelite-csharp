-- OneLite differential repro
-- status: expected-fail
-- class: runtime-error-order
-- engine: ours
-- families: aggregate,expr,json
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT percentile_cont(c0, 0.5) FROM t1 WHERE (json_quote(c0) IS NOT NULL) GROUP BY NULL, id
-- detail: error differs: ours run error (1): JSON cannot hold BLOB values; sqlite3 run error (1): input to percentile_cont() is not numeric
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 CHAR(3), c1 NUMERIC);
-- go
INSERT INTO t1(id, c0, c1) VALUES(22, '1e1', NULL);
-- go
INSERT INTO t1(id, c0, c1) VALUES(23, x'ff00ab', 9223372036854775807);
-- go
-- run
SELECT percentile_cont(c0, 0.5) FROM t1 WHERE (json_quote(c0) IS NOT NULL) GROUP BY NULL, id;
