-- OneLite differential repro
-- status: pass
-- class: bug:jsonb-set-padding-and-hex
-- engine: ours
-- families: json
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT hex(jsonb(json_set(' {"c":{x:-1,"é":7,c:{"c" : null,a:true,},"a b":-1e400,},"a":{name:1e400,''b'':255},"c" : 123456789012345678901234567890}/* t */', '$.x', json('5e-324'), '$[5]', json('1.5'))))
-- detail: row 0: ours ('CC621763CC1E1778232D3127C3A9133717636C17630017610137612062652D31653430301761CC11476E616D655531653430301762333235351763C31E31323334353637383930313233343536373839303132333435363738393017786535652D333234'); sqlite3 ('CC621763CC1E1778232D3127C3A9133717636C17630017610137612062652D31653430301761CC11476E616D655531653430301762333235351763C31E3132333435363738393031323334353637383930313233343536373839301A786535652D333234')
-- setup
-- run
SELECT hex(jsonb(json_set(' {"c":{x:-1,"é":7,c:{"c" : null,a:true,},"a b":-1e400,},"a":{name:1e400,''b'':255},"c" : 123456789012345678901234567890}/* t */', '$.x', json('5e-324'), '$[5]', json('1.5'))));
