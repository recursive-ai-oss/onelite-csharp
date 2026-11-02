-- OneLite differential repro
-- status: expected-fail
-- class: utf16-blob-text
-- engine: ours
-- families: expr,select,string
-- utf16: true
-- oracle: diff
-- ordered: false
-- query: SELECT concat(x'41', NULL, 0)
-- detail: row 0: ours ('0'); sqlite3 ('')
-- setup
PRAGMA encoding='UTF-16le';
-- go
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INT, c1 DOUBLE, c2 VARCHAR(10) COLLATE RTRIM, c3 REAL, c4 DATETIME);
-- go
-- run
SELECT concat(x'41', NULL, 0);
