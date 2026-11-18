using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The guards container is built on its own by the hook processes, so it must register every adapter the guards read
/// through. The host-config adapter reads the configuration repository: a missing registration does not fail the
/// build, it fails at resolve time inside the guard's catch-all, which denies every conductor call and allows every
/// worker call.
/// </summary>
public sealed class ArbiterGuardCompositionTests
{
    [Fact]
    public void TheGuardsContainerResolvesTheConfigurationRepositoryTheGuardsReadThrough()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        // The type is the proof: building the shipped repository resolves the tree probe, the file system, the path
        // resolver and the process environment, so a seam this container forgot fails here instead of inside a
        // guard's catch-all, which would deny every conductor call.
        Assert.IsType<FileKyberWeaveConfigRepository>(services.GetRequiredService<IKyberWeaveConfigRepository>());
        Assert.NotNull(services.GetRequiredService<IHostConfigLoader>());
    }

    [Fact]
    public void TheGuardsHostConfigLoaderReadsThroughTheInjectedConfigurationRepository()
    {
        const string Root = "/kw/guards-config";
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig(Root, "task-prompt-budget: 41041\n");
        ServiceCollection guarded = new();
        guarded.AddSingleton<IKyberWeaveConfigRepository>(configs);
        _ = ArbiterContainer.AddArbiterGuards(guarded);

        using ServiceProvider services = guarded.BuildServiceProvider();
        HostConfigLoadResult loaded = services.GetRequiredService<IHostConfigLoader>().TryLoad(Root);

        Assert.True(loaded.Success, loaded.Error);
        Assert.Equal(41041, loaded.Config!.TaskPromptBudget);
    }

    [Fact]
    public void TheGuardsContainerResolvesBothGuards()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        Assert.NotNull(services.GetRequiredService<IConductorGuard>());
        Assert.NotNull(services.GetRequiredService<IWorkerGuard>());
    }

    [Fact]
    public void TheGuardsContainerResolvesTheGitFactsThroughTheProcessAndFileSystemSeams()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        IGitRepositoryFacts facts = services.GetRequiredService<IGitRepositoryFacts>();

        Assert.NotNull(facts);
    }

    [Fact]
    public void TheGuardsContainerResolvesTheArbiterStateFactoryOverTheLogAndFileSystemSeams()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();
        string directory = Path.Combine(AppContext.BaseDirectory, "no-such-arbiter-state");

        IArbiterStateFactory state = services.GetRequiredService<IArbiterStateFactory>();

        Assert.Empty(state.OpenLedger(directory).ReadAll());
        Assert.Empty(state.OpenDecisionLog(directory).ReadAll());
        Assert.Empty(state.OpenRunStore(directory).ReadFindings("REVIEW-1"));
    }

    [Fact]
    public void TheGuardsContainerResolvesTheTreeProbeThroughTheFileSystemSeam()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();

        IRepositoryTreeProbe probe = services.GetRequiredService<IRepositoryTreeProbe>();

        Assert.IsType<FileRepositoryTreeProbe>(probe);
        Assert.True(probe.ProbeRoot(AppContext.BaseDirectory).Exists);
        Assert.False(probe.ProbeRoot(Path.Combine(AppContext.BaseDirectory, "no-such-root")).Exists);
    }

    [Fact]
    public void TheGuardsContainerResolvesTheRunRecordsAndTheCheckoutGuardOverTheFileSystemSeam()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.CreateGuards();
        string home = Directory.CreateTempSubdirectory("kw-guards-runs-").FullName;
        try
        {
            IRunStateStore store = services.GetRequiredService<IRunStateStore>();
            store.Write(home, "claude/fix/x", "origin/main");

            Assert.True(store.TryReadBaseRef(home, "claude/fix/x", out string? baseRef));
            Assert.Equal("origin/main", baseRef);
            Assert.Equal("/kw-absent/repo", services.GetRequiredService<IRunCheckout>().CanonicalCheckoutOf("/kw-absent/repo/.git"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void TheHookHostContainerResolvesTheServicesThatReadThroughTheFactsTheGuardsDoNotNeed()
    {
        using ServiceProvider services = (ServiceProvider)ArbiterContainer.Create();

        _ = Assert.IsAssignableFrom<IArbiterHookHost>(services.GetRequiredService<IArbiterHookHost>());
        _ = Assert.IsType<FileTextFacts>(services.GetRequiredService<IFileTextFacts>());
        _ = Assert.IsType<ClaudeHookCoverage>(services.GetRequiredService<IClaudeHookCoverage>());
        _ = Assert.IsType<GateReportFacts>(services.GetRequiredService<IGateReportFacts>());
        _ = Assert.IsType<FileArbiterUserSettings>(services.GetRequiredService<IArbiterUserSettings>());
        _ = Assert.IsType<FileArbiterModelTargetsReader>(services.GetRequiredService<IArbiterModelTargetsReader>());
    }
}
