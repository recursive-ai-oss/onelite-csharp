-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (SELECT max(c) FROM s), count(*), v IN (SELECT c FROM s), (SELECT count(*) FROM s WHERE c>=v), count(exists(SELECT c FROM s WHERE c=v)), sum((SELECT count(*) FROM s WHERE c>=v)) FROM p GROUP BY v HAVING (SELECT count(*) FROM s WHERE c>=v)>0 ORDER BY (SELECT count(*) FROM s WHERE c<v)
-- detail: 
-- setup
CREATE TABLE s(c)
-- go
INSERT INTO s VALUES(200),(NULL)
-- go
CREATE TABLE p(v)
-- go
INSERT INTO p VALUES(1),(200)
-- go
-- run
SELECT (SELECT max(c) FROM s), count(*), v IN (SELECT c FROM s), (SELECT count(*) FROM s WHERE c>=v), count(exists(SELECT c FROM s WHERE c=v)), sum((SELECT count(*) FROM s WHERE c>=v)) FROM p GROUP BY v HAVING (SELECT count(*) FROM s WHERE c>=v)>0 ORDER BY (SELECT count(*) FROM s WHERE c<v);
