-- OneLite differential repro
-- status: pass
-- class: fixed:date-floor-division-before-year-minus-100
-- engine: ours
-- families: collation,datetime,expr,select
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT strftime('%Y-%m-%d', (0 - datetime(0, 'ceiling')), '+01:30', '-3 months') FROM lg
-- detail: row 0: ours ('-4700-07-21'); sqlite3 ('-4700-07-20')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 NUMERIC, c1 BLOB);
-- go
CREATE TABLE lg(id INTEGER PRIMARY KEY, v);
-- go
CREATE TRIGGER trg0 AFTER INSERT ON t0 BEGIN INSERT INTO lg(v) VALUES(new.id); END;
-- go
INSERT INTO t0(id, c0, c1) VALUES(3, 100, 'hello world');
-- go
-- run
SELECT strftime('%Y-%m-%d', (0 - datetime(0, 'ceiling')), '+01:30', '-3 months') FROM lg;
