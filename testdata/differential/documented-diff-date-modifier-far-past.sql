-- OneLite differential repro
-- status: pass
-- class: date-modifier-far-past
-- engine: ours
-- families: datetime,expr,string,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT julianday((c0 || c0), '+1 year') FROM t0
-- detail: row 0: ours (4608r); sqlite3 (4607r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT, c1 TEXT COLLATE NOCASE, c2 NUMERIC, g0 GENERATED ALWAYS AS (c0 * 2) VIRTUAL) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(78, 42, '"s"', x'ff00ab');
-- go
-- run
SELECT julianday((c0 || c0), '+1 year') FROM t0;
