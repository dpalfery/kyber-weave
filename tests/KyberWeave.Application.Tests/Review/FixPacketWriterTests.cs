using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
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

        Assert.Equal(["T1", "T2", "unassigned", "docs"], outcome.DispatchBlocks.Select(GroupOf));
        string t1 = Read("task-T1");
        Assert.Contains("### correctness/a", t1, StringComparison.Ordinal);
        Assert.DoesNotContain("correctness/b", t1, StringComparison.Ordinal);
        Assert.Contains("### correctness/b", Read("task-T2"), StringComparison.Ordinal);
        Assert.Contains("### documentation-accuracy/guide", Read("docs"), StringComparison.Ordinal);
        string unassigned = Read("unassigned");
        Assert.Contains("### correctness/other", unassigned, StringComparison.Ordinal);
        Assert.Contains("### gate test", unassigned, StringComparison.Ordinal);
        Assert.Contains("- summary: 3 failed", unassigned, StringComparison.Ordinal);
    }

    /// <summary>The header's rules are written once, in the packet, so each worker reads the same seven instructions.</summary>
    [Fact]
    public void EveryPacketCarriesTheFixedHeaderRules()
    {
        ScriptChange();

        _ = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        string packet = Read("unassigned");
        Assert.StartsWith("# Fix packet: unassigned\n\nRead every finding here before you change anything.\n", packet, StringComparison.Ordinal);
        Assert.Contains("1. Make one design that satisfies all of these findings together, and the earlier ones in the history, not one patch per finding.", packet, StringComparison.Ordinal);
        Assert.Contains("3. Do not change an approved plan decision unless a finding here is a regression that decision caused: then fix the regression and name the decision you overrode in your digest. A finding that only disagrees with a plan decision never reaches this packet.", packet, StringComparison.Ordinal);
        Assert.Contains("6. The fix edits the current tree; never restore a file to base and re-apply.", packet, StringComparison.Ordinal);
        Assert.Contains("7. A test you add or change is deterministic: it controls time and ordering (fake clocks, awaited events, explicit waits on a condition) and never depends on a sleep, a fixed timeout or a race with a timer.", packet, StringComparison.Ordinal);
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
        Assert.Contains("## Earlier cycles' findings on these files (line numbers are from that cycle's commit; not necessarily still open)", packet, StringComparison.Ordinal);
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

        Assert.Contains("## Earlier cycles' findings on these files (line numbers are from that cycle's commit; not necessarily still open)\n\nnone\n", Read("unassigned"), StringComparison.Ordinal);
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
        Assert.Contains(_services.Processes.Calls, call => call.Arguments.SequenceEqual(["--literal-pathspecs", "log", "--oneline", "--no-color", "aaaaaaa..HEAD", "--", "src/Core/A.cs"]));
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
            new ProcessRunResult(128, string.Empty, false) { StandardError = "fatal: bad revision in /abs/path/src/Core/A.cs\n" },
            "--literal-pathspecs",
            "log",
            "--oneline",
            "--no-color",
            "aaaaaaa..HEAD",
            "--",
            "src/Core/A.cs");

        _ = Write([Finding("correctness/third", "src/Core/A.cs")], [], prior: Path.Combine(_review, "cycle-1"));

        string packet = Read("unassigned");
        Assert.DoesNotContain("## Commits on these files since cycle 1", packet, StringComparison.Ordinal);
        Assert.Contains("Commits on these files since cycle 1: not listed — git could not list them.\n", packet, StringComparison.Ordinal);

        // A git message can carry an absolute path, which must never reach the packet.
        Assert.DoesNotContain("/abs/path", packet, StringComparison.Ordinal);
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
            $"Fix packet: {Path.Combine(_cycle, "fix-packets", "task-T1.md")}\n--- end ---",
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
        string unassigned = outcome.DispatchBlocks[0];
        Assert.Contains("Assign: the task that owns these files' component\n", unassigned, StringComparison.Ordinal);
        Assert.DoesNotContain("TASK:", unassigned, StringComparison.Ordinal);
        string docs = outcome.DispatchBlocks[1];
        Assert.Contains("--- fix docs ---\n", docs, StringComparison.Ordinal);
        Assert.Contains("Agent: docs-dev\n", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("TASK:", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE:", docs, StringComparison.Ordinal);
    }

    /// <summary>
    /// The documentation fix describes the tree after the round's code fixes, so its block is written and dispatched
    /// last and tells the conductor to integrate every other block of the round first.
    /// </summary>
    [Fact]
    public void ACodeRoundWritesTheDocumentationBlockLastAndItWaitsForTheOtherBlocks()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T1
                files:
                  - src/Core/A.cs
            """);
        ScriptChange(Plan, "src/Core/A.cs", "docs/guide.md");

        FixPacketOutcome outcome = Write(
            [Finding("correctness/a", "src/Core/A.cs"), Finding("documentation-accuracy/guide", "docs/guide.md")],
            []);

        Assert.Equal(["T1", "docs"], outcome.DispatchBlocks.Select(GroupOf));
        Assert.Equal(
            $"--- fix docs ---\nKYBER-ARBITER: true\nPLAN_FILE: {Plan}\nAgent: docs-dev\n" +
            "After: every other fix block of this round is integrated\n" +
            $"Fix packet: {Path.Combine(_cycle, "fix-packets", "docs.md")}\n--- end ---",
            outcome.DispatchBlocks[1]);
    }

    /// <summary>A round whose only packet is the documentation one has nothing to wait for, so it carries no <c>After:</c> line.</summary>
    [Fact]
    public void ADocumentationOnlyRoundHasNoAfterLine()
    {
        ScriptChange("docs/guide.md");

        FixPacketOutcome outcome = Write([Finding("documentation-accuracy/guide", "docs/guide.md")], []);

        string docs = Assert.Single(outcome.DispatchBlocks);
        Assert.StartsWith("--- fix docs ---\n", docs, StringComparison.Ordinal);
        Assert.Contains("Agent: docs-dev\n", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("After:", docs, StringComparison.Ordinal);
    }

    /// <summary>
    /// A task's group is sanitized and prefixed with <c>task-</c>, so a task called <c>docs</c> cannot take the
    /// documentation packet's name and two groups that sanitize to the same name are still distinct files.
    /// </summary>
    [Fact]
    public void PacketFileNamesAreSafeUniqueAndNeverTakeABuiltInGroupsName()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: docs
                files:
                  - src/Core/A.cs
              - id: a/b
                files:
                  - src/Core/B.cs
              - id: a-b
                files:
                  - src/Core/C.cs
            """);
        ScriptChange(Plan, "src/Core/A.cs", "src/Core/B.cs", "src/Core/C.cs", "docs/guide.md");

        FixPacketOutcome outcome = Write(
            [
                Finding("correctness/a", "src/Core/A.cs"),
                Finding("correctness/b", "src/Core/B.cs"),
                Finding("correctness/c", "src/Core/C.cs"),
                Finding("documentation-accuracy/guide", "docs/guide.md")
            ],
            []);

        Assert.True(PacketExists("task-docs"));
        Assert.True(PacketExists("docs"));
        Assert.True(PacketExists("task-a-b"));
        Assert.True(PacketExists("task-a-b-2"));
        Assert.Contains("task-docs.md", outcome.DispatchBlocks[0], StringComparison.Ordinal);
        Assert.Contains("task-a-b.md", outcome.DispatchBlocks[1], StringComparison.Ordinal);
        Assert.Contains("task-a-b-2.md", outcome.DispatchBlocks[2], StringComparison.Ordinal);
    }

    /// <summary>Packet files an earlier round wrote and this round does not are deleted before the new packets land.</summary>
    [Fact]
    public void APacketFileFromAnEarlierRoundThatThisRoundDoesNotWriteIsDeleted()
    {
        _services.Files.CreateDirectory(Path.Combine(_cycle, "fix-packets"));
        _services.Files.WriteAllText(Path.Combine(_cycle, "fix-packets", "old-group.md"), "an earlier round's packet");
        _services.Files.WriteAllText(Path.Combine(_cycle, "fix-packets", "notes.txt"), "not a packet");
        ScriptChange();

        _ = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        Assert.False(PacketExists("old-group"));
        Assert.True(PacketExists("unassigned"));
        Assert.True(_services.Files.FileExists(Path.Combine(_cycle, "fix-packets", "notes.txt")));
    }

    /// <summary>A file two tasks list stays with the first in plan order, and that packet names the other task.</summary>
    [Fact]
    public void AFileTwoTasksListStaysWithTheFirstAndItsPacketNamesTheOther()
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
                  - src/Core/A.cs
            """);
        ScriptChange(Plan, "src/Core/A.cs");

        FixPacketOutcome outcome = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        Assert.Equal(["T1"], outcome.DispatchBlocks.Select(GroupOf));
        Assert.Contains(
            "src/Core/A.cs is also listed by task T2 in the plan's task file.",
            Read("task-T1"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A finding whose fix must change a file another task owns and the file it anchors on are one packet, owned by the
    /// first of those tasks in plan order. The packet and its dispatch name the other task's files, so the worker may
    /// edit them and the Arbiter's scope check reads them as part of this fix.
    /// </summary>
    [Fact]
    public void AFindingThatSpansTwoTasksFilesIsOnePacketThatNamesTheOtherTasksFiles()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T2
                files:
                  - src/Contract/OrderDto.cs
              - id: T3
                files:
                  - src/Http/OrdersController.cs
            """);
        ScriptChange(Plan, "src/Contract/OrderDto.cs", "src/Http/OrdersController.cs");

        FixPacketOutcome outcome = Write(
            [
                Finding(
                    "contract/page-envelope",
                    "src/Contract/OrderDto.cs",
                    evidence: "src/Http/OrdersController.cs:40 returns totalCount under page, and the page reads it at the root.")
            ],
            []);

        string block = Assert.Single(outcome.DispatchBlocks);
        Assert.Equal("T2", GroupOf(block));
        string packet = Read("task-T2");
        Assert.Contains("### contract/page-envelope", packet, StringComparison.Ordinal);
        Assert.Contains(
            "This fix may also edit task T3's files: src/Http/OrdersController.cs.",
            packet,
            StringComparison.Ordinal);
        Assert.Contains("Also in scope (task T3): src/Http/OrdersController.cs\n", block, StringComparison.Ordinal);
        Assert.Contains("src/Http/OrdersController.cs", AlsoInScopeLines.Files(block));
        Assert.False(PacketExists("task-T3"));
    }

    /// <summary>
    /// Seat text is capped before anything scans it, so a citation past the cap never widens the fix's scope to another
    /// task's file.
    /// </summary>
    [Fact]
    public void ACitationBeyondTheSeatTextLimitDoesNotExtendTheScope()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T2
                files:
                  - src/Contract/OrderDto.cs
              - id: T3
                files:
                  - src/Http/OrdersController.cs
            """);
        ScriptChange(Plan, "src/Contract/OrderDto.cs");
        string padding = string.Concat(Enumerable.Repeat("# ", ReviewSeatInput.MaxTextCharacters / 2));

        FixPacketOutcome outcome = Write(
            [
                Finding(
                    "contract/page-envelope",
                    "src/Contract/OrderDto.cs",
                    evidence: padding + "src/Http/OrdersController.cs:40 returns the envelope.")
            ],
            []);

        string block = Assert.Single(outcome.DispatchBlocks);
        Assert.DoesNotContain("Also in scope", block, StringComparison.Ordinal);
        Assert.DoesNotContain("This fix may also edit task T3's files", Read("task-T2"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A coupled packet's history and commits cover the other task's files too, so a finding or a commit already made
    /// there is not lost to the worker that must edit it.
    /// </summary>
    [Fact]
    public void ACoupledPacketsHistoryAndCommitsCoverTheOtherTasksFiles()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T2
                files:
                  - src/Contract/OrderDto.cs
              - id: T3
                files:
                  - src/Http/OrdersController.cs
            """);
        WriteEarlierCycle(
            1,
            head: "aaaaaaa",
            Finding("http/earlier", "src/Http/OrdersController.cs", line: 40, claim: "The controller drops the page."));
        ScriptChange(Plan, "src/Contract/OrderDto.cs", "src/Http/OrdersController.cs");
        _services.Processes.Script(
            "git",
            new ProcessRunResult(0, "ccccccc fix the controller\n", false),
            "--literal-pathspecs",
            "log",
            "--oneline",
            "--no-color",
            "aaaaaaa..HEAD",
            "--",
            "src/Contract/OrderDto.cs",
            "src/Http/OrdersController.cs");

        _ = Write(
            [
                Finding(
                    "contract/page-envelope",
                    "src/Contract/OrderDto.cs",
                    evidence: "src/Http/OrdersController.cs:40 returns the envelope.")
            ],
            [],
            prior: Path.Combine(_review, "cycle-1"));

        string packet = Read("task-T2");
        Assert.Contains(
            "cycle 1 · http/earlier (major) src/Http/OrdersController.cs:40 — The controller drops the page.",
            packet,
            StringComparison.Ordinal);
        Assert.Contains("## Commits on these files since cycle 1\n\nccccccc fix the controller\n", packet, StringComparison.Ordinal);
    }

    /// <summary>Findings that touch the same file are one fix even when the file is cited from another task's file.</summary>
    [Fact]
    public void FindingsThatShareAFileAreOnePacket()
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T2
                files:
                  - src/Contract/OrderDto.cs
              - id: T3
                files:
                  - src/Http/OrdersController.cs
            """);
        ScriptChange(Plan, "src/Contract/OrderDto.cs", "src/Http/OrdersController.cs");

        FixPacketOutcome outcome = Write(
            [
                Finding(
                    "contract/page-envelope",
                    "src/Contract/OrderDto.cs",
                    evidence: "src/Http/OrdersController.cs:40 returns the envelope."),
                Finding("http/page-envelope", "src/Http/OrdersController.cs")
            ],
            []);

        string block = Assert.Single(outcome.DispatchBlocks);
        Assert.Equal("T2", GroupOf(block));
        string packet = Read("task-T2");
        Assert.Contains("### contract/page-envelope", packet, StringComparison.Ordinal);
        Assert.Contains("### http/page-envelope", packet, StringComparison.Ordinal);
        Assert.False(PacketExists("task-T3"));
    }

    /// <summary>A packet that cannot be written is reported, and the blocks of the packets already written are kept.</summary>
    [Fact]
    public void APacketThatCannotBeWrittenKeepsTheBlocksOfThePacketsBeforeIt()
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
                  - src/Core/B.cs
            """);
        ScriptChange(Plan, "src/Core/A.cs", "src/Core/B.cs");
        _services.Files.FailWhen(
            operation => operation.Name == "WriteAllText"
                         && operation.Path is { } path
                         && path.EndsWith("task-T2.md", StringComparison.Ordinal),
            new IOException("disk full"));

        FixPacketOutcome outcome = Write(
            [Finding("correctness/a", "src/Core/A.cs"), Finding("correctness/b", "src/Core/B.cs")],
            []);

        Assert.Equal(["T1"], outcome.DispatchBlocks.Select(GroupOf));
        Assert.True(PacketExists("task-T1"));
        Assert.False(PacketExists("task-T2"));
        Assert.Contains(outcome.Notes, note => note.Contains("task-T2.md", StringComparison.Ordinal)
                                              && note.Contains("could not be written", StringComparison.Ordinal));
    }

    /// <summary>Git failing to name the change is reported, and the finding still reaches the unassigned packet.</summary>
    [Fact]
    public void AChangeGitCannotListIsReportedAsANote()
    {
        _services.Processes.Script(
            "git",
            new ProcessRunResult(128, string.Empty, false) { StandardError = "fatal: not a git repository\n" },
            "merge-base",
            "origin/HEAD",
            "HEAD");

        FixPacketOutcome outcome = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        Assert.Contains(outcome.Notes, note => note.Contains("git could not list the change", StringComparison.Ordinal));
        Assert.Contains("### correctness/a", Read("unassigned"), StringComparison.Ordinal);
    }

    /// <summary>No plan, or several, is reported: no task can own a finding, so all but the Markdown ones go to unassigned.</summary>
    [Theory]
    [InlineData(false, "no plan was found for this change")]
    [InlineData(true, "the change touches 2 plans")]
    public void AChangeWithoutExactlyOnePlanIsReportedAsANote(bool several, string reason)
    {
        WritePlan("""
            schema-version: 1
            plan: docs/plans/demo.md
            tasks:
              - id: T1
                files:
                  - src/Core/A.cs
            """);
        if (several)
        {
            Write("repo/docs/plans/other.md", "---\nid: other\ntitle: Other\ndoc-type: plan\nstatus: Ready\n---\n\n# Other\n");
            ScriptChange(Plan, "docs/plans/other.md", "src/Core/A.cs");
        }
        else
        {
            ScriptChange("src/Core/A.cs");
        }

        FixPacketOutcome outcome = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        Assert.Contains(outcome.Notes, note => note.Contains(reason, StringComparison.Ordinal));
        Assert.Contains("### correctness/a", Read("unassigned"), StringComparison.Ordinal);
    }

    /// <summary>A task file the plan names but the tree does not hold is reported, and its findings go to unassigned.</summary>
    [Fact]
    public void AMissingTaskFileIsReportedAsANote()
    {
        Write("repo/" + Plan, "---\nid: demo\ntitle: Demo\ndoc-type: plan\nstatus: Ready\n---\n\n# Demo\n");
        ScriptChange(Plan, "src/Core/A.cs");

        FixPacketOutcome outcome = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        Assert.Contains(outcome.Notes, note => note.Contains("docs/plans/demo.tasks.yml", StringComparison.Ordinal)
                                              && note.Contains("missing or could not be read", StringComparison.Ordinal));
        Assert.Contains("### correctness/a", Read("unassigned"), StringComparison.Ordinal);
    }

    /// <summary>A task file that cannot be parsed is reported with its reason, so it reads differently from a missing one.</summary>
    [Fact]
    public void ACorruptTaskFileIsReportedWithItsReason()
    {
        Write("repo/" + Plan, "---\nid: demo\ntitle: Demo\ndoc-type: plan\nstatus: Ready\n---\n\n# Demo\n");
        Write("repo/docs/plans/demo.tasks.yml", "tasks: [unclosed\n: : :\n");
        ScriptChange(Plan, "src/Core/A.cs");

        FixPacketOutcome outcome = Write([Finding("correctness/a", "src/Core/A.cs")], []);

        string note = Assert.Single(outcome.Notes, note => note.Contains("docs/plans/demo.tasks.yml", StringComparison.Ordinal));
        Assert.Contains("could not be read (", note, StringComparison.Ordinal);
        Assert.DoesNotContain("missing", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("### correctness/a", Read("unassigned"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A cycle whose record cannot be read is named in the history, never hiding the cycles that can be read, and the
    /// commits section names the first cycle it could actually read.
    /// </summary>
    [Fact]
    public void AnUnreadableEarlierCycleIsNamedAndDoesNotHideTheReadableOnes()
    {
        WriteEarlierCycle(1, head: "aaaaaaa", Finding("correctness/first", "src/Core/A.cs", line: 4));
        WriteEarlierCycle(2, head: "bbbbbbb", Finding("correctness/second", "src/Core/A.cs", line: 9));
        string head = Path.Combine(_review, "cycle-1", "head");
        _services.Files.WriteAllText(head, "aaaaaaa\n");
        _services.Files.FailWhen(
            operation => operation.Name == "ReadAllText"
                         && string.Equals(operation.Path, head, StringComparison.Ordinal),
            new IOException("the cycle record cannot be read"));
        ScriptChange("src/Core/A.cs");
        ScriptLog("bbbbbbb..HEAD", "ccccccc fix");

        FixPacketOutcome outcome = Write(
            [Finding("correctness/third", "src/Core/A.cs")], [], prior: Path.Combine(_review, "cycle-2"));

        string packet = Read("unassigned");
        Assert.Contains("(cycle 1 unreadable)", packet, StringComparison.Ordinal);
        Assert.Contains("cycle 2 · correctness/second (major) src/Core/A.cs:9", packet, StringComparison.Ordinal);
        Assert.Contains("## Commits on these files since cycle 2\n\nccccccc fix\n", packet, StringComparison.Ordinal);
        Assert.Contains(outcome.Notes, note => note.Contains("cycle 1 could not be read", StringComparison.Ordinal));
    }

    private static string GroupOf(string block) => block["--- fix ".Length..block.IndexOf(" ---", StringComparison.Ordinal)];

    private string PacketFile(string name) => Path.Combine(_cycle, "fix-packets", name + ".md");

    private bool PacketExists(string name) => _services.Files.FileExists(PacketFile(name));

    private static ReviewFinding Finding(
        string id,
        string file,
        int line = 12,
        string claim = "The loop bound excludes the final element.",
        string? evidence = "src/Foo.cs:12") =>
        new(
            id,
            "correctness",
            ReviewSeverity.Major,
            9,
            file,
            line,
            "if (i < count)",
            claim,
            evidence,
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
            "--literal-pathspecs",
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
