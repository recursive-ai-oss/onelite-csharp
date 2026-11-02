-- OneLite differential repro
-- status: pass
-- class: fixed:group-by-folded-signs-are-position
-- engine: ours
-- families: aggregate,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT NULL FROM t0 GROUP BY (- (- 0))
-- detail: ours: 0 rows; sqlite3: parse error (1): 1st GROUP BY term out of range - should be between 1 and 1
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 DECIMAL(5,2));
-- go
-- run
SELECT NULL FROM t0 GROUP BY (- (- 0));
