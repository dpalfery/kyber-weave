using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// The harness-collection world must serve its zero-record harness (<c>droid</c>) as honest-empty:
/// the harness endpoint answers with an empty summary and the runs endpoint answers an empty list,
/// instead of the missing-rollup 404 that renders the NotFound panel.
/// </summary>
public class E2eSeederWorldTests
{
    /// <summary>
    /// Seeding the harness-collection world leaves <c>droid</c> with no runs but servable: the
    /// harness lookup returns a summary and the runs lookup returns an empty list, while a
    /// genuinely unknown harness still answers null.
    /// </summary>
    [Fact]
    public void HarnessCollectionWorldServesTheZeroRecordHarnessAsHonestEmpty()
    {
        string repoRoot = LocateRepoRoot();
        using TempDirectory directory = new();
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonSessionProjectorEngine projector = new(store, store, store, pricing, new FakeTextTokenizer());
        CanonRollupsEngine rollups = new(store, store, pricing);
        CanonProjectionService projection = new(store, store, store, projector, rollups);
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());
        E2eSeederService seeder = new(store, store, store, ingestion, projection);

        Assert.Equal(
            0,
            seeder.SeedWorld(E2eSeederService.HarnessCollectionWorld, directory.Combine("world.db"), repoRoot));

        KyberBridgeEngine bridge = new(store, store, store, pricing, projector, rollups);

        JsonElement? droid = bridge.GetHarnessById("droid");
        Assert.NotNull(droid);
        Assert.Equal("droid", droid.Value.GetProperty("harness").GetString());
        Assert.Equal(0, droid.Value.GetProperty("runCount").GetInt32());
        JsonElement pressureTag = droid.Value.GetProperty("measurability").GetProperty("context_pressure");
        Assert.Equal("not_measurable", pressureTag.GetProperty("status").GetString());
        Assert.Contains("droid", pressureTag.GetProperty("reason").GetString(), StringComparison.Ordinal);
        JsonElement runs = bridge.GetRuns("droid");
        Assert.Equal(0, runs.GetProperty("runs").GetArrayLength());
        Assert.Null(bridge.GetHarnessById("no-such-harness"));
    }

    /// <summary>
    /// A seeded harness-collection store is already projection-current: a request-time read pass
    /// finds nothing to rebuild, so the explicit seeded runs survive instead of being replaced by
    /// derived session-fallback runs.
    /// </summary>
    [Fact]
    public void HarnessCollectionWorldSeedsAProjectionCurrentStoreThatReadsDoNotRebuild()
    {
        string repoRoot = LocateRepoRoot();
        using TempDirectory directory = new();
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonSessionProjectorEngine projector = new(store, store, store, pricing, new FakeTextTokenizer());
        CanonRollupsEngine rollups = new(store, store, pricing);
        CanonProjectionService projection = new(store, store, store, projector, rollups);
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());
        E2eSeederService seeder = new(store, store, store, ingestion, projection);

        Assert.Equal(
            0,
            seeder.SeedWorld(E2eSeederService.HarnessCollectionWorld, directory.Combine("world.db"), repoRoot));

        Assert.True(projector.IsProjectionCurrentForReads());
        Assert.True(projector.IsProjectionCurrent());
        Assert.Null(store.GetProjectionDirtyState());

        KyberBridgeEngine bridge = new(store, store, store, pricing, projector, rollups);
        JsonElement runs = bridge.GetRuns("opencode");
        Assert.Contains(
            runs.GetProperty("runs").EnumerateArray(),
            run => run.GetProperty("runId").GetString() == "hc-honest-run-1");
    }

    /// <summary>
    /// The seeder stamps its store as a completed full pass does: the dirty state is cleared
    /// before the watermark moves, so a crash between the two leaves the projection out of date
    /// rather than current over dirty sessions, and the detector version is current.
    /// </summary>
    [Fact]
    public void HarnessCollectionWorldStampsDirtyStateBeforeTheWatermarkAndTheDetectorVersion()
    {
        string repoRoot = LocateRepoRoot();
        using TempDirectory directory = new();
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonSessionProjectorEngine projector = new(store, store, store, pricing, new FakeTextTokenizer());
        CanonRollupsEngine rollups = new(store, store, pricing);
        CanonProjectionService projection = new(store, store, store, projector, rollups);
        // The seed's first projection pass heals a stale detector, so the detector is made stale
        // again right after that pass. Only the seeder's own final stamp can make it current.
        DetectorStaleAfterFirstPass projectionAfterFirstPass = new(projection, store);
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());
        E2eSeederService seeder = new(store, store, store, ingestion, projectionAfterFirstPass);
        store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion, SqliteRelationalStore.DetectorVersion - 1));
        List<string> writeBacks = [];
        store.BeforeProjectionWriteBack = writeBacks.Add;

        Assert.Equal(
            0,
            seeder.SeedWorld(E2eSeederService.HarnessCollectionWorld, directory.Combine("world.db"), repoRoot));

        Assert.Equal(SqliteRelationalStore.DetectorVersion, store.GetStoreState()?.DetectorVersion);
        Assert.Equal(
            [
                nameof(InMemoryRelationalStore.ClearProjectionDirtyState),
                nameof(InMemoryRelationalStore.SetProjectionState),
                nameof(InMemoryRelationalStore.SetStoreState),
            ],
            writeBacks.TakeLast(3));
        Assert.True(projector.IsProjectionCurrent());
    }

    /// <summary>
    /// Projection service that makes the store's detector version stale again right after the first
    /// incremental pass returns, so the seeder's final stamp is the only step left that can heal it.
    /// </summary>
    private sealed class DetectorStaleAfterFirstPass : ICanonProjectionService
    {
        private readonly ICanonProjectionService inner;
        private readonly InMemoryRelationalStore store;
        private bool staled;

        /// <param name="inner">Real projection service the seeder calls through.</param>
        /// <param name="store">Store whose detector version is made stale.</param>
        public DetectorStaleAfterFirstPass(ICanonProjectionService inner, InMemoryRelationalStore store)
        {
            this.inner = inner;
            this.store = store;
        }

        /// <inheritdoc />
        public ProjectionPassReport ProjectIncremental()
        {
            ProjectionPassReport report = inner.ProjectIncremental();
            if (!staled)
            {
                staled = true;
                store.SetStoreState(new StoreStateView(SqliteRelationalStore.SchemaVersion, SqliteRelationalStore.DetectorVersion - 1));
            }

            return report;
        }

        /// <inheritdoc />
        public IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnesses() => inner.ProjectRunsAndHarnesses();

        /// <inheritdoc />
        public (ProjectionPassReport Projection, IReadOnlyList<HarnessRollupRow> Rollups) ProjectAll() => inner.ProjectAll();
    }

    /// <summary>Walks up from the test assembly until the fixture the seeder replays is found.</summary>
    /// <returns>The repository root holding the seeder fixtures.</returns>
    private static string LocateRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        for (int level = 0; level < 12 && current is not null; level++)
        {
            if (File.Exists(Path.Combine(current, "dash", "e2e", "fixtures", "opencode", "otlp-v3-runA.consent-on.json")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException(
            "Cannot locate the repository root: the harness-collection fixture is missing above " + AppContext.BaseDirectory);
    }
}
