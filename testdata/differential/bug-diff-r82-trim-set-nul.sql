-- OneLite differential repro
-- status: pass
-- class: bug:r82-trim-set-nul
-- engine: ours
-- families: affinity,expr,string,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT ltrim(c0, ('a' || 'b' || char(0) || 'c' || 'd')) FROM t0
-- detail: row 0: ours (''); sqlite3 ('c')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 TEXT COLLATE BINARY, c1 BOOLEAN, c2 BOOLEAN, c3 NUMERIC) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(149, 'abc', NULL, 2, 2);
-- go
-- run
SELECT ltrim(c0, ('a' || 'b' || char(0) || 'c' || 'd')) FROM t0;
