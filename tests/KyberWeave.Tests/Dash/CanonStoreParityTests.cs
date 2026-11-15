using KyberWeave.Core.Dash;
using Xunit;

namespace KyberWeave.Tests.Dash;

/// <summary>
/// Verifies schema, path resolution, zlib compression, and SQLite canonical store parity
/// between the C# <see cref="CanonStore"/> foundation and <c>dash/src/canon/</c>.
/// </summary>
public sealed class CanonStoreParityTests
{
    private static readonly string[] ExpectedCanonicalTables =
    [
        "enriched_logs",
        "execution",
        "finding",
        "harness_rollup",
        "ingest_log",
        "metadata",
        "metric_point",
        "model_context_window_catalog",
        "model_context_window_catalog_vendor",
        "pending_logs",
        "prediction",
        "problems",
        "quarantine",
        "quarantined_logs",
        "record_provenance",
        "records",
        "refresh_run",
        "run",
        "session",
        "source_checkpoint",
        "token_cache",
    ];

    [Fact]
    public void CanonDbPathResolveFollowsExplicitEnvironmentAndHomePrecedence()
    {
        string explicitPath = CanonDbPath.Resolve(
            explicitPath: "/tmp/explicit-canon.db",
            environmentPath: "/tmp/env-canon.db",
            homeDirectory: "/tmp/home");
        Assert.Equal("/tmp/explicit-canon.db", explicitPath);

        string envPath = CanonDbPath.Resolve(
            explicitPath: null,
            environmentPath: "/tmp/env-canon.db",
            homeDirectory: "/tmp/home");
        Assert.Equal("/tmp/env-canon.db", envPath);

        string defaultPath = CanonDbPath.Resolve(
            explicitPath: null,
            environmentPath: string.Empty,
            homeDirectory: "/tmp/home");
        Assert.Equal(Path.Combine("/tmp/home", ".kyberdash", "canon.db"), defaultPath);
    }

    [Fact]
    public void CanonSchemaRefusalMessageAndNormalizersMatchTypeScriptContract()
    {
        Assert.Equal(24, CanonSchema.SchemaVersion);
        Assert.Equal(5, CanonSchema.DetectorVersion);
        Assert.Equal(32 * 1024 * 1024, CanonSchema.WalSizeLimitBytes);
        Assert.Equal("records_write_seq", CanonSchema.RecordsWriteMarkerKey);

        string refusal = CanonSchema.SchemaVersionRefusal("/tmp/canon.db", 25, 24);
        Assert.Equal(
            "canon store at /tmp/canon.db was built with schema version 25, but this build understands version 24; upgrade KyberDash",
            refusal);

        Assert.Null(CanonSchema.NormalizeHistoryWeeks(null));
        Assert.Null(CanonSchema.NormalizeHistoryWeeks(0));
        Assert.Null(CanonSchema.NormalizeHistoryWeeks(-4));
        Assert.Null(CanonSchema.NormalizeHistoryWeeks((long)int.MaxValue + 1));
        Assert.Equal(12, CanonSchema.NormalizeHistoryWeeks(12));

        Assert.Equal("otlp", CanonSchema.NormalizeIngestSource(null));
        Assert.Equal("otlp", CanonSchema.NormalizeIngestSource("   \t\n  "));
        Assert.Equal("claude-code", CanonSchema.NormalizeIngestSource("  claude-code  "));
        string oversized = new('a', 300);
        Assert.Equal(256, CanonSchema.NormalizeIngestSource(oversized).Length);
    }

    [Fact]
    public void CanonCompressedColumnDeflatesOnlyWhenSmallerAndRoundTripsZlib()
    {
        Assert.Null(CanonCompressedColumn.CompressIfSmaller("[]"));

        string largeJson = "{\"text\":\"" + new string('x', 500) + "\"}";
        byte[]? compressed = CanonCompressedColumn.CompressIfSmaller(largeJson);
        Assert.NotNull(compressed);
        Assert.True(compressed.Length < largeJson.Length);
        Assert.Equal(largeJson, CanonCompressedColumn.Decompress(compressed));

        byte[] raw = CanonCompressedColumn.CompressRaw("{}");
        Assert.Equal("{}", CanonCompressedColumn.Decompress(raw));
    }

    [Fact]
    public void CanonStoreInitializesFreshDatabaseAtSchema24WithAllCanonicalTables()
    {
        using TempDirectory directory = new();
        string dbPath = Path.Combine(directory.Path, "nested", "canon.db");

        CanonStore store = new(dbPath);

        Assert.Equal(Path.GetFullPath(dbPath), store.DatabasePath);
        Assert.Equal("24", store.GetMetadata("schema_version"));
        Assert.Equal(5, store.GetDetectorVersion());
        Assert.True(store.HasCurrentDetectorVersion());
        Assert.Equal(0, store.RecordsWriteMarker());
        Assert.Equal(ExpectedCanonicalTables, store.ListTables());
    }

    [Fact]
    public void CanonStoreMetadataCompareAndSetAndWriteMarkerRoundTrip()
    {
        using TempDirectory directory = new();
        string dbPath = Path.Combine(directory.Path, "canon.db");
        CanonStore store = new(dbPath);

        Assert.Null(store.GetMetadata("missing"));
        Assert.True(store.CompareAndSetMetadata("lease", expected: null, value: "owner-a"));
        Assert.False(store.CompareAndSetMetadata("lease", expected: null, value: "owner-b"));
        Assert.True(store.CompareAndSetMetadata("lease", expected: "owner-a", value: "owner-b"));
        Assert.Equal("owner-b", store.GetMetadata("lease"));
        Assert.True(store.CompareAndSetMetadata("lease", expected: "owner-b", value: null));
        Assert.Null(store.GetMetadata("lease"));
        Assert.True(store.CompareAndSetMetadata("lease", expected: null, value: null));

        store.SetMetadata("multiline\tkey", "line1\nline2\t'quoted'");
        Assert.Equal("line1\nline2\t'quoted'", store.GetMetadata("multiline\tkey"));

        store.BumpRecordsWriteMarker();
        store.BumpRecordsWriteMarker();
        Assert.Equal(2, store.RecordsWriteMarker());
    }

    [Fact]
    public void CanonStoreRefusesDatabaseFromNewerSchemaVersionWithExactMessage()
    {
        using TempDirectory directory = new();
        string dbPath = Path.Combine(directory.Path, "canon.db");
        CanonStore store = new(dbPath);
        store.SetMetadata("schema_version", "25");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new CanonStore(dbPath));

        Assert.Equal(
            CanonSchema.SchemaVersionRefusal(Path.GetFullPath(dbPath), 25, 24),
            exception.Message);
    }
}
