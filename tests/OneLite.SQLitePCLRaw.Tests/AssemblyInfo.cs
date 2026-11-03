// SQLitePCLRaw has one process-wide provider; the tests switch it between the native library and OneLite, so they run one at a time.
[assembly: Xunit.CollectionBehavior(Xunit.CollectionBehavior.CollectionPerAssembly)]
