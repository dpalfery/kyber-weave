using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     <see cref="CouncilSeating" /> decides which council seats start at once, which wait for the gates, and which a
///     file-pattern condition the lens catalogue states rules out; and it writes each seat's packet and dispatch block.
/// </summary>
public sealed class CouncilSeatingTests
{
    [Fact]
    public void GateReadingLensesWaitForTheGatesAndTheRestStartAtOnce()
    {
        CouncilSeatPlan plan = CouncilSeating.Plan(["src/A.cs", "tests/ATests.cs", "package.json", ".github/workflows/ci.yml"]);

        Assert.Equal(["duplicate-implementation", "static-analysis-triage", "test-adequacy"], plan.AfterGates);
        Assert.Contains("correctness", plan.Now);
        Assert.Contains("dependency-supply-chain", plan.Now);
        Assert.Contains("infra-workflow", plan.Now);
        Assert.Contains("test-infrastructure", plan.Now);
        Assert.Empty(plan.NotSeated);
        Assert.Equal(ReviewLensCatalogue.Names.Count, plan.Now.Count + plan.AfterGates.Count);
    }

    [Theory]
    [InlineData("dependency-supply-chain", "src/Directory.Packages.props")]
    [InlineData("dependency-supply-chain", "web/pnpm-lock.yaml")]
    [InlineData("dependency-supply-chain", "Dockerfile")]
    [InlineData("infra-workflow", ".github/workflows/release.yml")]
    [InlineData("infra-workflow", "db/Migrations/20260101_AddJobs.cs")]
    [InlineData("infra-workflow", "deploy/main.bicep")]
    [InlineData("test-infrastructure", "tests/Fixtures/Builder.cs")]
    [InlineData("test-infrastructure", "web/src/app.spec.ts")]
    public void APatternLensIsSeatedWhenOneChangedFileMatchesItsCatalogueCondition(string lens, string path)
    {
        CouncilSeatPlan plan = CouncilSeating.Plan(["src/Service.cs", path]);

        Assert.DoesNotContain(lens, plan.NotSeated.Keys);
        Assert.Contains(lens, plan.Now);
    }

    [Fact]
    public void APatternLensIsNotSeatedWhenNoChangedFileMatchesAndTheReasonSaysWhy()
    {
        CouncilSeatPlan plan = CouncilSeating.Plan(["src/Jobs/JobService.cs", "src/Utils/Infrastructure/Clock.cs"]);

        Assert.Contains("dependency-supply-chain", plan.NotSeated.Keys);
        Assert.Contains("infra-workflow", plan.NotSeated.Keys);
        Assert.Contains("test-infrastructure", plan.NotSeated.Keys);
        Assert.Contains("catalogue applicability", plan.NotSeated["infra-workflow"], StringComparison.Ordinal);
        Assert.DoesNotContain("infra-workflow", plan.Now);
        Assert.Contains("correctness", plan.Now);
        Assert.Contains("test-adequacy", plan.AfterGates);
    }

    [Fact]
    public void ADocumentationOnlyChangeSeatsNeitherCorrectnessNorTestAdequacy()
    {
        CouncilSeatPlan plan = CouncilSeating.Plan(["docs/guide.md", "README.md"]);

        Assert.Contains("correctness", plan.NotSeated.Keys);
        Assert.Contains("test-adequacy", plan.NotSeated.Keys);
        Assert.Contains("documentation-accuracy", plan.Now);
    }

    [Fact]
    public void AnUnknownChangedSetSeatsEveryLens()
    {
        CouncilSeatPlan plan = CouncilSeating.Plan(null);

        Assert.Empty(plan.NotSeated);
        Assert.Equal(ReviewLensCatalogue.Names.Count, plan.Now.Count + plan.AfterGates.Count);
    }

    [Fact]
    public void StaticAnalysisTriageIsNotSeatedOnlyWhenEveryGateThatRanPassed()
    {
        GateReport passed = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "ok"), new GateResult("docs", true, 0, "n/a", NotApplicableReason: "no match")]);
        GateReport failed = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "ok"), new GateResult("test", true, 1, "1 failed")]);

        Assert.Equal("gates build passed", CouncilSeating.GateProvenSkips(passed, null, ["a.cs"])["static-analysis-triage"]);
        Assert.DoesNotContain("static-analysis-triage", CouncilSeating.GateProvenSkips(failed, null, ["a.cs"]).Keys);
        Assert.DoesNotContain("static-analysis-triage", CouncilSeating.GateProvenSkips(new GateReport(GateReport.CurrentSchema, []), null, ["a.cs"]).Keys);
    }

    [Fact]
    public void DuplicateImplementationIsNotSeatedOnlyWhenNoClusterNamesAChangedFile()
    {
        GateReport gates = new(GateReport.CurrentSchema, [new GateResult("build", true, 1, "failed")]);
        DuplicateReport clusters = Duplicates(available: true, "src/Other.cs", "src/Third.cs");
        DuplicateReport touching = Duplicates(available: true, "src/A.cs", "src/Third.cs");

        Assert.Equal(
            "no duplicate cluster names a changed file",
            CouncilSeating.GateProvenSkips(gates, clusters, ["src/A.cs"])["duplicate-implementation"]);
        Assert.DoesNotContain("duplicate-implementation", CouncilSeating.GateProvenSkips(gates, touching, ["src/A.cs"]).Keys);
        Assert.DoesNotContain("duplicate-implementation", CouncilSeating.GateProvenSkips(gates, Duplicates(available: false), ["src/A.cs"]).Keys);
    }

    [Fact]
    public void APacketCarriesTheCharterTheEvidenceAndTheGuidanceSoTheSeatOpensNothingElseToStart()
    {
        CouncilSeatContext context = new("/repo", "/repo/.agent_scratchpad/t/review/cycle-1/evidence.md", false, "strict", 5, "/repo/REVIEW.md", "/repo/dup.json");

        string packet = CouncilSeating.Packet(context, "correctness");

        Assert.Contains("- Evidence: /repo/.agent_scratchpad/t/review/cycle-1/evidence.md", packet, StringComparison.Ordinal);
        Assert.Contains("- projectPath: /repo", packet, StringComparison.Ordinal);
        Assert.Contains("Index fallback: projectPath: not-indexed", packet, StringComparison.Ordinal);
        Assert.Contains("- Strictness: strict (min-confidence 5)", packet, StringComparison.Ordinal);
        Assert.Contains("- Review guidance: /repo/REVIEW.md.", packet, StringComparison.Ordinal);
        Assert.Contains("# Lens: correctness", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicates report", packet, StringComparison.Ordinal);
        Assert.Contains("- Duplicates report: /repo/dup.json", CouncilSeating.Packet(context, "duplicate-implementation"), StringComparison.Ordinal);
    }

    [Fact]
    public void ADispatchBlockCarriesTheRoutingHeaderThePacketAndOnlyInHandBackTheSeatFile()
    {
        string block = CouncilSeating.DispatchBlock("dependency-supply-chain", "run-1", "/c/packets/dependency-supply-chain.md", null);

        Assert.Equal(
            "--- seat dependency-supply-chain agent=review-triage ---\nKYBER-ARBITER: true\nLENS: dependency-supply-chain\n" +
            "REVIEW_RUN: run-1\n\nPacket: /c/packets/dependency-supply-chain.md\n--- end ---\n",
            block.ReplaceLineEndings("\n"));
        Assert.Contains(
            "Seat file: /c/seats/security.md",
            CouncilSeating.DispatchBlock("security", "run-1", "/c/packets/security.md", "/c/seats/security.md"),
            StringComparison.Ordinal);
    }

    private static DuplicateReport Duplicates(bool available, params string[] files) =>
        new(
            DuplicateReport.CurrentSchema,
            "/repo/.codegraph/codegraph.db",
            available,
            null,
            available ? null : "no index",
            5,
            0,
            0,
            files.Length == 0
                ? []
                : [new DuplicateCluster("dup-1", 6, [.. files.Select(file => new DuplicateMember("M", "N.M", file, 1, 6))])]);
}
