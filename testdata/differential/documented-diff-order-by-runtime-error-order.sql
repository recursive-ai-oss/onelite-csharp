-- OneLite differential repro
-- status: expected-fail
-- class: runtime-error-order
-- engine: ours
-- families: json,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT json_object('k', x'00000000') FROM t1 ORDER BY ('' -> '$.a'), id LIMIT 1
-- detail: error differs: ours run error (1): JSON cannot hold BLOB values; sqlite3 run error (1): malformed JSON
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 BOOLEAN, c1 CHAR(3));
-- go
INSERT INTO t1(id, c0, c1) VALUES(62, 'NULL', 'x');
-- go
-- run
SELECT json_object('k', x'00000000') FROM t1 ORDER BY ('' -> '$.a'), id LIMIT 1;
