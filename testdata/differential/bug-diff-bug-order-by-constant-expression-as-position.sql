-- OneLite differential repro
-- status: pass
-- class: bug:order-by-constant-expression-as-position
-- engine: ours
-- families: expr,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT NULL FROM t2 ORDER BY ((0 ISNULL) IS NULL), NULL
-- detail: ours: 0 rows; sqlite3: parse error (1): 1st ORDER BY term out of range - should be between 1 and 1
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 TEXT, c1 BLOB UNIQUE, c2 INTEGER, c3 DATETIME, c4 DECIMAL(5,2));
-- go
-- run
SELECT NULL FROM t2 ORDER BY ((0 ISNULL) IS NULL), NULL;
