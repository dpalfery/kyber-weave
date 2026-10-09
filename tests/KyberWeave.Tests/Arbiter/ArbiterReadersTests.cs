using System.Diagnostics;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The Arbiter derives its own facts from git, the gate report, and the caller's
/// identity. None of those sources is guaranteed to exist, so every reader treats an
/// absent source as an absent fact rather than a failure.
/// </summary>
public sealed class ArbiterReadersTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
            owned.Dispose();
    }

    private string Own(string path)
    {
        OwnedDir owned = new(path);
        _owned.Add(owned);
        return path;
    }

    private string NewTempPath()
    {
        TempDirectory tmp = new();
        _owned.Add(tmp);
        return tmp.Path;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        using Process process = new();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (string arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        Assert.True(process.Start(), "git could not be started.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', args)} failed ({process.ExitCode}): {stdout} {stderr}");
    }

    private static string InitRepoWithCommit(string fileName = "tracked.txt", string content = "one\n")
    {
        string dir = Path.Combine(Path.GetTempPath(), "kw-arbiter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        RunGit(dir, "init");
        RunGit(dir, "config", "user.email", "arbiter@example.com");
        RunGit(dir, "config", "user.name", "Arbiter");
        File.WriteAllText(Path.Combine(dir, fileName), content);
        RunGit(dir, "add", fileName);
        RunGit(dir, "commit", "-m", "initial");
        return dir;
    }

    [Fact]
    public void DirtySet_ReportsStagedUnstagedAndUntrackedPaths()
    {
        string repo = InitRepoWithCommit();
        Own(repo);

        File.AppendAllText(Path.Combine(repo, "tracked.txt"), "unstaged\n");
        File.WriteAllText(Path.Combine(repo, "staged.txt"), "staged\n");
        RunGit(repo, "add", "staged.txt");
        File.WriteAllText(Path.Combine(repo, "untracked.txt"), "untracked\n");

        IReadOnlyDictionary<string, string> dirty = GitFacts.GetDirtyStatuses(repo);

        Assert.Contains("tracked.txt", dirty.Keys);
        Assert.Contains("staged.txt", dirty.Keys);
        Assert.Contains("untracked.txt", dirty.Keys);
    }

    [Fact]
    public void ChangedPathsSinceBase_ReportsCommittedChange()
    {
        string repo = InitRepoWithCommit();
        Own(repo);

        string? head = GitFacts.GetHead(repo);
        Assert.False(string.IsNullOrWhiteSpace(head));

        File.WriteAllText(Path.Combine(repo, "second.txt"), "two\n");
        RunGit(repo, "add", "second.txt");
        RunGit(repo, "commit", "-m", "second");

        IReadOnlySet<string> changed = GitFacts.GetChangedPathsSinceBase(repo, head!);

        Assert.Contains("second.txt", changed);
    }

    [Fact]
    public void ChangedPathsSinceBase_ReportsRenameByItsNewPath()
    {
        string repo = InitRepoWithCommit();
        Own(repo);

        string? head = GitFacts.GetHead(repo);
        Assert.False(string.IsNullOrWhiteSpace(head));

        RunGit(repo, "mv", "tracked.txt", "renamed.txt");

        IReadOnlySet<string> changed = GitFacts.GetChangedPathsSinceBase(repo, head!);

        Assert.Contains("renamed.txt", changed);
        Assert.DoesNotContain("tracked.txt", changed);
    }

    [Fact]
    public void SnapshotDiff_ReportsStatusAndBlobChangesHeadMovesAndSkipsArtifacts()
    {
        string repo = InitRepoWithCommit();
        Own(repo);

        GitSnapshot? snapshot = GitFacts.CaptureSnapshot(repo);
        Assert.NotNull(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(snapshot!.Head));

        // Unstaged edit: status and blob both differ from the snapshot.
        File.AppendAllText(Path.Combine(repo, "tracked.txt"), "edited\n");

        // Move HEAD forward with a new commit.
        File.WriteAllText(Path.Combine(repo, "moved.txt"), "moved\n");
        RunGit(repo, "add", "moved.txt");
        RunGit(repo, "commit", "-m", "move head");

        // Gate output must never count as a dispatch change.
        Directory.CreateDirectory(Path.Combine(repo, "artifacts"));
        File.WriteAllText(Path.Combine(repo, "artifacts", "gates.json"), "{}\n");

        IReadOnlySet<string> changed = GitFacts.GetChangedPathsSinceSnapshot(repo, snapshot);

        Assert.Contains("tracked.txt", changed);
        Assert.Contains("moved.txt", changed);
        Assert.DoesNotContain("artifacts/gates.json", changed);
    }

    [Fact]
    public void HunkRanges_ReportsNewSideRanges()
    {
        string repo = InitRepoWithCommit(fileName: "code.txt", content: "a\nb\nc\nd\n");
        Own(repo);

        string? head = GitFacts.GetHead(repo);
        Assert.False(string.IsNullOrWhiteSpace(head));

        File.WriteAllText(Path.Combine(repo, "code.txt"), "a\nB\nc\nd\ne\n");
        RunGit(repo, "add", "code.txt");
        RunGit(repo, "commit", "-m", "edit");

        IReadOnlyList<HunkRange> hunks = GitFacts.GetHunkRanges(repo, head!);

        Assert.NotEmpty(hunks);
        Assert.All(hunks, h => Assert.Equal("code.txt", h.Path));
        Assert.Contains(hunks, h => h.StartLine >= 1 && h.LineCount >= 1);
    }

    [Fact]
    public void OutsideARepositoryOrWithoutGit_EveryGitFactIsAbsentAndNothingThrows()
    {
        string plain = NewTempPath();

        string? root = null;
        string? head = null;
        IReadOnlyDictionary<string, string>? dirty = null;
        IReadOnlyDictionary<string, string>? blobs = null;
        IReadOnlySet<string>? sinceBase = null;
        IReadOnlyList<HunkRange>? hunks = null;
        GitSnapshot? snapshot = null;
        IReadOnlySet<string>? sinceSnapshot = null;
        Exception? failure = null;
        try
        {
            root = GitFacts.GetRoot(plain);
            head = GitFacts.GetHead(plain);
            dirty = GitFacts.GetDirtyStatuses(plain);
            blobs = GitFacts.GetBlobIds(plain, ["nope.txt"]);
            sinceBase = GitFacts.GetChangedPathsSinceBase(plain, "HEAD");
            hunks = GitFacts.GetHunkRanges(plain, "HEAD");
            snapshot = GitFacts.CaptureSnapshot(plain);

            // A missing git binary is the same absent fact, not a crash.
            head = GitFacts.GetHead(plain, gitExecutable: "definitely-missing-git-binary-xyz") ?? head;
            root = GitFacts.GetRoot(plain, gitExecutable: "definitely-missing-git-binary-xyz") ?? root;
            sinceSnapshot = GitFacts.GetChangedPathsSinceSnapshot(
                plain,
                new GitSnapshot("deadbeef", new Dictionary<string, string>(), new Dictionary<string, string>()));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.Null(failure);
        Assert.Null(root);
        Assert.Null(head);
        Assert.NotNull(dirty);
        Assert.Empty(dirty!);
        Assert.NotNull(blobs);
        Assert.Empty(blobs!);
        Assert.NotNull(sinceBase);
        Assert.Empty(sinceBase!);
        Assert.NotNull(hunks);
        Assert.Empty(hunks!);
        Assert.Null(snapshot);
        Assert.NotNull(sinceSnapshot);
        Assert.Empty(sinceSnapshot!);
    }

    [Fact]
    public void CallerResolution_RanksHarnessAboveRenderedAboveHeaderAboveNone()
    {
        Dictionary<string, string?> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PLAN_FILE"] = "docs/plans/p.md",
        };

        CallerResolution none = CallerResolver.Resolve();
        CallerResolution header = CallerResolver.Resolve(headers: headers);
        CallerResolution rendered = CallerResolver.Resolve(renderedCaller: "conductor", headers: headers);
        CallerResolution harness = CallerResolver.Resolve(
            harnessCaller: "product-owner",
            renderedCaller: "conductor",
            headers: headers);

        Assert.Equal(CallerSource.None, none.Source);
        Assert.Null(none.Caller);
        Assert.Equal(CallerSource.Header, header.Source);
        Assert.Equal("conductor", header.Caller);
        Assert.Equal(CallerSource.Rendered, rendered.Source);
        Assert.Equal("conductor", rendered.Caller);
        Assert.Equal(CallerSource.Harness, harness.Source);
        Assert.Equal("product-owner", harness.Caller);
    }

    [Fact]
    public void CallerResolution_InfersCodeReviewerFromLensHeadersAndAssertsServeCaller()
    {
        Dictionary<string, string?> lensHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LENS"] = "di-composition",
        };
        CallerResolution lens = CallerResolver.Resolve(headers: lensHeaders);
        Assert.Equal(CallerSource.Header, lens.Source);
        Assert.Equal("code-reviewer", lens.Caller);

        Dictionary<string, string?> plannerHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TARGET"] = "squad-planner",
        };
        CallerResolution planner = CallerResolver.Resolve(headers: plannerHeaders);
        Assert.Equal(CallerSource.Header, planner.Source);
        Assert.Equal("conductor", planner.Caller);

        Dictionary<string, string?> markerOnly = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MARKER"] = "arbiter",
        };
        CallerResolution unidentified = CallerResolver.Resolve(headers: markerOnly);
        Assert.Equal(CallerSource.None, unidentified.Source);

        CallerResolution asserted = CallerResolver.Resolve(
            harnessCaller: "product-owner",
            assertedCaller: "serve");
        Assert.Equal(CallerSource.Asserted, asserted.Source);
        Assert.Equal("serve", asserted.Caller);
    }

    [Fact]
    public void GateReport_BaseRoundTrips()
    {
        string repo = InitRepoWithCommit();
        Own(repo);

        GateReport report = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "ok")],
            Base: "abc123");

        string dir = Path.Combine(repo, "artifacts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gates.json"), ReviewJson.Write(report));

        GateReport? read = GateReportFacts.TryRead(repo);

        Assert.NotNull(read);
        Assert.Equal("abc123", read!.Base);
        Assert.Equal(GateReport.CurrentSchema, read.Schema);
    }

    [Fact]
    public void GateReport_OldReportsWithoutBaseStillReadAndMissingReportIsAbsent()
    {
        string repo = InitRepoWithCommit();
        Own(repo);

        string dir = Path.Combine(repo, "artifacts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "gates.json"),
            """{"schema":"kyber-weave.review-gates/v1","gates":[]}""");

        GateReport? old = GateReportFacts.TryRead(repo);
        Assert.NotNull(old);
        Assert.Null(old!.Base);

        string empty = NewTempPath();
        Assert.Null(GateReportFacts.TryRead(empty));
    }

    private sealed class OwnedDir(string path) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
