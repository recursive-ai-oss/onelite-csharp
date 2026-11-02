-- OneLite differential repro
-- status: pass
-- class: bug:julianday-unixepoch-rounding
-- engine: ours
-- families: affinity,datetime,expr,like,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT julianday(c0rr, 'unixepoch') FROM t0
-- detail: row 0: ours (2440587.499982627r); sqlite3 (2440587.4999826388r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 VARCHAR(10), c1 DATETIME, c2 INTEGER, c3 BOOLEAN);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(63, -1.5, -1e300, 0, -2147483648);
-- go
ALTER TABLE t0 RENAME COLUMN c0 TO c0r;
-- go
ALTER TABLE t0 RENAME COLUMN c0r TO c0rr;
-- go
-- run
SELECT julianday(c0rr, 'unixepoch') FROM t0;
