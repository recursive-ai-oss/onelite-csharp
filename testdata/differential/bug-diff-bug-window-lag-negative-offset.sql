-- OneLite differential repro
-- status: pass
-- class: bug:window-lag-negative-offset
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT * FROM (SELECT id, min(id) OVER (PARTITION BY id, c1 ORDER BY c1 DESC ROWS BETWEEN 1+1 PRECEDING AND CURRENT ROW), lag(id, c1) OVER (ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), id FROM t2 ORDER BY id) WHERE id % 2 = 0 ORDER BY id
-- detail: row 1: ours (66, 66, 10, 66); sqlite3 (66, 66, NULL, 66)
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0, c1 INTEGER UNIQUE);
-- go
INSERT INTO t2(id, c0, c1) VALUES(10, NULL, '"s"');
-- go
INSERT OR REPLACE INTO t2(id, c0, c1) VALUES(13, '00', 100);
-- go
INSERT INTO t2(id, c0, c1) VALUES(63, x'616263', 2147483647);
-- go
INSERT INTO t2(id, c0, c1) VALUES(65, 'É', 19);
-- go
INSERT INTO t2(id, c0, c1) VALUES(66, 9223372036854775807, -6);
-- go
INSERT INTO t2(id, c0, c1) VALUES(67, 'ABC', 9007199254740993);
-- go
INSERT INTO t2(id, c0, c1) VALUES(68, 2147483647, 2.5);
-- go
-- run
SELECT * FROM (SELECT id, min(id) OVER (PARTITION BY id, c1 ORDER BY c1 DESC ROWS BETWEEN 1+1 PRECEDING AND CURRENT ROW), lag(id, c1) OVER (ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), id FROM t2 ORDER BY id) WHERE id % 2 = 0 ORDER BY id;
