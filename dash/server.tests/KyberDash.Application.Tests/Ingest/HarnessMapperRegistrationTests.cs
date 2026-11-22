using KyberDash.Application;
using KyberDash.Application.Tests.Analytics;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Antigravity;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Harnesses.Codex;
using KyberDash.Data.Harnesses.Droid;
using KyberDash.Data.Harnesses.Pi;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies the composition root registers every harness mapper so the refresh and ingestion
/// engines resolve a non-empty production pipeline, with a mapper resolving for each evidenced
/// harness id through the same lookup rules the engines build.
/// </summary>
public class HarnessMapperRegistrationTests
{
    /// <summary>Every evidenced recoverable harness id the composition must resolve a mapper for.</summary>
    public static TheoryData<string> EvidencedHarnessIds()
    {
        TheoryData<string> data = new();
        foreach (string harnessId in AllHarnessFixtureSet.EvidencedRecoverableHarnessIds)
        {
            data.Add(harnessId);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EvidencedHarnessIds))]
    public void AddDashApplication_ResolvesAMapperForEveryEvidencedHarness(string harnessId)
    {
        using ServiceProvider provider = Composition();

        Dictionary<string, IHarnessMapper> lookup = HarnessMapperLookup.Build(provider.GetServices<IHarnessMapper>());

        IHarnessMapper? resolved = HarnessMapperLookup.Resolve(lookup, harnessId);
        Assert.True(resolved is not null, $"no IHarnessMapper resolves for evidenced harness '{harnessId}'.");
    }

    [Fact]
    public void AddDashApplication_RegistersTheFullProductionPipeline()
    {
        using ServiceProvider provider = Composition();

        // The 22 mapper implementations plus the three family-member aliases (antigravity-cli,
        // cline-cli, kilo-vscode-legacy).
        Assert.Equal(25, provider.GetServices<IHarnessMapper>().Count());
    }

    [Fact]
    public void AddDashApplication_LocalTranscriptMappersOwnTheSharedHarnessKeys()
    {
        using ServiceProvider provider = Composition();
        Dictionary<string, IHarnessMapper> lookup = HarnessMapperLookup.Build(provider.GetServices<IHarnessMapper>());

        Assert.True(lookup.TryGetValue("claude-code", out IHarnessMapper? claude));
        Assert.IsType<ClaudeTranscriptMapper>(claude);
        Assert.True(lookup.TryGetValue("codex-cli", out IHarnessMapper? codex));
        Assert.IsType<CodexRolloutMapper>(codex);
        Assert.True(lookup.TryGetValue("antigravity", out IHarnessMapper? antigravity));
        Assert.IsType<AntigravityTranscriptMapper>(antigravity);
        Assert.True(lookup.TryGetValue("droid", out IHarnessMapper? droid));
        Assert.IsType<DroidSessionMapper>(droid);
        Assert.True(lookup.TryGetValue("pi", out IHarnessMapper? pi));
        Assert.IsType<PiMapper>(pi);
    }

    private static ServiceProvider Composition() =>
        new ServiceCollection().AddDashApplication().BuildServiceProvider();
}
