using System.Text.Json.Nodes;
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

    private string CycleFolder(int number) => Path.Combine(_services.Root, "cycles", $"cycle-{number}");

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
