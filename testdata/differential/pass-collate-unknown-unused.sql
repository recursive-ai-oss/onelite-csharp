-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: collation
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT 'a' COLLATE nosuch, abs(1 COLLATE nosuch), 'a' COLLATE nosuch IS NULL
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY);
-- go
-- run
SELECT 'a' COLLATE nosuch, abs(1 COLLATE nosuch), 'a' COLLATE nosuch IS NULL;
