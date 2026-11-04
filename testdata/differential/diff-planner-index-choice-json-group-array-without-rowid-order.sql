-- OneLite differential repro
-- status: expected-fail
-- class: planner-index-choice
-- engine: ours
-- families: aggregate,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT json_group_array(c0) FROM t1
-- detail: row 0: ours ('[9223372036854775807,2147483647]'); sqlite3 ('[2147483647,9223372036854775807]')
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT UNIQUE, c1 BLOB) WITHOUT ROWID;
-- go
INSERT INTO t1(id, c0, c1) VALUES(4, 9223372036854775807, x'ff00ab');
-- go
INSERT INTO t1(id, c0, c1) VALUES(5, 2147483647, '[1,2,3]');
-- go
-- run
SELECT json_group_array(c0) FROM t1;
