-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: json,join
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT t.fullkey, t.path FROM json_tree('{"a":[1,{"b":2}]}', '$.a') AS t ORDER BY t.id
-- detail: 
-- setup
CREATE TABLE d(id INTEGER PRIMARY KEY, j TEXT);
-- go
INSERT INTO d VALUES(1,'[1,2,{"a":[3,4]}]'),(2,'{"x":1,"y":[true,null]}'),(3,'5'),(4,NULL),(5,'[]');
-- go
-- run
SELECT t.fullkey, t.path FROM json_tree('{"a":[1,{"b":2}]}', '$.a') AS t ORDER BY t.id;
