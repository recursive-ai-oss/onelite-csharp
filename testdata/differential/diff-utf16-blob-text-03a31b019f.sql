-- OneLite differential repro
-- status: expected-fail
-- class: utf16-blob-text
-- engine: ours
-- families: affinity,expr,orderlimit,string
-- utf16: true
-- oracle: diff
-- ordered: false
-- query: SELECT instr(0, c0) FROM t0n
-- detail: row 0: ours (1); sqlite3 (2)
-- setup
PRAGMA encoding='UTF-16be';
-- go
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 INT);
-- go
INSERT INTO t0(id, c0, c1) VALUES(2, x'41', ('t' || 'a' || 'b' || char(9) || 'h' || 'e' || 'r' || 'e'));
-- go
ALTER TABLE t0 RENAME TO t0n;
-- go
-- run
SELECT instr(0, c0) FROM t0n;
