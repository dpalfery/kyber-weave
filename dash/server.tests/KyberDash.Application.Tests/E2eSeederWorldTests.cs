using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector.Sources;
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
        CanonSessionProjectorEngine projector = new(store, store, store, pricing);
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
