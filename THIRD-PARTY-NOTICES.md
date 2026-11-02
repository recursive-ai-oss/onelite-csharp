# Third-party notices

OneLite's shipped packages contain no third-party code. Anything that is later included, or any redistributed test data, is listed here with its source, licence and SHA-256.

| Component | Source | Licence | SHA-256 | Shipped in |
|---|---|---|---|---|
| sqllogictest test scripts (`test/` tree) | https://github.com/gregrahn/sqllogictest at commit `c93ac416f05635ff5cc490009add936d9753d01d` (mirror of the sqllogictest repository; © 2008 D. Richard Hipp) | "you can redistribute it and/or modify the code as you see fit. No attribution is required." Offered under GPL, BSD, MIT or CC0 terms. | per-file git blob SHA-1, checked by `tools/scripts/fetch_corpora.py` | not shipped; fetched for tests |
| Chinook v1.4.5 database | https://github.com/lerocha/chinook-database/releases/download/v1.4.5/Chinook_Sqlite.sqlite | MIT, © 2008-2024 Luis Rocha | `bdf635be69850bd3be09c9a2dbeef7ddfb80036bd3ef3381383cd03b61e4a61a` | not shipped; fetched for tests |
| Northwind database | https://github.com/jpwhite3/northwind-SQLite3 (commit `4f56e7f5906dfd23b25244c5bfe8fb5da6402efd`) | MIT, © 2016 JP White | `2f4f5c68dfcd33ba27373eae48c7a4869800c68095ee0f9f0da494f83382a877` | not shipped; fetched for tests |
| Sakila database | https://github.com/bradleygrant/sakila-sqlite3 (commit `9394b42d13888c3d3d3d56cd7e9c84fadafb71c7`) | BSD-3-Clause, © 2020 Bradley Grant | `88c91a4a1a6b61f9d3f35904c0a173c887b25e73f20c3c2fdb073818c06f4268` | not shipped; fetched for tests |
| SQLite command-line tools and library (test oracle) | https://sqlite.org (precompiled `sqlite-tools` 3.53.4; native library from the `SQLite` 3.53.4 NuGet package) | Public domain (https://sqlite.org/copyright.html) | see `tools/scripts/fetch_oracles.py` | not shipped; run as a test oracle |
