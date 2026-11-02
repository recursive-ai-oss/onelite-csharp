#!/usr/bin/env python3
"""Generates pragma differential cases (see pragma_diff.py) for every pragma name given on stdin or in the built-in list: get, set to several values,
set then get, schema-qualified forms (main, temp, an unknown name), and misuse (bad values, too many arguments). Deterministic.

usage: pragma_cases.py [name ...] > cases.txt
"""
import sys

SETUP = "create table t(a integer primary key, b text not null default 'x', c real unique);\ncreate index ib on t(b, c desc);\ncreate table p(x, y references t(a));\ninsert into t values (1,'a',1.5),(2,'b',2.5);\n"

BOOLS = ['0', '1', 'on', 'off', 'yes', 'no', 'true', 'false', 'TRUE', 'maybe', "'on'", '2', '-1', "''"]
INTS = ['0', '1', '5', '-5', '100', '-100', '4096', '1000000', "'abc'", '1.5', "''", '9999999999']

SPECIAL = {
    'auto_vacuum': ['0', '1', '2', 'none', 'full', 'incremental', 'NONE', '3', "'x'"],
    'journal_mode': ['delete', 'truncate', 'persist', 'memory', 'off', 'wal', 'DELETE', 'bogus', "'wal'", '1'],
    'locking_mode': ['normal', 'exclusive', 'NORMAL', 'EXCLUSIVE', 'bogus', '1'],
    'synchronous': ['0', '1', '2', '3', 'off', 'normal', 'full', 'extra', 'bogus', '4', '-1'],
    'temp_store': ['0', '1', '2', 'default', 'file', 'memory', 'bogus', '3'],
    'page_size': ['512', '1024', '4096', '65536', '131072', '1000', '0', '-1', "'x'", '2048'],
    'encoding': ["'UTF-8'", "'UTF-16'", "'UTF-16le'", "'UTF-16be'", "'utf8'", "'bogus'", 'UTF8'],
    'cache_size': INTS, 'cache_spill': INTS + ['on', 'off'], 'mmap_size': INTS,
    'busy_timeout': INTS, 'wal_autocheckpoint': INTS, 'journal_size_limit': INTS, 'max_page_count': INTS,
    'user_version': INTS, 'application_id': INTS + ['4294967295', '4294967296'],
    'soft_heap_limit': INTS, 'hard_heap_limit': ['0', "'abc'", '1.5', "''", '-5'], 'analysis_limit': INTS, 'threads': INTS,
    'wal_checkpoint': ['passive', 'full', 'restart', 'truncate', 'bogus', '1', "'full'"],
    'default_cache_size': INTS, 'schema_version': INTS, 'temp_store_directory': ["''", "'/nonexistent'"],
    'optimize': ['', '0x10002', '1', '-1', "'x'"],
    'integrity_check': ['', '1', '2', '100', 't', "'t'", 'nosuch', '-1', '0'],
    'quick_check': ['', '1', 't', 'nosuch'],
    'incremental_vacuum': ['', '1', '5', '-1', '0', "'x'"],
}
TABLE = ['table_info', 'table_xinfo', 'index_list', 'index_info', 'index_xinfo', 'foreign_key_list', 'foreign_key_check', 'table_list']
READONLY = ['collation_list', 'compile_options', 'database_list', 'data_version', 'freelist_count', 'function_list', 'module_list',
            'page_count', 'pragma_list']

def cases(name):
    out = []
    def add(*stmts, pre=SETUP):
        out.append(pre + '\n'.join(stmts))
    add('pragma %s;' % name)
    if name in TABLE:
        for arg in ['t', 'ib', 'p', 'nosuch', "'t'", '"t"', "sqlite_master", 'ib, 1', 't, 2']:
            add('pragma %s(%s);' % (name, arg))
            add('pragma %s=%s;' % (name, arg))
        add('pragma main.%s(t);' % name, 'pragma temp.%s(t);' % name, 'pragma nosuch.%s(t);' % name)
        add('create table u(a, b, c, primary key(a, b)) without rowid;', 'create index ic on u(c);', 'pragma %s(u);' % name,
            'pragma %s(ic);' % name)
        add('create table v(a int, b as (a+1), c as (a*2) stored, hidden);', 'pragma %s(v);' % name)
        return out
    if name in READONLY:
        add('pragma main.%s;' % name, 'pragma temp.%s;' % name, 'pragma nosuch.%s;' % name)
        add('pragma %s=1;' % name, 'pragma %s(1);' % name)
        return out
    vals = SPECIAL.get(name, BOOLS if name not in SPECIAL else INTS)
    for v in vals:
        add('pragma %s=%s;' % (name, v), 'pragma %s;' % name)
        add('pragma %s(%s);' % (name, v), 'pragma %s;' % name)
    add('pragma main.%s;' % name, 'pragma temp.%s;' % name, 'pragma nosuch.%s;' % name)
    if name == 'hard_heap_limit':
        return out  # a non-zero hard limit makes the shell itself run out of memory
    add('pragma main.%s=1;' % name, 'pragma main.%s;' % name)
    add('pragma temp.%s=1;' % name, 'pragma temp.%s;' % name)
    add('pragma %s(1, 2);' % name)
    add('begin;', 'pragma %s=1;' % name, 'pragma %s;' % name, 'commit;')
    add('begin immediate;', 'pragma %s;' % name, 'pragma %s=0;' % name, 'pragma %s;' % name, 'rollback;')
    return out

NAMES = ['analysis_limit', 'application_id', 'auto_vacuum', 'automatic_index', 'busy_timeout', 'cache_size', 'cache_spill',
         'case_sensitive_like', 'cell_size_check', 'checkpoint_fullfsync', 'collation_list', 'compile_options', 'count_changes',
         'data_version', 'database_list', 'default_cache_size', 'defer_foreign_keys', 'empty_result_callbacks', 'encoding',
         'foreign_key_check', 'foreign_key_list', 'foreign_keys', 'freelist_count', 'full_column_names', 'fullfsync',
         'function_list', 'hard_heap_limit', 'ignore_check_constraints', 'incremental_vacuum', 'index_info', 'index_list',
         'index_xinfo', 'integrity_check', 'journal_mode', 'journal_size_limit', 'legacy_alter_table', 'locking_mode',
         'max_page_count', 'mmap_size', 'module_list', 'optimize', 'page_count', 'page_size', 'pragma_list', 'query_only',
         'quick_check', 'read_uncommitted', 'recursive_triggers', 'reverse_unordered_selects', 'schema_version', 'secure_delete',
         'short_column_names', 'shrink_memory', 'soft_heap_limit', 'synchronous', 'table_info', 'table_list', 'table_xinfo',
         'temp_store', 'temp_store_directory', 'threads', 'trusted_schema', 'user_version', 'wal_autocheckpoint',
         'wal_checkpoint', 'writable_schema']

if __name__ == '__main__':
    names = sys.argv[1:] or NAMES
    allc = []
    for n in names:
        for c in cases(n):
            allc.append('-- pragma: %s\n%s' % (n, c))
    sys.stdout.write('\n%%\n'.join(allc) + '\n')
