using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The git facts over the process and file-system seams: every git answer is scripted and every file lives in
/// memory, so the parsing and the absent-on-failure rules are proven without a repository or a git install.
/// </summary>
public sealed class GitFactsTests
{
    private const string Root = "/repo";

    private readonly ScriptedProcessRunner _git = new();
    private readonly InMemoryFileSystem _files = new();

    private GitFacts Facts() => new(_git, _files);

    /// <summary>Scripts the two reads a change measurement makes before it looks at untracked files.</summary>
    private void ScriptEmptyDiffAndUntracked(params string[] untracked)
    {
        _ = _git
            .Script(
                "git",
                new ProcessRunResult(0, string.Empty, false),
                "-c",
                "core.quotePath=false",
                "diff",
                "--unified=0",
                "--no-color",
                "--no-ext-diff",
                "--src-prefix=a/",
                "--dst-prefix=b/",
                "-M",
                "HEAD")
            .Script(
                "git",
                new ProcessRunResult(0, string.Concat(untracked.Select(path => path + "\0")), false),
                "ls-files",
                "--others",
                "--exclude-standard",
                "--full-name",
                "-z")
            .Script("git", new ProcessRunResult(0, Root + "\n", false), "rev-parse", "--show-toplevel");
    }

    [Fact]
    public void TheDirtySetReportsARenamesNewPathAndNeverTheOldOne()
    {
        _ = _git.Script(
            "git",
            new ProcessRunResult(0, "R  new.txt\0old.txt\0 M keep.txt\0", false),
            "status",
            "--porcelain=v1",
            "-z",
            "--untracked-files=all");

        IReadOnlyDictionary<string, string> statuses = Facts().GetDirtyStatuses(Root);

        Assert.Equal("R ", statuses["new.txt"]);
        Assert.Equal(" M", statuses["keep.txt"]);
        Assert.DoesNotContain("old.txt", statuses.Keys);
        Assert.Equal(Root, _git.Calls[0].WorkingDirectory);
    }

    [Fact]
    public void AMissingGitLeavesEveryFactAbsentRatherThanThrowing()
    {
        _ = _git.Throws("git", new FileNotFoundException("git is not installed."));

        Assert.Null(Facts().GetRoot(Root));
        Assert.Empty(Facts().GetDirtyStatuses(Root));
        Assert.False(Facts().RefExists(Root, "HEAD"));
    }

    [Fact]
    public void AnUntrackedFileIsMeasuredAsOneWholeFileHunkReadThroughTheFileSystem()
    {
        _files.CreateDirectory(Root);
        _files.WriteAllText(Root + "/notes.md", "first\nsecond\n");
        ScriptEmptyDiffAndUntracked("notes.md");

        ChangeSet? change = Facts().GetChangeSet(Root, "HEAD");

        Assert.NotNull(change);
        MeasuredHunk measured = Assert.Single(change.Hunks);
        Assert.Equal("notes.md", measured.Hunk.Path);
        Assert.Equal(["first", "second"], measured.ChangedLines);
    }

    [Fact]
    public void AnUntrackedFileOverTheSizeLimitLeavesTheWholeChangeAbsent()
    {
        _files.CreateDirectory(Root);
        _files.WriteAllText(Root + "/huge.md", new string('a', (1024 * 1024) + 1));
        ScriptEmptyDiffAndUntracked("huge.md");

        Assert.Null(Facts().GetChangeSet(Root, "HEAD"));
    }
}
