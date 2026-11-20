using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>Where the brief builder looks for reference files, over the in-memory file system and a fake working directory.</summary>
public sealed class SquadBriefBuilderTests
{
    private const string Repo = "/kw/work/repo";

    private readonly InMemoryFileSystem _files = new();
    private readonly RecordingBriefs _briefs = new();
    private readonly SquadBriefBuilder _builder;
    private readonly string _agent = BriefMapLoader.Load().Agents.First();

    public SquadBriefBuilderTests() =>
        _builder = new SquadBriefBuilder(
            _briefs,
            _files,
            new InMemoryRepositoryFileStore(_files),
            new UnusedRoots(),
            new NoSource(),
            new FakeProcessEnvironment(currentDirectory: Repo),
            new FakePathResolver(_files));

    private SquadBriefRequest Request(string from) =>
        new(_agent, null, null, null, null, null, from, Repo, [], null);

    [Fact]
    public void ARelativeReferenceDirectoryIsResolvedAgainstTheRepositoryRoot()
    {
        _files.CreateDirectory("/kw/work/repo/refs");

        bool built = _builder.TryBuild(Request("refs/../refs"), out _, out _);

        Assert.False(built);
        Assert.Equal("/kw/work/repo/refs", _briefs.ReferencesDirectory);
    }

    [Fact]
    public void AMissingReferenceDirectoryIsRefusedByItsFullPath()
    {
        bool built = _builder.TryBuild(Request("nowhere"), out _, out string refusal);

        Assert.False(built);
        Assert.Contains("'/kw/work/repo/nowhere' does not exist", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGivenAutonomyWinsAndTheConfigurationIsTheLastSource()
    {
        // The given value wins over both the registry and the configuration, read case- and space-insensitively.
        _files.CreateDirectory(Repo);
        _files.WriteAllText(Repo + "/AGENTS.md", "- **<autonomy>**: `supervised`\n");
        KyberWeaveConfig configured = new()
        {
            Arbiter = KyberWeave.Core.Arbiter.ArbiterConfigDefaults.Product,
            ConfigReg = new KyberWeave.Core.Configuration.ConfigRegConfig { Autonomy = "supervised" }
        };

        Assert.Equal("autonomous", _builder.ResolveAutonomy(Repo, "  AUTONOMOUS ", configured));

        // With nothing given and no registry line, the configuration supplies the value.
        _files.DeleteFile(Repo + "/AGENTS.md");
        KyberWeaveConfig autonomous = new()
        {
            Arbiter = KyberWeave.Core.Arbiter.ArbiterConfigDefaults.Product,
            ConfigReg = new KyberWeave.Core.Configuration.ConfigRegConfig { Autonomy = "autonomous" }
        };

        Assert.Equal("autonomous", _builder.ResolveAutonomy(Repo, null, autonomous));

        // With nothing set anywhere, the run has none and reads the default.
        Assert.Null(_builder.ResolveAutonomy(Repo, null, KyberWeaveConfigDefaults.Product));
    }

    [Fact]
    public void TheRepositoryRegistryAutonomyIsUsedWhenNoneIsGiven()
    {
        _files.CreateDirectory(Repo);
        _files.WriteAllText(Repo + "/AGENTS.md", "- **<autonomy>**: `autonomous`\n");

        Assert.Equal("autonomous", _builder.ResolveAutonomy(Repo, null, KyberWeaveConfigDefaults.Product));
    }

    private sealed class RecordingBriefs : ISquadBriefService
    {
        public string? ReferencesDirectory { get; private set; }

        public BriefResult Assemble(BriefMap map, string agent, BriefFacts facts, string referencesDirectory)
        {
            ReferencesDirectory = referencesDirectory;
            throw new BriefMapException("stopped after the directory was chosen");
        }

        public string ResolveFile(string referencesDirectory, string file) => throw new NotSupportedException();
    }

    private sealed class UnusedRoots : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => throw new NotSupportedException();
    }

    private sealed class NoSource : ISquadSourceLocator
    {
        public string? Resolve(string workingDirectory) => null;
    }
}
