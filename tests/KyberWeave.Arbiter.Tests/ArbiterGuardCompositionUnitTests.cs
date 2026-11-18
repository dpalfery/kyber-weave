using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Unit twin of <c>ArbiterGuardCompositionTests</c>. The container registers every adapter with <c>TryAdd</c>, so
/// a test that registers the in-memory file system, path resolver, environment and configuration repository first
/// builds the real guard composition over them: a seam the container forgot fails here, and nothing reads the disk.
/// </summary>
public sealed class ArbiterGuardCompositionUnitTests
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    /// <summary>The guards container resolves both guards and the configuration repository that was registered first.</summary>
    [Fact]
    public void TheGuardsContainerResolvesBothGuardsOverTheRegisteredSeams()
    {
        FakeKyberWeaveConfigRepository configs = new();
        using ServiceProvider services = BuildGuards(configs);

        Assert.NotNull(services.GetRequiredService<IConductorGuard>());
        Assert.NotNull(services.GetRequiredService<IWorkerGuard>());
        Assert.Same(configs, services.GetRequiredService<IKyberWeaveConfigRepository>());
        Assert.NotNull(services.GetRequiredService<IHostConfigLoader>());
        Assert.NotNull(services.GetRequiredService<IGitRepositoryFacts>());
    }

    /// <summary>The host config loader the container supplies reads through the registered configuration repository.</summary>
    [Fact]
    public void TheGuardsHostConfigLoaderReadsThroughTheRegisteredConfigurationRepository()
    {
        const string Root = "/kw/guards-config";
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig(Root, "task-prompt-budget: 41041\n");
        using ServiceProvider services = BuildGuards(configs);

        HostConfigLoadResult loaded = services.GetRequiredService<IHostConfigLoader>().TryLoad(Root);

        Assert.True(loaded.Success, loaded.Error);
        Assert.Equal(41041, loaded.Config!.TaskPromptBudget);
    }

    /// <summary>The state factory, tree probe and run store resolve over the in-memory file system.</summary>
    [Fact]
    public void TheGuardsContainerResolvesStateTreeProbeAndRunRecordsOverTheFileSystemSeam()
    {
        string directory = Path.GetFullPath("/kw/guards/arbiter-state");
        string home = Path.GetFullPath("/kw/guards/runs");
        _files.CreateDirectory(home);
        using ServiceProvider services = BuildGuards(new FakeKyberWeaveConfigRepository());

        IArbiterStateFactory state = services.GetRequiredService<IArbiterStateFactory>();
        Assert.Empty(state.OpenLedger(directory).ReadAll());
        Assert.Empty(state.OpenDecisionLog(directory).ReadAll());
        Assert.Empty(state.OpenRunStore(directory).ReadFindings("REVIEW-1"));

        IRepositoryTreeProbe probe = services.GetRequiredService<IRepositoryTreeProbe>();
        Assert.IsType<FileRepositoryTreeProbe>(probe);
        Assert.True(probe.ProbeRoot(home).Exists);
        Assert.False(probe.ProbeRoot(Path.GetFullPath("/kw/guards/no-such-root")).Exists);

        IRunStateStore store = services.GetRequiredService<IRunStateStore>();
        store.Write(home, "claude/fix/x", "origin/main");
        Assert.True(store.TryReadBaseRef(home, "claude/fix/x", out string? baseRef));
        Assert.Equal("origin/main", baseRef);
    }

    /// <summary>The hook-host container resolves the hook host and the fact readers over the same seams.</summary>
    [Fact]
    public void TheHookHostContainerResolvesTheHookHostAndFactReadersOverTheRegisteredSeams()
    {
        ServiceCollection collection = Seams(new FakeKyberWeaveConfigRepository());
        _ = collection.AddArbiterHost();
        using ServiceProvider services = collection.BuildServiceProvider();

        _ = Assert.IsAssignableFrom<IArbiterHookHost>(services.GetRequiredService<IArbiterHookHost>());
        _ = Assert.IsType<FileTextFacts>(services.GetRequiredService<IFileTextFacts>());
        _ = Assert.IsType<ClaudeHookCoverage>(services.GetRequiredService<IClaudeHookCoverage>());
        _ = Assert.IsType<GateReportFacts>(services.GetRequiredService<IGateReportFacts>());
    }

    private ServiceProvider BuildGuards(FakeKyberWeaveConfigRepository configs)
    {
        ServiceCollection collection = Seams(configs);
        _ = ArbiterContainer.AddArbiterGuards(collection);
        return collection.BuildServiceProvider();
    }

    private ServiceCollection Seams(FakeKyberWeaveConfigRepository configs)
    {
        ServiceCollection collection = new();
        collection.AddSingleton<IWritableFileSystem>(_files);
        collection.AddSingleton<IFileSystem>(_files);
        collection.AddSingleton<IPathResolver>(new FakePathResolver(_files));
        collection.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(currentDirectory: _files.Root));
        collection.AddSingleton<IKyberWeaveConfigRepository>(configs);
        return collection;
    }
}
