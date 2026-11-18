using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;

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

    private static FileRepositoryTreeProbe Probe(DocFixture fixture) =>
        new(fixture.Memory ?? throw NotInMemory(), fixture.Paths, fixture.Environment);

    private static InvalidOperationException NotInMemory() => new("The fixture was not created in memory.");

    private sealed class NoTrackedFiles : IRepositoryFileLister
    {
        public IReadOnlyList<string> List(string repositoryRoot) => [];
    }
}
