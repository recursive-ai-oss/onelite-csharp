using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneLite.Interop;

/// <summary>The corpus manifest (<c>manifest.json</c>): one entry per case, with how it was made and what is expected of it.</summary>
public sealed class Manifest
{
    /// <summary>Format version of this manifest.</summary>
    [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; set; } = 1;

    /// <summary>The oracle header lines of the run that generated the corpus (versions, compile options).</summary>
    [JsonPropertyName("oracle")] public string Oracle { get; set; } = "";

    /// <summary>The cases, in generation order.</summary>
    [JsonPropertyName("cases")] public List<CorpusCase> Cases { get; set; } = [];
}

/// <summary>One corpus case.</summary>
public sealed class CorpusCase
{
    /// <summary>Unique name; also the base name of the files.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Tags such as <c>matrix</c>, <c>wal</c>, <c>hot-journal</c>, <c>reserved-bytes</c>.</summary>
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];

    /// <summary>How the file was made, in words.</summary>
    [JsonPropertyName("how")] public string How { get; set; } = "";

    /// <summary>Page size.</summary>
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }

    /// <summary>Text encoding.</summary>
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "";

    /// <summary>Auto-vacuum mode: 0 none, 1 full, 2 incremental.</summary>
    [JsonPropertyName("autoVacuum")] public int AutoVacuum { get; set; }

    /// <summary>Reserved bytes per page, from header offset 20.</summary>
    [JsonPropertyName("reservedBytes")] public int ReservedBytes { get; set; }

    /// <summary>Header bytes 18 and 19: file format write and read versions (1 rollback journal, 2 WAL).</summary>
    [JsonPropertyName("formatVersions")] public int[] FormatVersions { get; set; } = [];

    /// <summary>Database file path relative to the corpus directory (the main file; side files follow in <see cref="Files"/>).</summary>
    [JsonPropertyName("db")] public string Db { get; set; } = "";

    /// <summary>Every file of the case, relative to the corpus directory, with size and SHA-256 as generated.</summary>
    [JsonPropertyName("files")] public Dictionary<string, FileInfoEntry> Files { get; set; } = [];

    /// <summary>The SQL script that builds the same logical content, or null when the case needs process behaviour (kills, WAL states).</summary>
    [JsonPropertyName("script")] public string? Script { get; set; }

    /// <summary>True when a writer can rebuild the case from <see cref="Script"/> alone and the result's layout settings are comparable.</summary>
    [JsonPropertyName("writable")] public bool Writable { get; set; }

    /// <summary>Expected <c>.dump</c> (from the oracle, after recovery) relative path and SHA-256.</summary>
    [JsonPropertyName("expectedDump")] public string ExpectedDump { get; set; } = "";

    /// <summary>SHA-256 of the expected dump.</summary>
    [JsonPropertyName("expectedDumpSha256")] public string ExpectedDumpSha256 { get; set; } = "";

    /// <summary>Expected canonical rendering (see the oracle's <c>render</c>) relative path.</summary>
    [JsonPropertyName("expectedRender")] public string ExpectedRender { get; set; } = "";

    /// <summary>SHA-256 of the expected rendering.</summary>
    [JsonPropertyName("expectedRenderSha256")] public string ExpectedRenderSha256 { get; set; } = "";

    /// <summary>Result of <c>PRAGMA integrity_check</c> on a copy, after recovery.</summary>
    [JsonPropertyName("integrityAfterRecovery")] public string IntegrityAfterRecovery { get; set; } = "";

    /// <summary>True when a hot rollback journal is part of the case: opening it has to roll the database back.</summary>
    [JsonPropertyName("hotJournal")] public bool HotJournal { get; set; }

    /// <summary>For a hot journal: whether recovery changed the database file's bytes (the writer had spilled pages).</summary>
    [JsonPropertyName("recoveryChangedDb")] public bool? RecoveryChangedDb { get; set; }

    /// <summary>For WAL cases: the state of the log, in words.</summary>
    [JsonPropertyName("walState")] public string? WalState { get; set; }

    /// <summary>Page-type counts found by the oracle's <c>dbstat</c> (informational coverage).</summary>
    [JsonPropertyName("coverage")] public Dictionary<string, long> Coverage { get; set; } = [];
}

/// <summary>Size and hash of one generated file.</summary>
public sealed class FileInfoEntry
{
    /// <summary>Size in bytes.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>SHA-256, lowercase hex.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}
