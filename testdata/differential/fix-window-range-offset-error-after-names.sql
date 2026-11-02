-- OneLite differential repro
-- status: pass
-- class: fixed:window-range-offset-error-after-names
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, max(c1) OVER (ORDER BY c1, id, c1 RANGE BETWEEN UNBOUNDED PRECEDING AND 2 PRECEDING), total(id) OVER (ORDER BY c0 COLLATE NOCASE GROUPS BETWEEN 2 FOLLOWING AND 0 FOLLOWING), count(*) OVER (ORDER BY c0 DESC RANGE BETWEEN UNBOUNDED PRECEDING AND 0 PRECEDING EXCLUDE CURRENT ROW) FROM v0 ORDER BY id
-- detail: error differs: ours parse error (1): RANGE with offset PRECEDING/FOLLOWING requires one ORDER BY expression; sqlite3 parse error (1): no such column: c1
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 INT, c1 ANY) STRICT;
-- go
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t1;
-- go
ALTER TABLE t1 RENAME COLUMN c1 TO c1r;
-- go
-- run
SELECT id, max(c1) OVER (ORDER BY c1, id, c1 RANGE BETWEEN UNBOUNDED PRECEDING AND 2 PRECEDING), total(id) OVER (ORDER BY c0 COLLATE NOCASE GROUPS BETWEEN 2 FOLLOWING AND 0 FOLLOWING), count(*) OVER (ORDER BY c0 DESC RANGE BETWEEN UNBOUNDED PRECEDING AND 0 PRECEDING EXCLUDE CURRENT ROW) FROM v0 ORDER BY id;
