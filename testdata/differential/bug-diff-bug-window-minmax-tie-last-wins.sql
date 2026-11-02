-- OneLite differential repro
-- status: pass
-- class: bug:window-minmax-tie-last-wins
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id, min(c0) OVER (ORDER BY c0 DESC GROUPS CURRENT ROW), rank() OVER (ORDER BY c0 NULLS LAST), dense_rank() OVER (ORDER BY id DESC, id), c1 FROM t2
-- detail: row 0: ours (6, 100r, 1, 2, -9223372036854775808); sqlite3 (6, 100, 1, 2, -9223372036854775808)
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 ANY, c1 INT) STRICT;
-- go
INSERT INTO t2(id, c0, c1) VALUES(6, 100, -9223372036854775808);
-- go
INSERT INTO t2(id, c0, c1) VALUES(7, 100.0, 9223372036854775807);
-- go
-- run
SELECT id, min(c0) OVER (ORDER BY c0 DESC GROUPS CURRENT ROW), rank() OVER (ORDER BY c0 NULLS LAST), dense_rank() OVER (ORDER BY id DESC, id), c1 FROM t2;
