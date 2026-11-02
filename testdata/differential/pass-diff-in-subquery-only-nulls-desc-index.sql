-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT v, v IN (SELECT c FROM s) FROM p
-- detail: 
-- setup
CREATE TABLE s(c)
-- go
CREATE INDEX si ON s(c DESC)
-- go
INSERT INTO s VALUES(NULL)
-- go
CREATE TABLE p(v)
-- go
INSERT INTO p VALUES(10),(2),(NULL)
-- go
-- run
SELECT v, v IN (SELECT c FROM s) FROM p;
