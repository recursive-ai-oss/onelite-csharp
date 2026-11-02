-- OneLite differential repro
-- status: expected-fail
-- class: like-invalid-utf8
-- engine: ours
-- families: collation,expr,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (c2 COLLATE nocase LIKE x'e9') FROM t0
-- detail: row 0: ours (0); sqlite3 (1)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1, c2 DECIMAL(5,2), c3 INTEGER) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(181, 9007199254740993, -1, x'ff00ab', x'00');
-- go
-- run
SELECT (c2 COLLATE nocase LIKE x'e9') FROM t0;
