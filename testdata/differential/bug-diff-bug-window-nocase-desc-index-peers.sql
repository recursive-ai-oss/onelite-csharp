-- OneLite differential repro
-- status: pass
-- class: bug:window-nocase-desc-index-peers
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, row_number() OVER (), sum(c2)  OVER (ORDER BY id ROWS 3 PRECEDING), first_value(c1) OVER (ORDER BY c0 RANGE BETWEEN 3 FOLLOWING AND 0 FOLLOWING EXCLUDE NO OTHERS) FROM t2 ORDER BY id
-- detail: row 0: ours (5, 1, NULL, -26); sqlite3 (5, 1, NULL, -2147483648)
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 CHAR(3) COLLATE NOCASE, c1 INTEGER, c2 VARCHAR(10), c3 FLOAT);
-- go
CREATE INDEX i0 ON t2(c0 DESC);
-- go
INSERT INTO t2(id, c0, c1, c2, c3) VALUES(5, 'é', -26, NULL, 3.14159);
-- go
INSERT INTO t2(id, c0, c1, c2, c3) VALUES(223, 'é', -2147483648, 'abc', 1e18);
-- go
-- run
SELECT id, row_number() OVER (), sum(c2)  OVER (ORDER BY id ROWS 3 PRECEDING), first_value(c1) OVER (ORDER BY c0 RANGE BETWEEN 3 FOLLOWING AND 0 FOLLOWING EXCLUDE NO OTHERS) FROM t2 ORDER BY id;
