-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,json,window
-- utf16: true
-- oracle: diff
-- ordered: false
-- query: SELECT json_extract(c1, 'bad') FROM t0
-- detail: error differs: ours run error (1): malformed JSON; sqlite3 run error (1): bad JSON path: 'bad'
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BOOLEAN, c1 BLOB, c2 INT, c3 VARCHAR(10) COLLATE BINARY) WITHOUT ROWID;
-- go
CREATE INDEX i2 ON t0(c1 COLLATE NOCASE);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(5, NULL, x'', 9223372036854775807, '日本');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(6, 9223372036854775807, 255, 10, NULL);
-- go
-- run
SELECT json_extract(c1, 'bad') FROM t0;
