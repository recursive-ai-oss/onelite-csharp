-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: affinity,collation,expr,select
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, c0 = '10', c1 < 'abd', c3 + 1, c0 + 1, typeof(c2 * 2) FROM t0 WHERE c1 >= 'ab' OR c0 IS NULL ORDER BY id
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INTEGER, c1 TEXT COLLATE NOCASE, c2 REAL, c3);
-- go
CREATE INDEX i0 ON t0(c0);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(1, 10, 'abc', 1.5, x'00ff');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(2, NULL, 'ABD', -0.0, '10');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(3, 9223372036854775807, NULL, 1e300, 7);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(4, -5, '', 0.1, NULL);
-- go
-- run
SELECT id, c0 = '10', c1 < 'abd', c3 + 1, c0 + 1, typeof(c2 * 2) FROM t0 WHERE c1 >= 'ab' OR c0 IS NULL ORDER BY id;
