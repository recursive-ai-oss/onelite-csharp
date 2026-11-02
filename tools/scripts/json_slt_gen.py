#!/usr/bin/env python3
"""Writes the JSON sqllogictest-format script set under testdata/sqllogictest/json from the pinned sqlite3 shell.

    tools/scripts/json_slt_gen.py SQLITE3 OUT_DIR

Every expected result is whatever the shell printed for the statement, so the scripts are oracle output, not
hand-written expectations. A statement that fails in the shell becomes `statement error`; a result with a newline
is wrapped in replace(..., char(10), '~') first because the format has one row per line. Real results are
cast to text and blobs are shown with hex(), so the check covers the exact text.
The shell version is recorded in the header of each file.
"""
import subprocess, sys, os

SQLITE, OUT = sys.argv[1], sys.argv[2]
VERSION = subprocess.run([SQLITE, ":memory:", "select sqlite_version()"], capture_output=True, text=True).stdout.strip()

DOCS = [
    "'{\"a\":1,\"b\":[1,2,{\"c\":\"x\"}],\"d\":null,\"e\":true,\"f\":1.5}'",
    "'[1,2,3,[4,5],{\"k\":\"v\"}]'",
    "'  {\"a\" : [ 1 , 2 ] , \"b\" : \"s\\u00e9\\n\" }  '",
    "'{a:1,\\'b\\':0x10,c:[+1,.5,5.,Infinity,-Infinity,NaN,],}'",
    "'{\"a\":{\"b\":{\"c\":{\"d\":[[[]]]}}}}'",
    "'\"text\"'", "'12345678901234567890'", "'-0'", "'1e400'", "'0.1'", "'null'", "'true'",
    "'[1,2'", "'{\"a\":}'", "''", "'{\"a\":1,\"a\":2}'", "'[\"a\\\\u0041\\\\ud83d\\\\ude00\"]'",
    "'/* c */ [1, // x\n 2]'", "'{\"x\":[],\"y\":{}}'", "'[0x7fffffffffffffff, 0xffffffffffffffff, -0x10]'",
]
PATHS = ["'$'", "'$.a'", "'$.b'", "'$.b[1]'", "'$.b[#-1]'", "'$.b[#-1].c'", "'$[0]'", "'$[#-1]'", "'$[3][0]'", "'$[4].k'",
         "'$.a.b.c.d'", "'$.\"a\"'", "'$.nope'", "'$[99]'", "'a'", "'$.'", "'$['", "'$[x]'", "'$.a[0]'", "'$.x'", "'$.e'", "'$.f'"]
VALUES = ["1", "'x'", "null", "1.5", "json('[1]')", "'{\"q\":1}'", "x'00ff'", "true", "'a\"b'"]

stmts = {}


def add(group, sql):
    stmts.setdefault(group, []).append(sql)


for d in DOCS:
    for f in ["json", "json_valid", "json_error_position", "json_type", "json_array_length", "json_pretty", "json_quote"]:
        add("basic", f"select {f}({d})")
    add("basic", f"select hex(jsonb({d}))")
    add("basic", f"select json(jsonb({d}))")
    for fl in [1, 2, 3, 4, 5, 6, 8, 9, 15]:
        add("valid", f"select json_valid({d}, {fl})")
        add("valid", f"select json_valid(jsonb({d}), {fl})")
    for p in PATHS:
        add("extract", f"select json_extract({d}, {p})")
        add("extract", f"select {d} -> {p}")
        add("extract", f"select {d} ->> {p}")
        add("extract", f"select json_type({d}, {p})")
        add("extract", f"select json_array_length({d}, {p})")
        add("extract", f"select hex(jsonb_extract({d}, {p}))")
        add("remove", f"select json_remove({d}, {p})")
        add("remove", f"select hex(jsonb_remove({d}, {p}))")
        for v in VALUES[:3]:
            if PATHS.index(p) % 2: break
            for f in ["json_set", "json_insert", "json_replace", "json_array_insert"]:
                add("edit", f"select {f}({d}, {p}, {v})")
        add("edit", f"select hex(jsonb_set({d}, {p}, 7))")
        add("edit", f"select hex(jsonb_insert({d}, {p}, 'x'))")
    add("extract", f"select json_extract({d}, '$.a', '$.b')")
    add("extract", f"select json_extract({d}, '$[0]', '$[1]', '$.nope')")
    add("edit", f"select json_set({d}, '$.a', 1, '$.z', 2, '$.b[0]', 3)")
    add("edit", f"select json_set({d}, '$.a')")

for a in DOCS[:10]:
    for b in DOCS[:10]:
        add("patch", f"select json_patch({a}, {b})")
add("patch", "select json_patch('{\"a\":{\"b\":1}}', '{\"a\":{\"b\":null,\"c\":2}}')")
add("patch", "select hex(jsonb_patch('{\"a\":1}', '{\"b\":2}'))")

for v in VALUES:
    for w in VALUES:
        add("build", f"select json_array({v}, {w})")
        add("build", f"select json_object('k', {v}, 'l', {w})")
        add("build", f"select hex(jsonb_array({v}, {w}))")
        add("build", f"select hex(jsonb_object('k', {v}))")
    add("build", f"select json_quote({v})")
    add("build", f"select json_array({v})")
add("build", "select json_array()")
add("build", "select json_object()")
add("build", "select json_object('a')")
add("build", "select json_object(1, 2)")
add("build", "select json_object(null, 2)")
add("build", "select json_array(json_array(1,2), '[3]', json('[4]'))")
add("build", "select json_object('a', json_object('b', 1), 'c', '{\"d\":2}')")

# Operators and subtype behaviour
add("subtype", "select json('[1]') || 'x'")
add("subtype", "select json_array(json('[1]') || '')")
add("subtype", "select json_extract('[[1]]', '$[0]')")
add("subtype", "select json_array(json_extract('[[1]]', '$[0]'))")
add("subtype", "select json_array('[1]'->'$')")
add("subtype", "select json_array('{\"a\":[1]}'->>'$.a')")
add("subtype", "select typeof('[1]'->'$'), typeof('[1]'->>'$[0]'), typeof('{\"a\":\"x\"}'->>'a')")
add("subtype", "select json_object('a', 1) -> 'a', json_object('a', 'x') ->> 'a'")
add("subtype", "select '{\"a\":2}' -> '$.a' + 1")
add("subtype", "select '{\"a\":{\"b\":3}}' -> 'a' -> 'b'")
add("subtype", "select '{\"a\":1}' -> null, null -> '$'")
add("subtype", "select json_valid(x'7b7d'), json_valid(jsonb('{}')), json_valid(x'00')")
add("subtype", "select typeof(jsonb('[1]')), typeof(json('[1]'))")
add("subtype", "select hex(jsonb('[1,2,{\"a\":\"b\"}]'))")
add("subtype", "select json(jsonb('{\"a\":[1,2.5,\"x\",null,true]}'))")
add("subtype", "select json_pretty('{\"a\":[1,2,{\"b\":[]}],\"c\":{}}')")
add("subtype", "select json_pretty('[1,[2,3]]', '  ')")
add("subtype", "select json_pretty('[1,[2,3]]', null)")

# Large numbers and reals
for n in ["9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809", "1e308", "1e309", "-1e309", "5e-324", "1.0", "100.0", "0.30000000000000004", "1E5", "1.5e+2", "0.1e1", "123456789012345678901234567890"]:
    add("numbers", f"select json('[{n}]')")
    add("numbers", f"select json_extract('[{n}]', '$[0]')")
    add("numbers", f"select json_type('[{n}]', '$[0]')")
    add("numbers", f"select '[{n}]' ->> 0, '[{n}]' -> 0")
    add("numbers", f"select json_array({n})")
    add("numbers", f"select hex(jsonb('[{n}]'))")

add("errors", "select json_extract('[1]', '$[')")
add("errors", "select json_insert('[1]', 'x', 1)")
add("errors", "select json_set('[1]', '$.a', 1) ")
add("errors", "select json_remove('[1]', '$')")
add("errors", "select json_patch('[1]', 'x')")
add("errors", "select json_object('a')")
add("errors", "select json_array_length('{}')")
add("errors", "select json_valid()")
add("errors", "select json_valid('[1]', 0)")
add("errors", "select json_valid('[1]', 16)")
add("errors", "select json_valid('[1]', null)")
add("errors", "select json_extract('[1]')")
add("errors", "select json_type('[1]', '$', 3)")
add("errors", "select json_pretty('[1]', 1, 2)")
add("errors", "select json_quote()")
add("errors", "select json(x'ff')")
add("errors", "select json(x'')")
add("errors", "select json_array_insert('[1]', '$.a', 1)")
add("errors", "select json_array_insert('[1]', '$[0].a', 1)")
add("errors", "select '[1]' -> 'a' -> 3")
deep = "[" * 1000 + "]" * 1000
deep1 = "[" * 1001 + "]" * 1001
add("errors", f"select json_valid('{deep}'), json_valid('{deep1}')")
ROW = ("coalesce(key,'N')||'|'||coalesce(typeof(value),'N')||'|'||coalesce(cast(value as text),'N')||'|'||type||'|'||coalesce(cast(atom as text),'N')"
       "||'|'||id||'|'||coalesce(parent,'N')||'|'||fullkey||'|'||path||'|'||rowid")
for d in DOCS:
    for fn in ["json_each", "json_tree", "jsonb_each", "jsonb_tree"]:
        add("tables", f"select group_concat({ROW}, ' ; ') from {fn}({d})")
        for p in PATHS[:12]:
            add("tables", f"select group_concat({ROW}, ' ; ') from {fn}({d}, {p})")
    add("tables", f"select group_concat(a.key||'/'||b.key, ' ') from json_each({d}) a, json_each(a.value) b")
    add("tables", f"select count(*) from json_tree({d}) where type = 'integer'")
    add("tables", f"select json_group_array(value), json_group_object(coalesce(key, 'k'), type) from json_each({d})")
    add("tables", f"select json_group_array(value) from json_tree({d})")
    add("tables", f"select hex(jsonb_group_array(atom)) from json_each({d})")
for a_, b_, c_ in [(1, 5, 1), (5, 1, -1), (5, 1, 1), (0, 10, 3), (-3, 3, 2), (1, 1, 1), (3, 1, -2), (0, 0, 0), (1, 3, 0), (-5, -1, 1)]:
    add("tables", f"select group_concat(value||':'||rowid, ' ') from generate_series({a_}, {b_}, {c_})")
for t_ in ["generate_series(1, 4)", "generate_series(2.7, 6.2)", "generate_series('3', 5)", "generate_series(NULL, 5)", "generate_series(1, NULL)",
           "generate_series(9223372036854775806, 9223372036854775807)", "generate_series(-9223372036854775807, -9223372036854775808, -1)"]:
    add("tables", f"select group_concat(value, ' ') from {t_}")
add("tables", "select count(*) from generate_series where start = 1 and stop = 100 and step = 7")
add("tables", "select group_concat(value, ' ') from generate_series where start = 2 and stop = 9")
add("tables", "select * from generate_series")
add("tables", "select group_concat(key, ' ') from json_each where json = '[4,5,6]'")
add("tables", "select count(*) from json_each('[1]', '$', 3)")
add("tables", "select count(*) from json_each('[1')")
add("tables", "select group_concat(x.value||y.value, ' ') from generate_series(1, 3) x, generate_series(1, 2) y")
add("tables", "select group_concat(e.value, ' ') from json_each('[[1,2],[3]]') o, json_each(o.value) e")
add("tables", "select group_concat(value, ' ') from json_each(null)")
add("tables", "select json_group_array(1), json_group_array(null), json_group_array('x'), json_group_array(json('[]'))")
add("tables", "select json_group_object('a', 1), json_group_object(1.5, 'x')")
add("tables", "select json_group_array(v) from (select 1 v where 0)")
add("tables", "select json_group_object(k, v) from (select null k, 1 v)")
add("tables", "select json_group_array(x'41')")
add("errors", f"select length(json('{deep}'))")
add("errors", f"select json('{deep1}')")
add("errors", f"select length(json_pretty('{deep}'))")
add("errors", f"select json_valid(jsonb('{deep}'), 8)")


def run(sql):
    return subprocess.run([SQLITE, ":memory:", "-escape", "off", "-list", "-separator", "\x1f", "-nullvalue", "NULL"], input=sql, capture_output=True, text=True, errors="replace")


def failed(r):
    return r.returncode != 0 or r.stderr.strip() != ""


files = {}
for group, lst in stmts.items():
    if lst is None:
        continue
    seen = set()
    lines = [f"# JSON functions, group '{group}'. Expected results are the output of sqlite {VERSION} (the pinned shell).", ""]
    for sql in lst:
        if sql in seen:
            continue
        seen.add(sql)
        sql = sql.rstrip()
        r = run(sql + ";")
        if failed(r):
            lines.append("statement error")
            lines.append(sql)
            lines.append("")
            continue
        # the column count and the type of each value, then a wrapper that shows reals as text and blobs as hex
        n = int(run(f"create temp table t as {sql}; select count(*) from pragma_table_info('t');").stdout.strip() or 0)
        cols = ",".join(f"c{i}" for i in range(n))
        ty = run(f"create temp table u({cols}); insert into u {sql}; select {','.join(f'typeof(c{i})' for i in range(n))} from u;").stdout.strip().split("\x1f")
        qt = ""
        wrap = None
        for i in range(n):
            t = ty[i] if i < len(ty) else "null"
            qt += "I" if t == "integer" else "T"
            if t in ("blob", "real"):
                wrap = t
        tested = sql
        # Reals are shown as their text and blobs as hex; a multi-line text has its newlines replaced. Only single-column
        # statements are wrapped (the engine under test then needs nothing beyond scalar expressions).
        if wrap is not None or (n == 1 and ty[0] == "text" and "\n" in run(sql + ";").stdout):
            if n != 1 or not sql.startswith("select "):
                continue
            e = sql[len("select "):]
            if " from " in e:
                # a statement over a table: wrap it in a CTE so the wrapper applies to its one column
                w = "hex(x)" if wrap == "blob" else ("cast(x as text)" if wrap == "real" else "replace(x, char(10), '~')")
                tested = f"with q(x) as ({sql}) select {w} from q"
            else:
                tested = f"select hex({e})" if wrap == "blob" else (f"select cast({e} as text)" if wrap == "real" else f"select replace({e}, char(10), '~')")
        o = run(tested + ";")
        if failed(o):
            lines.append("statement error")
            lines.append(sql)
            lines.append("")
            continue
        vals = o.stdout.rstrip("\n").split("\x1f") if o.stdout != "" else []
        vals = [v if v != "" else "(empty)" for v in vals]
        if len(vals) != n or any(ord(ch) < 32 or ord(ch) > 126 for v in vals for ch in v):
            # the format shows non-printable characters as '@', so such results cannot be written down faithfully
            continue
        lines.append(f"query {qt} nosort")
        lines.append(tested)
        lines.append("----")
        lines.extend(vals)
        lines.append("")
    files[group] = lines

os.makedirs(OUT, exist_ok=True)
total = 0
for g, lines in files.items():
    with open(os.path.join(OUT, f"json_{g}.test"), "w", newline="\n") as f:
        f.write("\n".join(lines))
    total += sum(1 for l in lines if l.startswith("query") or l == "statement error")
print("scripts", len(files), "records", total, "sqlite", VERSION)
