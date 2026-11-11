using System.Text;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The review facts that describe what a change touched: its hunks, its renames, its
/// comment-only and no-code-change paths, and the lines a finding names inside its hunks.
/// Every fact is measured from a real repository through the real hook path, because a
/// hand-fed diff proved nothing in the earlier review rounds.
/// </summary>
/// <remarks>
/// A hunk list shortened or emptied where git reported a change would let a lens judge a
/// partial change as the whole one, so the list is asserted in full. The absence of a change
/// is asserted as absence, never as an empty list: an empty list reads as "measured, nothing
/// changed", which is a claim the harness cannot make when the base is unreadable.
/// </remarks>
public sealed class ArbiterChangedHunksTests : IDisposable
{
    private const string ChangedHunks = "review.changed-hunks";
    private const string RenameOnly = "review.rename-only-paths";
    private const string CommentOnly = "review.comment-only-paths";
    private const string NoCodeChange = "review.no-code-change-paths";
    private const string FindingChangedLines = "finding.changed-lines";

    private static readonly string[] ExpectedRenameOnly = new[] { "src/New.cs" };
    private static readonly string[] ExpectedCommentOnly = new[] { "src/Notes.cs" };
    private static readonly string[] ExpectedNoCodeChange = new[] { "docs/guide.md", "src/New.cs", "src/Notes.cs" };
    private static readonly int[] ExpectedFindingChangedLines = new[] { 3, 7, 8 };

    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly TempDirectory _home = new();
    private readonly TempDirectory _arbiter = new();
    private readonly TempDirectory _outside = new();

    public void Dispose()
    {
        _probe.Dispose();
        _home.Dispose();
        _arbiter.Dispose();
        _outside.Dispose();
    }

    /// <summary>
    /// Each hunk carries its old and new ranges exactly as git reports them with zero context,
    /// so a lens can cite the line a change occupies on either side of it.
    /// </summary>
    [Fact]
    public void ChangedHunksListEveryHunkWithItsOldAndNewRanges()
    {
        CommitFile("src/Service.cs", NumberedLines(10));
        string baseRef = Head();
        Write("src/Service.cs", Edit(NumberedLines(10), (3, "changed 3"), (7, "changed 7"), (8, "changed 8")));

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.NotNull(hunks);
        Assert.Equal(
            new[]
            {
                new ChangedHunk("src/Service.cs", 3, 1, 3, 1),
                new ChangedHunk("src/Service.cs", 7, 2, 7, 2),
            },
            hunks);
    }

    /// <summary>
    /// A pure deletion reports a zero new-side count and a pure insertion a zero old-side count,
    /// which is how git marks the side that has no lines. Hunks are ordered by path.
    /// </summary>
    [Fact]
    public void PureDeletionAndPureInsertionCarryZeroCountsAndHunksAreOrderedByPath()
    {
        CommitFile("src/Delete.cs", NumberedLines(10));
        CommitFile("src/Append.cs", NumberedLines(10));
        string baseRef = Head();
        Write("src/Delete.cs", NumberedLines(10).Replace("line 5\n", string.Empty, StringComparison.Ordinal));
        Write("src/Append.cs", NumberedLines(10) + "line 11\n");

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.NotNull(hunks);
        Assert.Equal(
            new[]
            {
                new ChangedHunk("src/Append.cs", 10, 0, 11, 1),
                new ChangedHunk("src/Delete.cs", 5, 1, 4, 0),
            },
            hunks);
    }

    /// <summary>
    /// A committed change and an uncommitted one are both measured, because the base is compared
    /// with the working tree and not with <c>HEAD</c>. Without the dirty set a change a lens has
    /// not yet committed would be invisible to it.
    /// </summary>
    [Fact]
    public void CommittedAndUncommittedChangesAreBothMeasuredAgainstTheWorkingTree()
    {
        CommitFile("src/a.cs", NumberedLines(5));
        CommitFile("src/b.cs", NumberedLines(5));
        string baseRef = Head();
        CommitFile("src/a.cs", NumberedLines(5).Replace("line 2\n", "committed 2\n", StringComparison.Ordinal));
        Write("src/b.cs", NumberedLines(5).Replace("line 1\n", "dirty 1\n", StringComparison.Ordinal));

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.NotNull(hunks);
        Assert.Equal(
            new[]
            {
                new ChangedHunk("src/a.cs", 2, 1, 2, 1),
                new ChangedHunk("src/b.cs", 1, 1, 1, 1),
            },
            hunks);
    }

    /// <summary>
    /// An untracked file is not in any git diff, yet a new file is part of the change. It is
    /// reported as one hunk covering the whole file, with a zero old side.
    /// </summary>
    [Fact]
    public void AnUntrackedFileIsOneWholeFileHunk()
    {
        CommitFile("src/Tracked.cs", "class Tracked { }\n");
        string baseRef = Head();
        Write("docs/New.md", "first\nsecond\nthird\n");

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.NotNull(hunks);
        Assert.Equal(new[] { new ChangedHunk("docs/New.md", 0, 0, 1, 3) }, hunks);
    }

    /// <summary>
    /// A change of eighty lines alternating with unchanged ones is forty hunks. The list is never
    /// cut short, because a lens that sees the first hunks and not the rest judges a partial change.
    /// </summary>
    [Fact]
    public void EveryHunkIsReportedWithoutTruncation()
    {
        CommitFile("src/Many.cs", NumberedLines(80));
        string baseRef = Head();
        Write(
            "src/Many.cs",
            string.Concat(Enumerable.Range(1, 80).Select(i => i % 2 == 1 ? $"edited {i}\n" : $"line {i}\n")));

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.NotNull(hunks);
        Assert.Equal(40, hunks.Count);
        Assert.Equal(new ChangedHunk("src/Many.cs", 79, 1, 79, 1), hunks[^1]);
    }

    /// <summary>
    /// A base that does not resolve leaves the fact absent even though the working tree has a
    /// real change. Reporting it empty would tell a lens the change touched nothing.
    /// </summary>
    [Fact]
    public void AnUnreadableBaseLeavesTheFactAbsentNotEmpty()
    {
        CommitFile("src/Service.cs", NumberedLines(3));
        Write("src/Service.cs", NumberedLines(3) + "line 4\n");

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, "refs/heads/no-such-base");

        Assert.Null(hunks);
    }

    /// <summary>
    /// A clean checkout measured against its own head has no hunks, and the fact stays absent
    /// rather than becoming an empty list that a rule would read as a measured empty change.
    /// </summary>
    [Fact]
    public void ACleanCheckoutLeavesTheFactAbsentNotEmpty()
    {
        CommitFile("src/Service.cs", NumberedLines(3));

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, Head());

        Assert.Null(hunks);
    }

    /// <summary>
    /// Offline <c>arbiter eval</c> has no repository to measure, so every review fact that needs
    /// one stays absent. A rule then answers undecidable instead of judging an empty change.
    /// </summary>
    [Fact]
    public void OutsideARepositoryLeavesEveryReviewFactAbsent()
    {
        ArbiterFactSet facts = Enrich(
            Declare(ChangedHunks, RenameOnly, CommentOnly, NoCodeChange),
            cwd: _outside.Path);

        Assert.False(facts.IsPresent(ChangedHunks), "no repository means no hunks, so the fact must stay absent.");
        Assert.False(facts.IsPresent(RenameOnly), "no repository means no renames, so the fact must stay absent.");
        Assert.False(facts.IsPresent(CommentOnly), "no repository means no comment-only paths, so the fact must stay absent.");
        Assert.False(facts.IsPresent(NoCodeChange), "no repository means no no-code-change paths, so the fact must stay absent.");
    }

    /// <summary>
    /// The hook measures the hunks from the recorded review base, and the fact carries the same
    /// list the git helper reports.
    /// </summary>
    [Fact]
    public void TheHookMeasuresChangedHunksFromTheReviewBase()
    {
        CommitFile("src/Service.cs", NumberedLines(10));
        SetOriginHead(Head());
        Write("src/Service.cs", Edit(NumberedLines(10), (3, "changed 3")));

        ArbiterFactSet facts = Enrich(Declare(ChangedHunks));

        Assert.Equal(
            new[] { new ChangedHunk("src/Service.cs", 3, 1, 3, 1) },
            ValueOf<IEnumerable<ChangedHunk>>(facts, ChangedHunks));
    }

    /// <summary>
    /// A rename at full similarity has no hunk, yet it is a change the lens must see. Its new path
    /// is listed as rename-only.
    /// </summary>
    [Fact]
    public void APureRenameIsListedAsRenameOnly()
    {
        CommitFile("src/Old.cs", NumberedLines(12));
        SetOriginHead(Head());
        _probe.Git("mv", "src/Old.cs", "src/New.cs");

        ArbiterFactSet facts = Enrich(Declare(RenameOnly));

        Assert.Equal(ExpectedRenameOnly, ValueOf<IEnumerable<string>>(facts, RenameOnly));
    }

    /// <summary>
    /// A rename that also edits its content is not R100 and so is not rename-only. Treating it as
    /// one would let an edit slip past the no-code-change path.
    /// </summary>
    [Fact]
    public void ARenameWithAnEditIsNotRenameOnly()
    {
        CommitFile("src/Old.cs", NumberedLines(12));
        SetOriginHead(Head());
        _probe.Git("mv", "src/Old.cs", "src/New.cs");
        Write("src/New.cs", NumberedLines(12).Replace("line 6\n", "changed 6\n", StringComparison.Ordinal));

        ArbiterFactSet facts = Enrich(Declare(RenameOnly));

        Assert.DoesNotContain("src/New.cs", ValueOf<IEnumerable<string>>(facts, RenameOnly));
    }

    /// <summary>
    /// A file whose only change is a comment is listed as comment-only, and a file whose code
    /// changed is not, even when the code change sits on a line that also looks like a comment.
    /// </summary>
    [Fact]
    public void ACommentOnlyChangeIsListedAndACodeChangeIsNot()
    {
        CommitFile("src/Notes.cs", "class Notes { }\n");
        CommitFile("src/Code.cs", "class Code { int x = 1; }\n");
        SetOriginHead(Head());
        Write("src/Notes.cs", "// added note\nclass Notes { }\n");
        Write("src/Code.cs", "class Code { int x = 2; }\n");

        ArbiterFactSet facts = Enrich(Declare(ChangedHunks, CommentOnly));

        Assert.Equal(ExpectedCommentOnly, ValueOf<IEnumerable<string>>(facts, CommentOnly));
    }

    /// <summary>
    /// The no-code-change set is the union of comment-only, rename-only and changed markdown. A
    /// code change in the same run stays out of it, so a lens never skips a file that changed code.
    /// </summary>
    [Fact]
    public void NoCodeChangeCombinesCommentOnlyRenameOnlyAndMarkdownPaths()
    {
        CommitFile("src/Notes.cs", "class Notes { }\n");
        CommitFile("src/Old.cs", NumberedLines(12));
        CommitFile("src/Code.cs", "class Code { int x = 1; }\n");
        CommitFile("docs/guide.md", "# Guide\n\nFirst.\n");
        SetOriginHead(Head());
        Write("src/Notes.cs", "// added note\nclass Notes { }\n");
        _probe.Git("mv", "src/Old.cs", "src/New.cs");
        Write("src/Code.cs", "class Code { int x = 2; }\n");
        Write("docs/guide.md", "# Guide\n\nSecond.\n");

        ArbiterFactSet facts = Enrich(Declare(ChangedHunks, RenameOnly, CommentOnly, NoCodeChange));

        Assert.Equal(
            ExpectedNoCodeChange,
            ValueOf<IEnumerable<string>>(facts, NoCodeChange).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The finding's changed lines are the new-side lines of its file that fall inside the hunks:
    /// the lines a finding can claim the change touched.
    /// </summary>
    [Fact]
    public void FindingChangedLinesAreTheNewSideLinesInsideTheHunks()
    {
        CommitFile("src/Service.cs", NumberedLines(10));
        SetOriginHead(Head());
        Write("src/Service.cs", Edit(NumberedLines(10), (3, "changed 3"), (7, "changed 7"), (8, "changed 8")));

        ArbiterFactSet facts = Enrich(Declare(ChangedHunks, FindingChangedLines).With(
            "finding.file",
            "src/Service.cs",
            ArbiterFactLabel.Asserted));

        Assert.Equal(ExpectedFindingChangedLines, ValueOf<IEnumerable<int>>(facts, FindingChangedLines));
    }

    /// <summary>
    /// Without a finding file there is nothing to intersect, so the fact stays absent even when
    /// the hunks are present.
    /// </summary>
    [Fact]
    public void FindingChangedLinesStayAbsentWithoutAFindingFile()
    {
        CommitFile("src/Service.cs", NumberedLines(10));
        SetOriginHead(Head());
        Write("src/Service.cs", Edit(NumberedLines(10), (3, "changed 3")));

        ArbiterFactSet facts = Enrich(Declare(ChangedHunks, FindingChangedLines));

        Assert.False(facts.IsPresent(FindingChangedLines), "no finding file means nothing to intersect with the hunks.");
    }

    /// <summary>
    /// Without hunks there is no changed line to name, so the fact stays absent even when the
    /// finding names a file.
    /// </summary>
    [Fact]
    public void FindingChangedLinesStayAbsentWithoutHunks()
    {
        CommitFile("src/Service.cs", NumberedLines(10));
        SetOriginHead(Head());

        ArbiterFactSet facts = Enrich(Declare(ChangedHunks, FindingChangedLines).With(
            "finding.file",
            "src/Service.cs",
            ArbiterFactLabel.Asserted));

        Assert.False(facts.IsPresent(FindingChangedLines), "a clean change has no hunks, so no finding line is changed.");
    }

    /// <summary>
    /// A one-line shebang change is code: <c>#!/bin/bash</c> picks the interpreter that runs the
    /// script. Measured as a comment it put the script on the comment-only and no-code-change lists,
    /// and KW-ARB-LENS-002 would then skip every lens for it.
    /// </summary>
    [Fact]
    public void AShebangOnlyChangeIsNeitherCommentOnlyNorNoCodeChange()
    {
        CommitFile("scripts/build.sh", "#!/bin/sh\necho build\n");
        CommitFile("docs/guide.md", "# Guide\n\nFirst.\n");
        SetOriginHead(Head());
        Write("scripts/build.sh", "#!/bin/bash\necho build\n");
        Write("docs/guide.md", "# Guide\n\nSecond.\n");

        ArbiterFactSet facts = Enrich(Declare(ChangedHunks, CommentOnly, NoCodeChange));

        Assert.DoesNotContain("scripts/build.sh", ValueOf<IEnumerable<string>>(facts, CommentOnly));
        Assert.DoesNotContain("scripts/build.sh", ValueOf<IEnumerable<string>>(facts, NoCodeChange));
    }

    /// <summary>
    /// An untracked file over one mebibyte is not read whole: a generated or vendored blob would be
    /// loaded into memory just to count its lines. A partial read would also hand a lens a partial
    /// change, so the whole fact is absent and no path is classed comment-only or no-code.
    /// </summary>
    [Fact]
    public void AnUntrackedFileOverOneMebibyteLeavesTheHunksAbsent()
    {
        CommitFile("src/Service.cs", NumberedLines(3));
        string baseRef = Head();
        Write("src/Service.cs", Edit(NumberedLines(3), (2, "changed 2")));
        Write("data/large.txt", string.Concat(Enumerable.Range(1, 100_000).Select(i => $"line {i}\n")));
        Assert.True(
            new FileInfo(Path.Combine(_probe.Root, "data", "large.txt")).Length > 1024 * 1024,
            "the fixture must exceed the one-mebibyte untracked-file cap.");

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.Null(hunks);
    }

    /// <summary>
    /// A binary untracked file (one with NUL bytes) has no meaningful lines to measure. Reading it as
    /// text would invent hunks that no reviewer can judge, so the whole fact is absent instead.
    /// </summary>
    [Fact]
    public void ABinaryUntrackedFileLeavesTheHunksAbsent()
    {
        CommitFile("src/Service.cs", NumberedLines(3));
        string baseRef = Head();
        Write("src/Service.cs", Edit(NumberedLines(3), (2, "changed 2")));
        WriteBytes("assets/logo.dat", [0x89, 0x50, 0x00, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.Null(hunks);
    }

    /// <summary>
    /// An untracked entry that cannot be read (a dangling symbolic link here) leaves the fact absent
    /// rather than throwing or dropping the file silently. This is a guard: the reader already maps
    /// the failure to absence, and the test keeps it that way.
    /// </summary>
    [Fact]
    public void AnUnreadableUntrackedFileLeavesTheHunksAbsent()
    {
        CommitFile("src/Service.cs", NumberedLines(3));
        string baseRef = Head();
        Write("src/Service.cs", Edit(NumberedLines(3), (2, "changed 2")));
        File.CreateSymbolicLink(
            Path.Combine(_probe.Root, "src", "Dangling.cs"),
            Path.Combine(_probe.Root, "src", "missing-target.cs"));

        IReadOnlyList<ChangedHunk>? hunks = GitFacts.GetChangedHunks(_probe.Root, baseRef);

        Assert.Null(hunks);
    }

    private ArbiterFactSet Enrich(ArbiterFactSet facts, string? cwd = null)
    {
        string root = cwd ?? _probe.Root;
        TriggerClassification classification = new(
            Trigger: "lens.spawn",
            Caller: "conductor",
            CallerSource: "header",
            Target: "reviewer",
            TargetClass: "reviewer",
            Headers: new Dictionary<string, string>(StringComparer.Ordinal),
            Marker: null,
            PassedWithoutConfig: false);
        ArbiterEvent ev = new()
        {
            Harness = "claude",
            Phase = "post",
            Cwd = root,
            IsDispatch = false,
        };
        return new HookGitFacts(root, _home.Path, _arbiter.Path).Enrich(facts, classification, ev);
    }

    private static ArbiterFactSet Declare(params string[] names)
    {
        ArbiterFactSet facts = new();
        foreach (string name in names)
        {
            facts = facts.With(name, null, ArbiterFactLabel.Derived);
        }

        return facts;
    }

    private static T ValueOf<T>(ArbiterFactSet facts, string name)
    {
        Assert.True(
            facts.TryGet(name, out ArbiterFact? fact) && fact!.Value is not null,
            $"{name} must be present for this change.");
        return Assert.IsAssignableFrom<T>(fact!.Value);
    }

    private static string NumberedLines(int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"line {i}\n"));

    private static string Edit(string text, params (int Line, string Replacement)[] edits)
    {
        string[] lines = text.Split('\n');
        foreach ((int line, string replacement) in edits)
        {
            lines[line - 1] = replacement;
        }

        return string.Join('\n', lines);
    }

    private string Head() =>
        GitFacts.GetHead(_probe.Root) ?? throw new InvalidOperationException("the probe repository has no HEAD.");

    private void SetOriginHead(string sha) =>
        _probe.Git("update-ref", "refs/remotes/origin/HEAD", sha);

    private void CommitFile(string relative, string text)
    {
        Write(relative, text);
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", $"test: {relative}");
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_probe.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    private void WriteBytes(string relative, byte[] bytes)
    {
        string path = Path.Combine(_probe.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
