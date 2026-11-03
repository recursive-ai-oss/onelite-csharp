-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: pragma
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT name, origin FROM pragma_index_list('a7')
-- detail: 
-- setup
CREATE TABLE a7(c0 unique, id integer, c1 UNIQUE, primary key (id, c0), unique(c1,id)) WITHOUT ROWID;
-- go
-- run
SELECT name, origin FROM pragma_index_list('a7');
