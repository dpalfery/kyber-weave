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
