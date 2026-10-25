using System.Diagnostics;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// An isolated temporary git repository. Each test owns its directory, so parallel
/// workers never share state through the file system or the git config.
/// </summary>
internal sealed class CloseoutGitRepo : IDisposable
{
    public string Root { get; }

    public CloseoutGitRepo()
        : this(Path.Combine(Path.GetTempPath(), "kw-closeout-" + Guid.NewGuid().ToString("N")), true)
    {
    }

    private CloseoutGitRepo(string root, bool init)
    {
        Root = root;
        if (init)
        {
            Directory.CreateDirectory(Root);
            Git("init", "-b", "main");
        }
    }

    /// <summary>Wraps an existing directory created outside this helper, such as a clone.</summary>
    internal static CloseoutGitRepo Open(string root)
    {
        return new CloseoutGitRepo(root, false);
    }

    /// <summary>Runs git with argv and never a shell, pinned to local test identity.</summary>
    internal static string RunGit(string workingDirectory, params string[] args)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("user.name=Kyber-Weave Tests");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("user.email=kyber-weave@example.com");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("commit.gpgsign=false");
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with exit {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    public string Git(params string[] args)
    {
        return RunGit(Root, args);
    }

    public CloseoutGitRepo Write(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    /// <summary>Creates a directory for a move target, since git mv will not make one.</summary>
    public CloseoutGitRepo CreateDirectory(string relativeDir)
    {
        Directory.CreateDirectory(Path.Combine(Root, relativeDir.Replace('/', Path.DirectorySeparatorChar)));
        return this;
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-m", message);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }
}

/// <summary>
/// The gate's scope in real repositories: which changes select an archived entry, how
/// the merge base is resolved, and how the gate fails closed. Each test fails while
/// <c>ResolveSinceMergeBase</c> is a stub and the command runs no closeout gate.
/// </summary>
public sealed class ArchiveCloseoutScopeTests
{
    private const string PlanPath = "6-Docs/archive/plans/2026-10-20-widget.md";

    private static string UnclosedPlan(string id)
    {
        return CloseoutCorpus.PlanDocument(id, "draft", "Draft", string.Empty, string.Empty, string.Empty);
    }

    private static IReadOnlyList<ChangedPath> ChangedSince(CloseoutGitRepo repo, string baseRef)
    {
        return ChangedFiles.ResolveSinceMergeBase(repo.Root, baseRef);
    }

    private static DiagnosticReport ValidateGate(CloseoutGitRepo repo, IReadOnlyList<ChangedPath> changes)
    {
        return new ArchiveCloseoutValidator(
                repo.Root,
                KyberWeaveConfig.ProductDefaults,
                () => new DocumentLoader(repo.Root, KyberWeaveConfig.ProductDefaults.Ontology))
            .Validate(new DocumentLoader(repo.Root).Load(), changes);
    }

    private static string FindingText(Diagnostic finding)
    {
        return string.Join(" ", [finding.Message, finding.Hint ?? string.Empty]);
    }

    private static CapturedConsoleExecution<int> RunValidate(string path, bool mergeReady, string? gitBase = null)
    {
        DocsValidateSettings settings = new()
        {
            Path = path,
            Format = "json",
            MergeReady = mergeReady,
            Base = gitBase
        };
        return ProcessConsoleCapture.Run(() => new DocsValidateCommand().Execute(null!, settings));
    }

    /// <summary>
    /// A content-preserving rename into the archive still archives the plan, so the
    /// entry is in scope and fails closeout.
    /// </summary>
    [Fact]
    public void AnUnchangedRenameIntoTheArchiveFailsCloseout()
    {
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write("6-Docs/plans/widget.md", UnclosedPlan("plans/widget"));
        repo.Commit("base with an open plan");
        repo.CreateDirectory("6-Docs/archive/plans");
        repo.Git("mv", "6-Docs/plans/widget.md", PlanPath);

        Diagnostic finding = Assert.Single(ValidateGate(repo, ChangedSince(repo, "HEAD")).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal(PlanPath, finding.FilePath);
    }

    /// <summary>
    /// Adding an archived plan whose body still says Ready isolates C2: every other
    /// condition passes, so the finding names only the live body status.
    /// </summary>
    [Fact]
    public void APlanAddedWhileReadyFailsCloseoutConditionC2()
    {
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument());
        repo.Commit("base corpus");
        repo.Write(PlanPath, CloseoutCorpus.PlanDocument(bodyStatus: "Ready"));

        Diagnostic finding = Assert.Single(ValidateGate(repo, ChangedSince(repo, "HEAD")).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C2", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("Ready", FindingText(finding), StringComparison.Ordinal);
        Assert.DoesNotContain("C1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A RESIDUAL closeout line that tracks no todo isolates C9: the finding names the
    /// condition and the untracked marker.
    /// </summary>
    [Fact]
    public void AnArchivedPlanWithAnUntrackedResidualFailsCloseoutConditionC9()
    {
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument());
        repo.Commit("base corpus");
        repo.Write(PlanPath, CloseoutCorpus.PlanDocument(sections:
            CloseoutCorpus.DecisionsSection("D1")
            + CloseoutCorpus.CloseoutMappingSection(
                ("D1", "[Widget architecture](../../reference/widget.md)"),
                ("H1", "RESIDUAL"))
            + CloseoutCorpus.DesignHarvestSection("H1")));

        Diagnostic finding = Assert.Single(ValidateGate(repo, ChangedSince(repo, "HEAD")).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
        Assert.Contains("C9", FindingText(finding), StringComparison.Ordinal);
        Assert.Contains("RESIDUAL", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// A legacy archive committed long ago is not part of this change, so the gate never
    /// checks it: the single finding belongs to the newly archived entry.
    /// </summary>
    [Fact]
    public void AnUntouchedLegacyArchivedPlanIsNeverChecked()
    {
        const string LegacyPath = "6-Docs/archive/plans/2019-legacy.md";
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(LegacyPath, UnclosedPlan("plans/2019-legacy"));
        repo.Commit("legacy archive from years ago");
        repo.Write(PlanPath, UnclosedPlan("plans/2026-10-20-widget"));

        Diagnostic finding = Assert.Single(ValidateGate(repo, ChangedSince(repo, "HEAD")).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
    }

    /// <summary>
    /// Touching a legacy archive whose harvest document declares no impact does not select
    /// its entry: only the newly archived entry reports.
    /// </summary>
    [Fact]
    public void EditingALegacyArchiveWithoutAnImpactKeyIsNotChecked()
    {
        const string LegacyPath = "6-Docs/archive/plans/2019-legacy.md";
        string legacy = UnclosedPlan("plans/2019-legacy");
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(LegacyPath, legacy);
        repo.Commit("legacy archive from years ago");
        repo.Write(LegacyPath, legacy + "\n<!-- uncommitted touch -->\n");
        repo.Write(PlanPath, UnclosedPlan("plans/2026-10-20-widget"));

        Diagnostic finding = Assert.Single(ValidateGate(repo, ChangedSince(repo, "HEAD")).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
    }

    /// <summary>
    /// Touching an archived entry whose harvest document declares documentation-impact
    /// selects it: a modified closeout is re-checked.
    /// </summary>
    [Fact]
    public void EditingAnArchiveThatDeclaresImpactIsChecked()
    {
        const string SpecPath = "6-Docs/archive/specs/tray/design.md";
        string spec = """
                      ---
                      id: specs/tray/design
                      title: Tray design
                      doc-type: spec
                      status: draft
                      component: MotorcycleRAG API
                      owner: API maintainers
                      last-reviewed: 2026-10-20
                      documentation-impact: extends-component
                      impact-components:
                        - MotorcycleRAG API
                      ---

                      # Tray design

                      **Status:** Draft
                      """;
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(SpecPath, spec);
        repo.Commit("archived spec with an impact declaration");
        repo.Write(SpecPath, spec + "\n<!-- uncommitted touch -->\n");

        Diagnostic finding = Assert.Single(ValidateGate(repo, ChangedSince(repo, "HEAD")).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(SpecPath, finding.FilePath);
    }

    /// <summary>
    /// A deleted archive carries no document to check, so the deletion is ignored while
    /// the newly archived entry still reports.
    /// </summary>
    [Fact]
    public void ADeletedArchiveIsIgnored()
    {
        const string GonePath = "6-Docs/archive/plans/gone.md";
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(GonePath, UnclosedPlan("plans/gone"));
        repo.Commit("archive that will be deleted");
        repo.Git("rm", GonePath);
        repo.Write(PlanPath, UnclosedPlan("plans/2026-10-20-widget"));

        Diagnostic finding = Assert.Single(ValidateGate(repo, ChangedSince(repo, "HEAD")).Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(PlanPath, finding.FilePath);
    }

    /// <summary>
    /// The change is the working tree, not the last commit: uncommitted modifications and
    /// untracked files are both in scope.
    /// </summary>
    [Fact]
    public void UncommittedAndUntrackedMovesAreInScope()
    {
        const string TrackedPath = "6-Docs/reference/tracked.md";
        const string FreshPath = "6-Docs/archive/plans/fresh.md";
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write(TrackedPath, CloseoutCorpus.TargetDocument("reference/tracked"));
        repo.Commit("base corpus");
        repo.CreateDirectory("6-Docs/archive/reference");
        repo.Git("mv", TrackedPath, "6-Docs/archive/reference/tracked.md");
        repo.Write(FreshPath, UnclosedPlan("plans/fresh"));

        List<string> paths = ChangedSince(repo, "HEAD").Select(change => change.Path).ToList();

        Assert.Contains("6-Docs/archive/reference/tracked.md", paths);
        Assert.Contains(FreshPath, paths);
    }

    /// <summary>
    /// Scope resolves the merge base first: an archive committed on the base after the
    /// branch point belongs to someone else's change and stays out of scope.
    /// </summary>
    [Fact]
    public void ChangesOnTheBaseAfterTheBranchPointAreOutOfScope()
    {
        const string BranchPath = "6-Docs/archive/plans/branch.md";
        const string MainSidePath = "6-Docs/archive/plans/main-side.md";
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument());
        repo.Commit("common base");
        repo.Git("checkout", "-b", "feature");
        repo.Write(BranchPath, UnclosedPlan("plans/branch"));
        repo.Commit("archive a plan on the branch");
        repo.Git("checkout", "main");
        repo.Write(MainSidePath, UnclosedPlan("plans/main-side"));
        repo.Commit("archive a plan on main after the branch point");
        repo.Git("checkout", "feature");

        List<string> paths = ChangedSince(repo, "main").Select(change => change.Path).ToList();

        Assert.Contains(BranchPath, paths);
        Assert.DoesNotContain(MainSidePath, paths);
    }

    /// <summary>
    /// With no --base the gate diffs against the remote default branch. The archive commit
    /// sits on top of origin/main locally, so it is invisible against HEAD and visible
    /// against origin/HEAD: only the default finds it.
    /// </summary>
    [Fact]
    public void BaseDefaultsToTheRemoteDefaultBranch()
    {
        string originDir = Path.Combine(Path.GetTempPath(), "kw-closeout-origin-" + Guid.NewGuid().ToString("N"));
        string cloneDir = Path.Combine(Path.GetTempPath(), "kw-closeout-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(originDir);
        try
        {
            CloseoutGitRepo.RunGit(originDir, "init", "--bare");
            CloseoutGitRepo.RunGit(originDir, "symbolic-ref", "HEAD", "refs/heads/main");
            using (CloseoutGitRepo seed = new())
            {
                seed.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
                    .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument());
                seed.Commit("seed the remote");
                seed.Git("remote", "add", "origin", originDir);
                seed.Git("push", "-u", "origin", "main");
            }

            CloseoutGitRepo.RunGit(Path.GetTempPath(), "clone", originDir, cloneDir);
            using (CloseoutGitRepo clone = CloseoutGitRepo.Open(cloneDir))
            {
                clone.Write(PlanPath, UnclosedPlan("plans/2026-10-20-widget"));
                clone.Commit("archive the plan locally, ahead of origin");

                CapturedConsoleExecution<int> execution = RunValidate(cloneDir, true);

                Assert.Equal(1, execution.Result);
                Assert.Contains(
                    ArchiveCloseoutValidator.MissingCloseoutEvidence, execution.Output, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(originDir))
            {
                Directory.Delete(originDir, true);
            }

            if (Directory.Exists(cloneDir))
            {
                Directory.Delete(cloneDir, true);
            }
        }
    }

    /// <summary>
    /// A shallow clone has no merge base for a ref outside its depth, so the gate fails
    /// closed and tells the operator to fetch full history instead of passing silently.
    /// </summary>
    [Fact]
    public void AShallowCloneWithoutAMergeBaseFailsClosedWithAFetchHint()
    {
        string originDir = Path.Combine(Path.GetTempPath(), "kw-closeout-origin-" + Guid.NewGuid().ToString("N"));
        string cloneDir = Path.Combine(Path.GetTempPath(), "kw-closeout-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(originDir);
        try
        {
            CloseoutGitRepo.RunGit(originDir, "init", "--bare");
            CloseoutGitRepo.RunGit(originDir, "symbolic-ref", "HEAD", "refs/heads/main");
            string oldHash;
            using (CloseoutGitRepo seed = new())
            {
                seed.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
                    .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument());
                seed.Commit("first commit");
                seed.Write("6-Docs/reference/extra.md", CloseoutCorpus.TargetDocument("reference/extra"));
                seed.Commit("second commit");
                oldHash = seed.Git("rev-parse", "HEAD~1").Trim();
                seed.Git("branch", "old", oldHash);
                seed.Git("remote", "add", "origin", originDir);
                seed.Git("push", "origin", "main", "old");
            }

            CloseoutGitRepo.RunGit(
                Path.GetTempPath(),
                "clone", "--depth", "1", "--single-branch", "--branch", "main",
                "file://" + originDir, cloneDir);
            using (CloseoutGitRepo clone = CloseoutGitRepo.Open(cloneDir))
            {
                clone.Write(PlanPath, UnclosedPlan("plans/2026-10-20-widget"));

                CapturedConsoleExecution<int> execution = RunValidate(cloneDir, true, oldHash);

                Assert.Equal(1, execution.Result);
                Assert.Contains(
                    "cannot determine what this change archives", execution.Output, StringComparison.Ordinal);
                Assert.Contains("fetch", execution.Output, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (Directory.Exists(originDir))
            {
                Directory.Delete(originDir, true);
            }

            if (Directory.Exists(cloneDir))
            {
                Directory.Delete(cloneDir, true);
            }
        }
    }

    /// <summary>
    /// Outside a repository there is no change to scope, so merge-ready fails closed
    /// instead of reporting a clean run.
    /// </summary>
    [Fact]
    public void ANonGitDirectoryFailsClosedUnderMergeReady()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(PlanPath, UnclosedPlan("plans/2026-10-20-widget"));

        CapturedConsoleExecution<int> execution = RunValidate(fixture.Root, true);

        Assert.Equal(1, execution.Result);
        Assert.Contains("cannot determine what this change archives", execution.Output, StringComparison.Ordinal);
        Assert.Contains("fetch", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The closeout gate is part of merge-ready: an archived plan without evidence passes a
    /// normal validate and fails only when the merge gate runs.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCloseoutEvidenceIsOnlyReportedWithMergeReady(bool mergeReady)
    {
        using CloseoutGitRepo repo = new();
        repo.Write("6-Docs/catalog.md", CloseoutCorpus.Catalog)
            .Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument());
        repo.Commit("base corpus");
        repo.Write(PlanPath, UnclosedPlan("plans/2026-10-20-widget"));

        CapturedConsoleExecution<int> execution = RunValidate(repo.Root, mergeReady);

        if (mergeReady)
        {
            Assert.Equal(1, execution.Result);
            Assert.Contains(
                ArchiveCloseoutValidator.MissingCloseoutEvidence, execution.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(0, execution.Result);
            Assert.DoesNotContain(
                ArchiveCloseoutValidator.MissingCloseoutEvidence, execution.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The docs root is a host setting, not a hard-coded folder: a live Ready body under
    /// a root named handbook resolves its archive container there and fails C2.
    /// </summary>
    [Fact]
    public void DocsRootNamedHandbookResolvesTheArchiveContainer()
    {
        const string HandbookPlan = "handbook/archive/plans/2026-10-20-widget.md";
        OntologyConfig handbook = OntologyConfig.ProductDefaults.WithDocsRoot("handbook");
        KyberWeaveConfig config = KyberWeaveConfig.ProductDefaults.WithOntology(handbook);
        using DocFixture fixture = new();
        fixture.Write("handbook/catalog.md", CloseoutCorpus.Catalog)
            .Write("handbook/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(HandbookPlan, CloseoutCorpus.PlanDocument(bodyStatus: "Ready"));

        DiagnosticReport report = new ArchiveCloseoutValidator(
            fixture.Root,
            config,
            () => new DocumentLoader(fixture.Root, handbook)).Validate(
            new DocumentLoader(fixture.Root, handbook).Load(),
            [new ChangedPath(HandbookPlan, ChangeKind.Added)]);

        Diagnostic finding = Assert.Single(report.Items);

        Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
        Assert.Equal(HandbookPlan, finding.FilePath);
        Assert.Contains("C2", FindingText(finding), StringComparison.Ordinal);
        Assert.DoesNotContain("C1", FindingText(finding), StringComparison.Ordinal);
    }

    /// <summary>
    /// Entries resolve under every configured root, not just the primary one: a live
    /// Ready body under each root fails C2 in its own entry.
    /// </summary>
    [Fact]
    public void MultipleDocsRootsResolveEntriesUnderEveryRoot()
    {
        const string PrimaryPlan = "6-Docs/archive/plans/2026-10-20-widget.md";
        const string HandbookPlan = "handbook/archive/plans/2026-10-20-widget.md";
        OntologyConfig both = OntologyConfig.ProductDefaults.WithDocsRoots(["6-Docs", "handbook"]);
        KyberWeaveConfig config = KyberWeaveConfig.ProductDefaults.WithOntology(both);
        using DocFixture fixture = new DocFixture().WithCatalog();
        fixture.Write("6-Docs/reference/widget.md", CloseoutCorpus.TargetDocument())
            .Write(PrimaryPlan, CloseoutCorpus.PlanDocument(bodyStatus: "Ready"))
            .Write("handbook/reference/widget.md", CloseoutCorpus.TargetDocument("reference/widget-hb"))
            .Write(HandbookPlan, CloseoutCorpus.PlanDocument(
                "plans/2026-10-20-widget-hb", bodyStatus: "Ready"));

        DiagnosticReport report = new ArchiveCloseoutValidator(
            fixture.Root,
            config,
            () => new DocumentLoader(fixture.Root, both)).Validate(
            new DocumentLoader(fixture.Root, both).Load(),
            [
                new ChangedPath(PrimaryPlan, ChangeKind.Added),
                new ChangedPath(HandbookPlan, ChangeKind.Added)
            ]);

        Assert.Equal(2, report.Items.Count);
        List<Diagnostic> findings = report.Items.OrderBy(finding => finding.FilePath, StringComparer.Ordinal).ToList();

        Assert.Equal(
            [PrimaryPlan, HandbookPlan],
            findings.Select(finding => finding.FilePath).ToList());
        foreach (Diagnostic finding in findings)
        {
            Assert.Equal(ArchiveCloseoutValidator.MissingCloseoutEvidence, finding.Code);
            Assert.Contains("C2", FindingText(finding), StringComparison.Ordinal);
            Assert.DoesNotContain("C1", FindingText(finding), StringComparison.Ordinal);
        }
    }
}
