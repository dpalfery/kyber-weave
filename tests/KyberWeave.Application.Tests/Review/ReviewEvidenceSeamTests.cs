using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The evidence service writes the diff and the evidence file through the file seam, and the gate report's path in
/// the evidence reads the fully qualified path the path seam gives it rather than a spelling taken from the caller.
/// </summary>
public sealed class ReviewEvidenceSeamTests
{
    private readonly FakeReviewEvidenceStore _files = new();
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work");
    private readonly FakePathResolver _paths = FakePathResolver.OverEmptyFileSystem();

    [Fact]
    public void TheEvidenceNamesTheGateReportByItsFullPath()
    {
        ReviewEvidenceService service = new(new StubGit(), _files, _paths, _environment);

        string path = service.Write("/work/review/cycle-1", "/work/tree", "origin/main", null, "gates.json");

        string evidence = Assert.IsType<string>(_files.TextAt(path));
        Assert.Contains("/work/gates.json", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEvidenceAndTheDiffAreWrittenIntoTheCycleFolder()
    {
        ReviewEvidenceService service = new(new StubGit(), _files, _paths, _environment);

        string path = service.Write("/work/review/cycle-1", "/work/tree", "origin/main", null, "/work/out/gates.json");

        Assert.Equal("/work/review/cycle-1/" + ReviewEvidence.FileName, path);
        Assert.Equal([ReviewEvidence.DiffFileName, ReviewEvidence.FileName], _files.WrittenNames());
        Assert.Equal("diff", _files.TextAt("/work/review/cycle-1/" + ReviewEvidence.DiffFileName));
    }

    /// <summary>Git answers one merge base, one head and an empty change, so the evidence is written without a checkout.</summary>
    private sealed class StubGit : IGitChangeReader
    {
        public IReadOnlyList<string> Resolve(string workingDirectory, string baseRef) => [];

        public string CurrentBranch(string workingDirectory) => "branch";

        public string HeadCommit(string workingDirectory) => "head";

        public string MergeBaseCommit(string workingDirectory, string baseRef) => "base";

        public bool IsAncestorOfHead(string workingDirectory, string commit) => false;

        public IReadOnlyList<string> ResolveSince(string workingDirectory, string commit) => [];

        public IReadOnlyList<ChangedPath> ResolveSinceMergeBase(string workingDirectory, string baseRef) => [];

        public (string MergeBase, string Patch) DiffSinceMergeBase(string workingDirectory, string baseRef) => ("base", "diff");

        public string DiffSince(string workingDirectory, string commit) => "fix";
    }
}
