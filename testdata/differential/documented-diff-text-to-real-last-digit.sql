-- OneLite differential repro
-- status: expected-fail
-- class: text-to-real-last-digit
-- engine: ours
-- families: cast
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT CAST(concat(NULL, (65536 | c1), (1 << '10')) AS NUMERIC) FROM t0
-- detail: row 0: ours (1.0000000000000654E+22r); sqlite3 (1.0000000000000656E+22r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT, c1 FLOAT, c2 TEXT COLLATE RTRIM);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(34, 255, 1e18, '0');
-- go
-- run
SELECT CAST(concat(NULL, (65536 | c1), (1 << '10')) AS NUMERIC) FROM t0;
