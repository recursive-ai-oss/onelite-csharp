-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id FROM t ORDER BY a DESC, id DESC
-- detail: 
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, a, b, c TEXT);
-- go
CREATE INDEX i ON t(a, b);
-- go
INSERT INTO t VALUES(1, 2, 1, 'x'), (2, 1, 2, 'y'), (3, NULL, 3, 'z'), (4, 1, 1, 'w'), (5, 2, 0, 'v'), (6, 'q', 1, 'u');
-- go
-- run
SELECT id FROM t ORDER BY a DESC, id DESC;
