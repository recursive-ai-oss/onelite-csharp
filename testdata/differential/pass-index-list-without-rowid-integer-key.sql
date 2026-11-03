-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: pragma
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT name, origin FROM pragma_index_list('a1')
-- detail: 
-- setup
CREATE TABLE a1(id INTEGER PRIMARY KEY, c1 UNIQUE, c3 unique, unique(c1,id)) WITHOUT ROWID;
-- go
CREATE INDEX a1x ON a1(c1);
-- go
-- run
SELECT name, origin FROM pragma_index_list('a1');
