-- OneLite differential repro
-- status: expected-fail
-- class: runtime-error-order
-- engine: ours
-- families: aggregate,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT json_group_object(c2, c0), percentile(c2, 0.25) FROM t0 GROUP BY c2, id
-- detail: error differs: ours run error (1): JSON cannot hold BLOB values; sqlite3 run error (1): input to percentile() is not numeric
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 CHAR(3), c1 FLOAT, c2 DATETIME);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(11, 'abd', 100.0, 'abd');
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(21, x'616263', 31.125, 1);
-- go
-- run
SELECT json_group_object(c2, c0), percentile(c2, 0.25) FROM t0 GROUP BY c2, id;
