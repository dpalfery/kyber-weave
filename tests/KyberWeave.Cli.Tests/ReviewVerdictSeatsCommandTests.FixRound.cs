using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review verdict</c> command's fix round: what it takes, what becomes follow-up work, and what a later cycle
/// carries. Runs in process over the in-memory file system; no child process, no disk.
/// </summary>
public sealed partial class ReviewVerdictSeatsCommandTests
{
    private const string FixRound = "KW-REVIEW-056";

    /// <summary>
    /// An APPROVE with a surviving major and two minors sends all three to the fix round by default, and says to run a
    /// verifier cycle after it.
    /// </summary>
    [Fact]
    public void AnApprovalWithAMajorAndMinorsSendsAllOfThemToTheFixRoundByDefault()
    {
        WriteSeat("correctness", FindingSeat(severity: "major"));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/name-unclear", severity: "minor"));
        WriteSeat("test-adequacy", FindingSeat(lens: "test-adequacy", id: "test-adequacy/no-test", severity: "minor", line: 20));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness", "security", "test-adequacy")]);

        Assert.Equal(0, run.Result);
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: correctness/off-by-one (major) src/Foo.cs:12", lines);
        Assert.Contains("fix: security/name-unclear (minor) src/Foo.cs:12", lines);
        Assert.Contains("fix: test-adequacy/no-test (minor) src/Foo.cs:20", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("follow-up:", StringComparison.Ordinal));
        Assert.Contains("fix round: 3 item(s) at fix level minor; fix these, then run a verifier cycle.", lines);
    }

    /// <summary>The opt-out <c>review.policy.fix-level: major</c> leaves the minor finding out of the fix round.</summary>
    [Fact]
    public void TheMajorLevelLeavesTheMinorFindingAsFollowUpWork()
    {
        UsePolicy("    fix-level: major\n");
        WriteSeat("correctness", FindingSeat(severity: "major"));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/name-unclear", severity: "minor"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness", "security")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: correctness/off-by-one (major) src/Foo.cs:12", lines);
        Assert.Contains("follow-up: security/name-unclear (minor) src/Foo.cs:12", lines);
        Assert.Contains(lines, line => line.StartsWith("fix round: 1 item(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A finding dropped as pre-existing debt blocks nothing and is listed as follow-up work, so it becomes a todo; a
    /// clean review says the loop ends.
    /// </summary>
    [Fact]
    public void AFindingDroppedAsPreExistingDebtIsFollowUpWorkAndTheLoopEnds()
    {
        WriteSeat("correctness", FindingSeat(severity: "major"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--drop", "correctness/off-by-one=pre-existing debt: the file is not in this change",
                .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("follow-up: correctness/off-by-one (major) src/Foo.cs:12 (pre-existing debt)", lines);
        Assert.Contains("fix round: none (fix level minor); review clean: proceed to archive.", lines);
    }

    /// <summary>
    /// A seat that states in its claim that the code predates the change needs no <c>--drop</c>: the major hardening it
    /// reports in cycle 1 is dropped as pre-existing debt and listed as follow-up work instead of fixed.
    /// </summary>
    [Fact]
    public void APreExistingDebtMajorHardeningInCycle1IsFollowUpWork()
    {
        WriteSeat("correctness", FindingSeat(
            severity: "major",
            kind: "hardening",
            claim: "Pre-existing debt: the file is not in this change"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(
            MessagesWithCode(run.Output, KindRouting),
            line => line == "Dropped 'correctness/off-by-one': pre-existing debt: the file is not in this change.");
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("follow-up: correctness/off-by-one (major) src/Foo.cs:12 (pre-existing debt)", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("fix:", StringComparison.Ordinal));
        Assert.Contains("fix round: none (fix level minor); review clean: proceed to archive.", lines);
    }

    /// <summary>A hardening whose claim does not say it predates the change is still fixed in cycle 1.</summary>
    [Fact]
    public void ANormalMajorHardeningInCycle1IsStillFixed()
    {
        WriteSeat("correctness", FindingSeat(
            severity: "major",
            kind: "hardening",
            claim: "The loop bound excludes the final element."));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains("fix: correctness/off-by-one (major) src/Foo.cs:12", MessagesWithCode(run.Output, FixRound));
    }

    /// <summary>
    /// A manual <c>--drop</c> of a finding whose claim also says it predates the change wins: the finding is dropped
    /// once, with the reviewer's reason, and never reaches the automatic routing.
    /// </summary>
    [Fact]
    public void AManualDropOfAPreExistingDebtFindingProducesOneDrop()
    {
        WriteSeat("correctness", FindingSeat(
            severity: "major",
            kind: "hardening",
            claim: "Pre-existing debt: the file is not in this change"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--drop", "correctness/off-by-one=pre-existing debt: the reviewer says so",
                .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Single(
            MessagesWithCode(run.Output, FixRound),
            line => line.StartsWith("follow-up: correctness/off-by-one", StringComparison.Ordinal));
        Assert.DoesNotContain(
            MessagesWithCode(run.Output, KindRouting),
            line => line.StartsWith("Dropped 'correctness/off-by-one'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A finding dropped as a pull request note blocks nothing and is listed as follow-up work, exactly as pre-existing
    /// debt is; a clean review says the loop ends.
    /// </summary>
    [Fact]
    public void AFindingDroppedAsAPullRequestNoteIsFollowUpWorkAndTheLoopEnds()
    {
        WriteSeat("correctness", FindingSeat(severity: "major"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--drop", "correctness/off-by-one=pr note: the pull request describes this instead",
                .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("follow-up: correctness/off-by-one (major) src/Foo.cs:12 (pr note)", lines);
        Assert.Contains("fix round: none (fix level minor); review clean: proceed to archive.", lines);
    }

    /// <summary>
    /// A later cycle carries the prior minor finding the fix round did not take, so the last cycle, which the pull
    /// request reads, still lists it; the major the fix round took is not carried.
    /// </summary>
    [Fact]
    public void ALaterCycleCarriesThePriorMinorFindingButNotTheMajorTheFixRoundTook()
    {
        UsePolicy("    fix-level: major\n");
        WriteSource("public int Total(int[] items)\n{\n    if (i < count)\n    return total;\n}\n");
        JsonObject minor = PriorFinding();
        minor["id"] = "security/name-unclear";
        minor["lens"] = "security";
        minor["severity"] = "minor";
        minor["line"] = 3;
        minor["excerpt"] = "if (i < count)";
        minor["claim"] = "The name does not say what it counts.";
        minor["evidence"] = "src/Foo.cs:3";
        minor["failureScenario"] = "A reader takes the count for a total.";
        WritePrior("findings", "lower", new JsonArray(PriorFinding(), minor));
        string priorPath = Path.Combine(PriorFolder, "findings.json");
        JsonObject priorDocument = JsonNode.Parse(_services.Files.ReadAllText(priorPath))!.AsObject();
        priorDocument["coverage"] = new JsonArray(new JsonObject { ["lens"] = "security", ["outcome"] = "findings", ["reason"] = null });
        _services.Files.WriteAllText(priorPath, priorDocument.ToJsonString());
        WriteSeat("correctness", NoFindingsSeat("correctness"));
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, "--out", report, .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(0, run.Result);
        Assert.Equal(["security/name-unclear"], ReadFindingIds(report));
    }

    /// <summary>
    /// An approval is not the end: a major still open after a fix round goes to another one, and the loop goes on.
    /// </summary>
    [Fact]
    public void AnApprovalAfterAFixRoundSendsAStillOpenMajorBackForAnotherRound()
    {
        JsonObject major = PriorFinding();
        major["excerpt"] = "if (i < count)";
        major["claim"] = "The loop bound excludes the final element.";
        major["evidence"] = "src/Foo.cs";
        major["failureScenario"] = "The last element is never processed.";
        WritePrior("findings", "lower", new JsonArray(major));
        WriteSeat("correctness", FindingSeat(severity: "major"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: correctness/off-by-one (major) src/Foo.cs:12", lines);
        Assert.Contains("fix round: 1 item(s) at fix level minor; fix these, then run a verifier cycle.", lines);
    }

    /// <summary>A verifier cycle that finds nothing ends the loop and says to archive.</summary>
    [Fact]
    public void ACleanVerifierCycleEndsTheLoop()
    {
        WritePrior("findings", "lower", new JsonArray(PriorFinding()));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Equal(["fix round: none (fix level minor); review clean: proceed to archive."], lines);
    }

    /// <summary>Three fix rounds (four cycles) are reachable: a falling count never trips a cap.</summary>
    [Fact]
    public void ThreeFixRoundsAreReachableWhileTheCountFalls()
    {
        WriteCycles(Minors(3), Minors(2), Minors(1));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/m1", severity: "minor"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", CycleFolder(3), .. NotSeatedExcept("security")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: security/m1 (minor) src/Foo.cs:12", lines);
        Assert.Contains("fix round: 1 item(s) at fix level minor; fix these, then run a verifier cycle.", lines);
    }

    /// <summary>At the fifth cycle the loop stops and the one minor finding left is listed as open, not dropped.</summary>
    [Fact]
    public void TheCycleCapListsTheMinorFindingStillLeftAsOpen()
    {
        WriteCycles(Minors(4), Minors(3), Minors(2), Minors(1));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/m1", severity: "minor"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", CycleFolder(4), .. NotSeatedExcept("security")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("open: security/m1 (minor) src/Foo.cs:12", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("fix:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("fix round: none (fix level minor); the loop stopped at its 5-cycle cap", StringComparison.Ordinal));
    }

    /// <summary>Under the default, a loop that only finds minors without ever lowering the count trips the no-progress cap.</summary>
    [Fact]
    public void TheNoProgressCapCountsMinorFindingsUnderTheFixPolicy()
    {
        WriteCycles(Minors(1), Minors(1), Minors(1));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/m1", severity: "minor"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", CycleFolder(3), .. NotSeatedExcept("security")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("open: security/m1 (minor) src/Foo.cs:12", lines);
        Assert.Contains(lines, line => line.StartsWith("fix round: none (fix level minor); the loop stopped because 3 cycles in a row", StringComparison.Ordinal));
    }

    /// <summary>At fix level major the same minors are not findings to fix, so no cap applies and the review is clean.</summary>
    [Fact]
    public void TheMajorLevelDoesNotCountMinorFindingsTowardTheCaps()
    {
        UsePolicy("    fix-level: major\n");
        WriteCycles(Minors(1), Minors(1), Minors(1));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/m1", severity: "minor"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", CycleFolder(3), .. NotSeatedExcept("security")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("follow-up: security/m1 (minor) src/Foo.cs:12", lines);
        Assert.Contains("fix round: none (fix level major); review clean: proceed to archive.", lines);
    }

    /// <summary>Fix level <c>critical</c> sends only critical findings to the loop; a major goes to follow-up.</summary>
    [Fact]
    public void TheCriticalLevelFixesOnlyCriticalFindings()
    {
        UsePolicy("    fix-level: critical\n");
        WriteSeat("correctness", FindingSeat(severity: "critical"));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/major", severity: "major", line: 20));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness", "security")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: correctness/off-by-one (critical) src/Foo.cs:12", lines);
        Assert.Contains("follow-up: security/major (major) src/Foo.cs:20", lines);
        Assert.Contains("fix round: 1 item(s) at fix level critical; fix these, then run a verifier cycle.", lines);
    }

    /// <summary>Below the fix level a finding is routed to follow-up work, never fixed and never dropped.</summary>
    [Fact]
    public void AFindingBelowTheLevelIsFollowUpWorkAndTheReviewIsCleanOfFixes()
    {
        UsePolicy("    fix-level: major\n");
        WriteSeat("security", FindingSeat(lens: "security", id: "security/minor", severity: "minor"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("security")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("follow-up: security/minor (minor) src/Foo.cs:12", lines);
        Assert.Contains("fix round: none (fix level major); review clean: proceed to archive.", lines);
    }

    /// <summary>At fix level <c>nitpick</c> a nitpick reaches the fix loop.</summary>
    [Fact]
    public void TheNitpickLevelSendsNitpicksToTheFixLoop()
    {
        UsePolicy("    fix-level: nitpick\n");
        WriteSeat("supportability", FindingSeat(lens: "supportability", id: "naming/style", severity: "nitpick"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("supportability")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: naming/style (nitpick) src/Foo.cs:12", lines);
        Assert.Contains("fix round: 1 item(s) at fix level nitpick; fix these, then run a verifier cycle.", lines);
    }

    /// <summary>At every other level a nitpick is dropped, listed in the report and routed nowhere else.</summary>
    [Theory]
    [InlineData("critical")]
    [InlineData("major")]
    [InlineData("minor")]
    public void BelowTheNitpickLevelANitpickIsDroppedAndListedInTheReportOnly(string level)
    {
        UsePolicy($"    fix-level: {level}\n");
        WriteSeat("supportability", FindingSeat(lens: "supportability", id: "naming/style", severity: "nitpick"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("supportability")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(MessagesWithCode(run.Output, "KW-REVIEW-055"), line => line.StartsWith("Dropped as nitpick", StringComparison.Ordinal) && line.Contains("naming/style", StringComparison.Ordinal));
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.DoesNotContain(lines, line => line.Contains("naming/style", StringComparison.Ordinal));
        Assert.Contains($"fix round: none (fix level {level}); review clean: proceed to archive.", lines);
    }

    private const string KindRouting = "KW-REVIEW-063";

    /// <summary>
    /// A plan objection never takes a fix round: it becomes follow-up work, and its quoted basis is the recorded reason.
    /// </summary>
    [Fact]
    public void APlanObjectionIsFollowUpWorkAndItsBasisIsTheReason()
    {
        WriteSeat("documentation-accuracy", FindingSeat(
            lens: "documentation-accuracy",
            id: "documentation-accuracy/plan-t2",
            severity: "critical",
            kind: "plan-objection",
            basis: "plan T2 says the selector stays in Application."));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("documentation-accuracy")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(
            MessagesWithCode(run.Output, KindRouting),
            line => line.Contains(
                "'documentation-accuracy/plan-t2': pr note: plan objection: plan T2 says the selector stays in Application.",
                StringComparison.Ordinal));
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("follow-up: documentation-accuracy/plan-t2 (critical) src/Foo.cs:12 (pr note)", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("fix:", StringComparison.Ordinal));
        Assert.Contains("fix round: none (fix level minor); review clean: proceed to archive.", lines);
    }

    /// <summary>
    /// A cycle 3 hardening is dropped whatever its severity, so a change whose only findings are a hardening and a plan
    /// objection is approved, with both listed as follow-up work and neither as a fix.
    /// </summary>
    [Fact]
    public void OnlyDroppedFindingsLeaveTheVerdictApprove()
    {
        string cycle = OutCycleFolder(3);
        _services.Files.CreateDirectory(cycle);
        WriteSeat("correctness", FindingSeat(severity: "critical", kind: "hardening"));
        WriteSeat("documentation-accuracy", FindingSeat(
            lens: "documentation-accuracy",
            id: "documentation-accuracy/plan-d4",
            severity: "major",
            kind: "plan-objection",
            basis: "plan D4 names the decision model as optional."));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--out", Path.Combine(cycle, "findings.json"), .. NotSeatedExcept("correctness", "documentation-accuracy")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(MessagesWithCode(run.Output, "KW-REVIEW-024"), line => line.StartsWith("APPROVE", StringComparison.Ordinal));
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("follow-up: correctness/off-by-one (critical) src/Foo.cs:12 (pr note)", lines);
        Assert.Contains("follow-up: documentation-accuracy/plan-d4 (major) src/Foo.cs:12 (pr note)", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("fix:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A security lens finding is always a defect, so a critical hardening it reports is still fixed in cycle 3, when
    /// the same kind from any other lens would be dropped.
    /// </summary>
    [Fact]
    public void ASecurityHardeningIsKeptAsADefectInCycle3()
    {
        string cycle = OutCycleFolder(3);
        _services.Files.CreateDirectory(cycle);
        WriteSeat("security", FindingSeat(lens: "security", id: "security/key-in-argv", severity: "critical", kind: "hardening"));
        ScriptChange();

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--out", Path.Combine(cycle, "findings.json"), .. NotSeatedExcept("security")]);

        Assert.Equal(1, run.Result);
        Assert.DoesNotContain(MessagesWithCode(run.Output, KindRouting), line => line.StartsWith("Dropped", StringComparison.Ordinal));
        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: security/key-in-argv (critical) src/Foo.cs:12", lines);
    }

    /// <summary>A seat that wrote no kind is read as a defect, and each such finding draws its own warning.</summary>
    [Fact]
    public void AFindingsWithNoKindIsKeptWithAWarning()
    {
        WriteSeat("correctness", FindingSeat(severity: "major"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(
            MessagesWithCode(run.Output, KindRouting),
            line => line == "'correctness/off-by-one': the seat wrote no kind; read as a defect.");
        Assert.Contains(
            MessagesWithCode(run.Output, KindRouting),
            line => line.Contains("routed as cycle 1", StringComparison.Ordinal));
        Assert.Contains("fix: correctness/off-by-one (major) src/Foo.cs:12", MessagesWithCode(run.Output, FixRound));
    }

    /// <summary>With no cycle path of its own, the verdict reads the cycle number from the <c>--prior</c> folder.</summary>
    [Fact]
    public void APriorCycleNamesTheCurrentCycleWhenNoOutPathDoes()
    {
        WriteCycles(Minors(0), Minors(0));
        WriteSeat("correctness", FindingSeat(severity: "critical", kind: "hardening"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", CycleFolder(2), .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(
            MessagesWithCode(run.Output, KindRouting),
            line => line.Contains("pr note: hardening outside the cycle 3 schedule.", StringComparison.Ordinal));
        Assert.Contains(MessagesWithCode(run.Output, "KW-REVIEW-024"), line => line.StartsWith("APPROVE", StringComparison.Ordinal));
    }

    /// <summary>
    /// The findings document keeps only what the verdict kept, so the next cycle's recheck, which reads it and filters
    /// by the fix level, never re-checks a finding dropped by its kind.
    /// </summary>
    [Fact]
    public void TheNextCycleRecheckExcludesADroppedHardeningAndPlanObjection()
    {
        string cycle = OutCycleFolder(1);
        _services.Files.CreateDirectory(cycle);
        string outPath = Path.Combine(cycle, "findings.json");
        WriteSeat("correctness", FindingSeat(severity: "minor", kind: "hardening"));
        WriteSeat("documentation-accuracy", FindingSeat(
            lens: "documentation-accuracy",
            id: "documentation-accuracy/plan-t2",
            severity: "major",
            kind: "plan-objection",
            basis: "plan T2 says the selector stays in Application."));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--out", outPath, .. NotSeatedExcept("correctness", "documentation-accuracy")]);

        Assert.Equal(0, run.Result);
        FindingsReport stored = ReviewJson.ReadFindings(_services.Files.ReadAllText(outPath));
        // The next cycle's recheck is this document filtered by the fix level (ReviewCycleSelector.ReadRecheck).
        Assert.Empty(stored.Findings);
        Assert.DoesNotContain(stored.Findings, new ReviewPolicy().FixRoundTakes);
    }

    /// <summary>
    /// A round that continues writes one ready packet beside the cycle's findings and prints its dispatch block, so the
    /// conductor sends it unchanged instead of paraphrasing the <c>fix:</c> line.
    /// </summary>
    [Fact]
    public void AContinuingRoundWritesOneFixPacketAndPrintsItsDispatchBlock()
    {
        string cycle = CycleFolder(3);
        _services.Cycles.WriteIdentity(cycle, Identity);
        WriteFile("repo/docs/plans/demo.md", "---\nid: demo\ntitle: Demo\ndoc-type: plan\nstatus: Ready\n---\n\n# Demo\n");
        WriteFile(
            "repo/docs/plans/demo.tasks.yml",
            "schema-version: 1\nplan: docs/plans/demo.md\ntasks:\n  - id: T1\n    files:\n      - src/Foo.cs\n");
        ScriptChange("docs/plans/demo.md", "src/Foo.cs");
        WriteSeat("correctness", FindingSeat(severity: "major"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--out", Path.Combine(cycle, "findings.json"), .. NotSeatedExcept("correctness")]);

        Assert.Contains(MessagesWithCode(run.Output, FixRound), block =>
            block.StartsWith("--- fix T1 ---", StringComparison.Ordinal)
            && block.Contains("KYBER-ARBITER: true", StringComparison.Ordinal)
            && block.Contains("PLAN_FILE: docs/plans/demo.md", StringComparison.Ordinal)
            && block.Contains("TASK: T1", StringComparison.Ordinal)
            && block.Contains($"Fix packet: {Path.Combine(cycle, "fix-packets", "task-T1.md")}", StringComparison.Ordinal)
            && block.EndsWith("--- end ---", StringComparison.Ordinal));
        Assert.True(_services.Files.FileExists(Path.Combine(cycle, "fix-packets", "task-T1.md")));
    }

    /// <summary>A clean review sends nothing to a fix round, so it writes no packet.</summary>
    [Fact]
    public void ACleanRoundWritesNoFixPackets()
    {
        string cycle = CycleFolder(3);
        _services.Cycles.WriteIdentity(cycle, Identity);
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--out", Path.Combine(cycle, "findings.json"), .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.False(_services.Files.DirectoryExists(Path.Combine(cycle, "fix-packets")));
    }

    /// <summary>A capped loop sends its findings to human review, so it writes no packet either.</summary>
    [Fact]
    public void ACappedRoundWritesNoFixPackets()
    {
        WriteCycles(Minors(4), Minors(3), Minors(2), Minors(1));
        string cycle = CycleFolder(5);
        _services.Cycles.WriteIdentity(cycle, Identity);
        WriteSeat("security", FindingSeat(lens: "security", id: "security/m1", severity: "minor"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", CycleFolder(4), "--out", Path.Combine(cycle, "findings.json"),
                .. NotSeatedExcept("security")]);

        Assert.Contains(MessagesWithCode(run.Output, FixRound), line => line.StartsWith("open:", StringComparison.Ordinal));
        Assert.False(_services.Files.DirectoryExists(Path.Combine(cycle, "fix-packets")));
    }

    /// <summary>
    /// The first verdict of a cycle runs before the cycle records its change, so the packet writer must still name
    /// the cycle folder from <c>--out</c> and write the packets the normal flow dispatches from.
    /// </summary>
    [Fact]
    public void AContinuingRoundWritesPacketsBeforeTheCycleRecordsItsIdentity()
    {
        string cycle = OutCycleFolder(3);
        _services.Files.CreateDirectory(cycle);
        WriteSeat("correctness", FindingSeat(severity: "major"));
        ScriptChange();

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--out", Path.Combine(cycle, "findings.json"), .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.True(_services.Files.FileExists(Path.Combine(cycle, "fix-packets", "unassigned.md")));
        Assert.DoesNotContain(
            MessagesWithCode(run.Output, FixRound),
            line => line.Contains("the cycle folder could not be named", StringComparison.Ordinal));
    }

    /// <summary>
    /// A continuing round that cannot name a cycle folder writes no packet, so it says so instead of leaving the
    /// conductor to guess why no dispatch block followed the <c>fix:</c> lines.
    /// </summary>
    [Fact]
    public void AContinuingRoundWithNoCycleFolderWarnsThatThePacketsWereNotWritten()
    {
        WriteSeat("correctness", FindingSeat(severity: "major"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--out", Path.Combine(_services.Root, "report.json"), .. NotSeatedExcept("correctness")]);

        AssertDiagnostic(
            run.Output,
            FixRound,
            "warning",
            "fix packets: not written — the cycle folder could not be named (pass --out inside the cycle folder); " +
            "dispatch from the fix: lines and findings.json");
        Assert.DoesNotContain(
            MessagesWithCode(run.Output, FixRound),
            line => line.StartsWith("--- fix ", StringComparison.Ordinal));
    }

    /// <summary>Scripts the change the packet writer reads: a merge base, the changed paths and no untracked file.</summary>
    private void ScriptChange(params string[] paths)
    {
        string status = string.Concat(paths.Select(path => $"M\0{path}\0"));
        _ = _services.Processes
            .Script("git", new ProcessRunResult(0, "base123\n", false), "merge-base", "origin/HEAD", "HEAD")
            .Script("git", new ProcessRunResult(0, status, false), "diff", "-z", "--name-status", "-M", "--relative", "base123", "--")
            .Script("git", new ProcessRunResult(0, string.Empty, false), "ls-files", "-z", "--others", "--exclude-standard", "--");
    }

    private string CycleFolder(int number) => Path.Combine(_services.Root, "cycles", $"cycle-{number}");

    /// <summary>A cycle folder the verdict can name its cycle from: its parent is the review folder.</summary>
    private string OutCycleFolder(int number) => Path.Combine(_services.Root, "review", $"cycle-{number}");

    /// <summary>The ids of <paramref name="count"/> distinct minor findings.</summary>
    private static string[] Minors(int count) => [.. Enumerable.Range(1, count).Select(index => $"security/m{index}")];

    /// <summary>Writes finished cycles 1..n, each holding the minor findings named, recorded as this change's.</summary>
    private void WriteCycles(params string[][] findingIdsPerCycle)
    {
        for (int index = 0; index < findingIdsPerCycle.Length; index++)
        {
            string folder = CycleFolder(index + 1);
            _services.Cycles.WriteIdentity(folder, Identity);
            JsonArray findings = [.. findingIdsPerCycle[index].Select((id, position) =>
            {
                JsonObject finding = PriorFinding();
                finding["id"] = id;
                finding["line"] = 100 + position;
                finding["lens"] = "security";
                finding["severity"] = "minor";
                finding["excerpt"] = "if (i < count)";
                finding["claim"] = "The name does not say what it counts.";
                finding["evidence"] = "src/Foo.cs";
                finding["failureScenario"] = "A reader takes the count for a total.";
                return (JsonNode)finding;
            })];
            JsonObject document = new()
            {
                ["schema"] = "kyber-weave.review-findings/v1",
                ["findings"] = findings,
                ["changedPaths"] = new JsonArray(),
                ["changedLines"] = 0
            };
            _services.Files.WriteAllText(Path.Combine(folder, "findings.json"), document.ToJsonString());
        }
    }

    private void UsePolicy(string extra) =>
        _services.Files.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n" + extra);
}
