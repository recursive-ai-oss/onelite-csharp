-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: json,join
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT key FROM json_each WHERE json = '[7,8]' ORDER BY key
-- detail: 
-- setup
CREATE TABLE d(id INTEGER PRIMARY KEY, j TEXT);
-- go
INSERT INTO d VALUES(1,'[1,2,{"a":[3,4]}]'),(2,'{"x":1,"y":[true,null]}'),(3,'5'),(4,NULL),(5,'[]');
-- go
-- run
SELECT key FROM json_each WHERE json = '[7,8]' ORDER BY key;
