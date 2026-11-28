using KyberWeave.Application;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests;

/// <summary>
/// The configuration repository and the docs scaffolder over a <see cref="DocFixture"/> built in memory, for a unit
/// test that arranges a host configuration file and asks what the product reads or rewrites. Nothing touches the disk.
/// </summary>
internal static class DocsMemoryPortsC
{
    /// <summary>The shipped configuration repository over the fixture's in-memory file system.</summary>
    /// <param name="fixture">An in-memory fixture.</param>
    public static FileKyberWeaveConfigRepository Config(this DocFixture fixture) =>
        new(Probe(fixture), fixture.Memory ?? throw NotInMemory(), fixture.Paths, fixture.Environment);

    /// <summary>The scaffolder over the fixture's in-memory file system, with no tracked files and no embedded templates.</summary>
    /// <param name="fixture">An in-memory fixture.</param>
    public static DocsScaffolder Scaffolder(this DocFixture fixture) =>
        new(
            fixture.Store,
            fixture.Config(),
            new NoTrackedFiles(),
            Probe(fixture),
            fixture.Paths,
            fixture.Environment,
            TimeProvider.System,
            new KyberStandardsTemplateReader(new FakeEmbeddedResources()));

    /// <summary>Merges the <c>ontology:</c> section of the config file at <paramref name="yamlPath"/> over <paramref name="defaults"/>.</summary>
    /// <param name="fixture">An in-memory fixture.</param>
    /// <param name="defaults">The ontology the file is merged over.</param>
    /// <param name="yamlPath">The config file.</param>
    public static OntologyConfig LoadOntologyMerged(this DocFixture fixture, OntologyConfig defaults, string yamlPath) =>
        OntologyConfigLoader.Merge(defaults, fixture.Config().ReadDocument(yamlPath).Ontology);

    /// <summary>
    /// The container the MCP documentation tools resolve their collaborators from, with every file, path, environment and
    /// process port replaced by the fixture's in-memory file system and scripted process seams, so a tool answers over the
    /// in-memory corpus and starts nothing. A process the test did not script fails loudly.
    /// </summary>
    /// <param name="fixture">An in-memory fixture.</param>
    public static IServiceProvider McpServices(this DocFixture fixture)
    {
        InMemoryFileSystem memory = fixture.Memory ?? throw NotInMemory();
        ScriptedProcessRunner runner = new();
        _ = runner.Script("git", new ProcessRunResult(128, "fatal: not a git repository", TimedOut: false));
        ScriptedProcessExecutor executor = new(_ => throw new UnscriptedProcessCallException("process"));
        ServiceCollection services = new();
        services.AddSingleton<IWritableFileSystem>(memory);
        services.AddSingleton<IFileSystem>(memory);
        services.AddSingleton(fixture.Store);
        services.AddSingleton(fixture.Paths);
        services.AddSingleton(fixture.Environment);
        services.AddSingleton<IProcessRunner>(runner);
        services.AddSingleton<IBoundedProcessExecutor>(executor);
        return services.AddWeaveApplication().BuildServiceProvider();
    }

    private static FileRepositoryTreeProbe Probe(DocFixture fixture) =>
        new(fixture.Memory ?? throw NotInMemory(), fixture.Paths, fixture.Environment);

    private static InvalidOperationException NotInMemory() => new("The fixture was not created in memory.");

    private sealed class NoTrackedFiles : IRepositoryFileLister
    {
        public IReadOnlyList<string> List(string repositoryRoot) => [];

        public Task<IReadOnlyList<string>> ListAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
