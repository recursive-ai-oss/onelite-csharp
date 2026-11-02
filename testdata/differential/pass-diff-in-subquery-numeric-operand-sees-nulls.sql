-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (SELECT group_concat(ifnull(v IN (SELECT c FROM s),'N')) FROM p), (SELECT group_concat(ifnull(v IN (SELECT c FROM s),'N')) FROM q)
-- detail: 
-- setup
CREATE TABLE s(c)
-- go
CREATE INDEX si ON s(c DESC)
-- go
INSERT INTO s VALUES(200),(NULL)
-- go
CREATE TABLE p(v integer primary key)
-- go
CREATE TABLE q(v)
-- go
INSERT INTO p VALUES(48),(1)
-- go
INSERT INTO q VALUES(48),(1)
-- go
-- run
SELECT (SELECT group_concat(ifnull(v IN (SELECT c FROM s),'N')) FROM p), (SELECT group_concat(ifnull(v IN (SELECT c FROM s),'N')) FROM q);
