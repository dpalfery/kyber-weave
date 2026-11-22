using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// A fix round writes one ready packet per owning group, so the conductor sends the findings themselves instead of
/// paraphrasing the <c>fix:</c> lines. Every test runs in memory: the file system is the shared fake, the cycles are
/// folders in it, and git answers from a script.
/// </summary>
public sealed class FixPacketWriterTests : IDisposable
{
    private const string Plan = "docs/plans/demo.md";

    private readonly InMemoryReviewServices _services = new();
    private readonly string _tree;
    private readonly string _review;
    private readonly string _cycle;

    public FixPacketWriterTests()
    {
        _tree = Path.Combine(_services.Root, "repo");
        _review = Path.Combine(_tree, ".agent_scratchpad", "task", "review");
        _cycle = Path.Combine(_review, "cycle-3");
        _services.Files.CreateDirectory(_cycle);
    }

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <summary>A finding on a task's file, a Markdown file and a file no task lists each reach their own packet.</summary>
    [Fact]
    public void FindingsAreGroupedByTaskByDocumentationAndByNoTaskAtAll()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T1
                files:
                  - src/Core/A.cs
              - id: T2
                files:
                  - src/Review/
            """);
        ScriptChange(Plan, "src/Core/A.cs", "src/Review/B.cs", "docs/guide.md", "src/Other/C.cs");

        FixPacketOutcome outcome = Write(
            [
                Finding("correctness/a", "src/Core/A.cs"),
                Finding("correctness/b", "src/Review/B.cs"),
                Finding("documentation-accuracy/guide", "docs/guide.md"),
                Finding("correctness/other", "src/Other/C.cs")
            ],
            [new GateResult("test", true, 1, "3 failed")]);

        Assert.Equal(["T1", "T2", "docs", "unassigned"], outcome.DispatchBlocks.Select(GroupOf));
        string t1 = Read("T1");
        Assert.Contains("### correctness/a", t1, StringComparison.Ordinal);
        Assert.DoesNotContain("correctness/b", t1, StringComparison.Ordinal);
        Assert.Contains("### correctness/b", Read("T2"), StringComparison.Ordinal);
        Assert.Contains("### documentation-accuracy/guide", Read("docs"), StringComparison.Ordinal);
        string unassigned = Read("unassigned");
        Assert.Contains("### correctness/other", unassigned, StringComparison.Ordinal);
        Assert.Contains("### gate test", unassigned, StringComparison.Ordinal);
        Assert.Contains("- summary: 3 failed", unassigned, StringComparison.Ordinal);
    }

    /// <summary>The header's rules are written once, in the packet, so each worker reads the same six instructions.</summary>
    [Fact]
    public void EveryPacketCarriesTheFixedHeaderRules()
    {
        ScriptChange();

        _ = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        string packet = Read("unassigned");
        Assert.StartsWith("# Fix packet: unassigned\n\nRead every finding here before you change anything.\n", packet, StringComparison.Ordinal);
        Assert.Contains("1. Make one design that satisfies all of these findings together, and the earlier ones in the history, not one patch per finding.", packet, StringComparison.Ordinal);
        Assert.Contains("3. Never change an approved plan decision. If a finding can only be fixed by changing one, leave it and report `Plan conflict: <id> — <the decision>`.", packet, StringComparison.Ordinal);
        Assert.Contains("6. The fix edits the current tree; never restore a file to base and re-apply.", packet, StringComparison.Ordinal);
    }

    /// <summary>Every earlier cycle's finding on a file of the packet is in its history, oldest cycle first.</summary>
    [Fact]
    public void TheHistoryNamesEveryEarlierFindingOnThePacketsFiles()
    {
        WriteEarlierCycle(1, head: "aaaaaaa", Finding("correctness/first", "src/Core/A.cs", line: 4, claim: "The first defect."));
        WriteEarlierCycle(2, head: "bbbbbbb", Finding("correctness/second", "src/Core/A.cs", line: 9, claim: "The second defect."));
        ScriptChange("src/Core/A.cs");
        ScriptLog("aaaaaaa..HEAD", "ccccccc later fix");

        FixPacketOutcome outcome = Write([Finding("correctness/third", "src/Core/A.cs")], [], prior: Path.Combine(_review, "cycle-2"));

        string packet = Read(GroupOf(outcome.DispatchBlocks[0]));
        Assert.Contains("## History on these files", packet, StringComparison.Ordinal);
        Assert.Contains("cycle 1 · correctness/first (major) src/Core/A.cs:4 — The first defect.", packet, StringComparison.Ordinal);
        Assert.Contains("cycle 2 · correctness/second (major) src/Core/A.cs:9 — The second defect.", packet, StringComparison.Ordinal);
    }

    /// <summary>A file no earlier finding touched says <c>none</c> rather than leaving the section blank.</summary>
    [Fact]
    public void AHistoryWithNoEarlierFindingSaysNone()
    {
        WriteEarlierCycle(1, head: null, Finding("correctness/first", "src/Core/A.cs"));
        ScriptChange();

        _ = Write([Finding("correctness/other", "src/Other/C.cs")], [], prior: Path.Combine(_review, "cycle-1"));

        Assert.Contains("## History on these files\n\nnone\n", Read("unassigned"), StringComparison.Ordinal);
    }

    /// <summary>The commits on the packet's files since cycle 1 are its last section.</summary>
    [Fact]
    public void TheCommitsOnThePacketsFilesSinceCycleOneAreListed()
    {
        WriteEarlierCycle(1, head: "aaaaaaa", Finding("correctness/first", "src/Core/A.cs"));
        ScriptChange("src/Core/A.cs");
        ScriptLog("aaaaaaa..HEAD", "ccccccc first fix", "ddddddd second fix");

        _ = Write([Finding("correctness/third", "src/Core/A.cs")], [], prior: Path.Combine(_review, "cycle-1"));

        string packet = Read("unassigned");
        Assert.Contains("## Commits on these files since cycle 1\n\nccccccc first fix\nddddddd second fix\n", packet, StringComparison.Ordinal);
        Assert.Contains(_services.Processes.Calls, call => call.Arguments.SequenceEqual(["log", "--oneline", "--no-color", "aaaaaaa..HEAD", "--", "src/Core/A.cs"]));
    }

    /// <summary>Cycle 1 with no head omits the commits section and says why in one line.</summary>
    [Fact]
    public void ACycleOneWithNoHeadOmitsTheCommitsSectionAndSaysWhy()
    {
        WriteEarlierCycle(1, head: null, Finding("correctness/first", "src/Core/A.cs"));
        ScriptChange();

        _ = Write([Finding("correctness/third", "src/Core/A.cs")], [], prior: Path.Combine(_review, "cycle-1"));

        string packet = Read("unassigned");
        Assert.DoesNotContain("## Commits on these files since cycle 1", packet, StringComparison.Ordinal);
        Assert.Contains("Commits on these files since cycle 1: not listed — cycle 1 recorded no head.", packet, StringComparison.Ordinal);
    }

    /// <summary>A git failure omits the commits section and states the reason on one line.</summary>
    [Fact]
    public void AGitFailureOmitsTheCommitsSectionAndSaysWhy()
    {
        WriteEarlierCycle(1, head: "aaaaaaa", Finding("correctness/first", "src/Core/A.cs"));
        ScriptChange();
        _services.Processes.Script(
            "git",
            new ProcessRunResult(128, string.Empty, false) { StandardError = "fatal: bad revision\n" },
            "log",
            "--oneline",
            "--no-color",
            "aaaaaaa..HEAD",
            "--",
            "src/Core/A.cs");

        _ = Write([Finding("correctness/third", "src/Core/A.cs")], [], prior: Path.Combine(_review, "cycle-1"));

        string packet = Read("unassigned");
        Assert.DoesNotContain("## Commits on these files since cycle 1", packet, StringComparison.Ordinal);
        Assert.Contains("Commits on these files since cycle 1: not listed — git could not list them", packet, StringComparison.Ordinal);
        Assert.Contains("exited with code 128: fatal: bad revision", packet, StringComparison.Ordinal);
        Assert.EndsWith(").\n", packet[(packet.IndexOf("not listed — git could not list them", StringComparison.Ordinal))..], StringComparison.Ordinal);
    }

    /// <summary>A task packet's block names the plan, the task and the packet's absolute path, and nothing else.</summary>
    [Fact]
    public void ATaskDispatchBlockNamesThePlanTheTaskAndThePacketPath()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T1
                files:
                  - src/Core/A.cs
            """);
        ScriptChange(Plan, "src/Core/A.cs");

        FixPacketOutcome outcome = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        Assert.Equal(
            $"--- fix T1 ---\nKYBER-ARBITER: true\nPLAN_FILE: {Plan}\nTASK: T1\n" +
            $"Fix packet: {Path.Combine(_cycle, "fix-packets", "T1.md")}\n--- end ---",
            Assert.Single(outcome.DispatchBlocks));
    }

    /// <summary>The documentation and unassigned blocks name their agent or say who must assign the work.</summary>
    [Fact]
    public void TheDocumentationAndUnassignedBlocksNameTheirOwner()
    {
        ScriptChange();

        FixPacketOutcome outcome = Write(
            [Finding("documentation-accuracy/guide", "docs/guide.md"), Finding("correctness/other", "src/Other/C.cs")],
            []);

        Assert.Equal(2, outcome.DispatchBlocks.Count);
        string docs = outcome.DispatchBlocks[0];
        Assert.Contains("--- fix docs ---\n", docs, StringComparison.Ordinal);
        Assert.Contains("Agent: docs-dev\n", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("TASK:", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE:", docs, StringComparison.Ordinal);
        string unassigned = outcome.DispatchBlocks[1];
        Assert.Contains("Assign: the task that owns these files' component\n", unassigned, StringComparison.Ordinal);
        Assert.DoesNotContain("TASK:", unassigned, StringComparison.Ordinal);
    }

    private static string GroupOf(string block) => block["--- fix ".Length..block.IndexOf(" ---", StringComparison.Ordinal)];

    private static ReviewFinding Finding(
        string id, string file, int line = 12, string claim = "The loop bound excludes the final element.") =>
        new(
            id,
            "correctness",
            ReviewSeverity.Major,
            9,
            file,
            line,
            "if (i < count)",
            claim,
            "src/Foo.cs:12",
            "The last element is never processed.",
            "Use <= or count + 1.",
            Kind: FindingKind.Defect,
            Basis: "AC-2: every element is processed.");

    private FixPacketOutcome Write(
        IReadOnlyList<ReviewFinding> findings, IReadOnlyList<GateResult> gates, string? prior = null) =>
        FixPacketWriter.Write(
            new FixPacketInput(_tree, _cycle, prior, null, "docs/plans", findings, gates),
            _services.Cycles,
            _services.Get<IGitChangeReader>(),
            _services.Files);

    private string Read(string group) => _services.Files.ReadAllText(Path.Combine(_cycle, "fix-packets", group + ".md"));

    private void WritePlan(string tasks) =>
        Write("repo/" + Plan, "---\nid: demo\ntitle: Demo\ndoc-type: plan\nstatus: Ready\n---\n\n# Demo\n")
            .Write("repo/docs/plans/demo.tasks.yml", tasks.TrimEnd() + "\n");

    private void WriteEarlierCycle(int number, string? head, ReviewFinding finding)
    {
        string folder = Path.Combine(_review, $"cycle-{number}");
        _services.Files.CreateDirectory(folder);
        if (head is not null)
            _services.Cycles.WriteIdentity(folder, new ReviewCycleIdentity("branch", "base", head));
        _services.Files.WriteAllText(
            Path.Combine(folder, "findings.json"),
            ReviewJson.Write(new FindingsReport(FindingsReport.CurrentSchema, [finding], [], 0)));
    }

    private void ScriptChange(params string[] paths)
    {
        string status = string.Concat(paths.Select(path => $"M\0{path}\0"));
        _ = _services.Processes
            .Script("git", new ProcessRunResult(0, "base123\n", false), "merge-base", "origin/HEAD", "HEAD")
            .Script("git", new ProcessRunResult(0, status, false), "diff", "-z", "--name-status", "-M", "--relative", "base123", "--")
            .Script("git", new ProcessRunResult(0, string.Empty, false), "ls-files", "-z", "--others", "--exclude-standard", "--");
    }

    private void ScriptLog(string range, params string[] lines) =>
        _services.Processes.Script(
            "git",
            new ProcessRunResult(0, string.Join('\n', lines) + (lines.Length == 0 ? string.Empty : "\n"), false),
            "log",
            "--oneline",
            "--no-color",
            range,
            "--",
            "src/Core/A.cs");

    private FixPacketWriterTests Write(string relativePath, string content)
    {
        string path = Path.Combine(_services.Root, relativePath);
        _services.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _services.Files.WriteAllText(path, content);
        return this;
    }
}
