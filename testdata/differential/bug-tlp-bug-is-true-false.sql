-- OneLite differential repro
-- status: pass
-- class: bug:is-true-false
-- engine: ours
-- families: affinity,expr,like
-- utf16: false
-- oracle: tlp
-- from: t2
-- predicate: (NULL IS FALSE)
-- detail: ours: parse error (1): no such column: FALSE; sqlite3: rows
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 DECIMAL(5,2), c1 CHAR(3), g0 GENERATED ALWAYS AS (c0 * 2) STORED);
-- go
-- run
SELECT * FROM t2;
SELECT * FROM t2 WHERE ((NULL IS FALSE));
SELECT * FROM t2 WHERE NOT ((NULL IS FALSE));
SELECT * FROM t2 WHERE ((NULL IS FALSE)) IS NULL;
