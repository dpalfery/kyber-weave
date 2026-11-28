using KyberWeave.Contracts.Repositories;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Mcp;

/// <summary>
/// The provenance line over the path and environment seams: the root it names is resolved against the
/// environment's current directory, and the probe is asked about that resolved root.
/// </summary>
public sealed class CorpusProvenanceServiceTests
{
    private readonly RecordingProbe _probe = new();

    private CorpusProvenanceService Service(string currentDirectory) =>
        new(_probe, FakePathResolver.OverEmptyFileSystem(), new FakeProcessEnvironment(currentDirectory));

    [Fact]
    public async Task ALineNamesTheBranchRepositoryRevisionAndDocumentCountOfTheResolvedRoot()
    {
        string line = await Service("/cp-a/work").LineAsync("/cp-a/repo", 7, corpusStamp: 1);

        Assert.Equal(
            "provenance: root=" + _probe.LastRoot + " branch=topic repo=/cp-a/repo/.git rev=abc1234 dirty=no documents=7",
            line);
        Assert.EndsWith("/cp-a/repo", _probe.LastRoot.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARelativeRootIsResolvedAgainstTheEnvironmentsCurrentDirectoryNotTheProcess()
    {
        _ = await Service("/cp-b/work").LineAsync("repo", documentCount: null, corpusStamp: 1);

        Assert.EndsWith("/cp-b/work/repo", _probe.LastRoot.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentCountThatIsNotKnownIsReportedAsUnavailable()
    {
        string line = await Service("/cp-c/work").LineAsync("/cp-c/repo", documentCount: null, corpusStamp: 1);

        Assert.EndsWith(" documents=" + CorpusProvenance.Unavailable, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondLineForTheSameStampsDoesNotReadTheCheckoutAgain()
    {
        CorpusProvenanceService service = Service("/cp-d/work");

        _ = await service.LineAsync("/cp-d/repo", 1, corpusStamp: 5);
        _ = await service.LineAsync("/cp-d/repo", 1, corpusStamp: 5);

        Assert.Equal(1, _probe.LocationReads);
    }

    [Fact]
    public async Task AChangedCorpusStampReadsTheCheckoutAgain()
    {
        CorpusProvenanceService service = Service("/cp-e/work");

        _ = await service.LineAsync("/cp-e/repo", 1, corpusStamp: 5);
        _ = await service.LineAsync("/cp-e/repo", 1, corpusStamp: 6);

        Assert.Equal(2, _probe.LocationReads);
    }

    private sealed class RecordingProbe : IGitCheckoutProbe
    {
        public string LastRoot { get; private set; } = string.Empty;

        public int LocationReads { get; private set; }

        public long Fingerprint(string root)
        {
            LastRoot = root;
            return 42;
        }

        public GitCheckoutLocation ReadLocation(string root)
        {
            LocationReads++;
            return new GitCheckoutLocation("topic", root + "/.git");
        }

        public Task<GitCheckoutRevision> ReadRevisionAsync(string root, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitCheckoutRevision("abc1234", "no"));
    }
}
