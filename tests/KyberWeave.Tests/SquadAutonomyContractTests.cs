using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Checks the protocol tokens, markers and file references of the <c>autonomy</c> contract across
/// the portable Squad text. The value is a repository default (Config Reg <c>&lt;autonomy&gt;</c>)
/// overridable per run. The prose rules themselves (resolution order, hard stops, supervised gates,
/// hard-block repair, autonomous approval) are graded from the prose rule catalog
/// (<c>Fixtures/prose-rules/catalog.yml</c>).
/// </summary>
public sealed class SquadAutonomyContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string Read(params string[] relative)
    {
        return File.ReadAllText(Path.Combine([ProductRoot, .. relative]));
    }

    private static string ConductorContract()
    {
        return string.Join(
            "\n",
            Read("agents", "conductor.md"),
            Read("agents", "conductor", "references", "autonomy.md"),
            Read("agents", "conductor", "references", "intake-path.md"),
            Read("agents", "conductor", "references", "plan-path.md"),
            Read("agents", "conductor", "references", "spec-path.md"),
            Read("skills", "delivery-execution", "SKILL.md"));
    }

    private static void AssertAll(string text, params string[] phrases)
    {
        foreach (string phrase in phrases)
        {
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConductorResolvesAutonomyFromPromptThenConfigRegThenSupervised()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");
        string conductor = Read("agents", "conductor.md");

        AssertAll(autonomy,
            "autonomy: supervised|autonomous",
            "`development-mode`");
        Assert.Contains("conductor/references/autonomy.md", conductor, StringComparison.Ordinal);
        Assert.True(
            autonomy.IndexOf("autonomy: supervised|autonomous", StringComparison.Ordinal) <
            autonomy.IndexOf("**<autonomy>**", StringComparison.Ordinal),
            "Resolution order must read the prompt directive, then the Config Reg property.");
    }

    [Fact]
    public void ConductorAutonomousModeRecordsProvenanceInsteadOfAsking()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");

        AssertAll(autonomy,
            "INTAKE_RECOMMENDATION",
            "autonomous: recommendation accepted",
            "`## Decisions`",
            "ESCALATION: hard_block",
            "SOURCES_SEARCHED",
            "COST_OF_WRONG",
            "RECOMMENDED");
    }

    [Fact]
    public void ConductorKeepsOnlyTrueHardStopsHumanInAutonomousMode()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");

        AssertAll(autonomy,
            "Hard stops",
            "RECOMMENDED: none",
            "STATUS: BLOCKED");
    }

    [Fact]
    public void AutonomyAndExecutionNeverEmitTheRetiredNeedsHumanVerdict()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");
        string execution = Read("skills", "delivery-execution", "SKILL.md");

        Assert.DoesNotContain("NEEDS_HUMAN", autonomy, StringComparison.Ordinal);
        Assert.DoesNotContain("NEEDS_HUMAN", execution, StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomyCarriesTheLowConfidenceMarker()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");

        Assert.Contains("autonomous: low-confidence", autonomy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agents/architect/references/autonomy-decisions.md", "agents/architect.md")]
    [InlineData("skills/product-owner/references/autonomy-decisions.md", "skills/product-owner/SKILL.md")]
    public void SpecialistsCarryTheLowConfidenceAndEscalationMarkers(string reference, string owner)
    {
        string text = Read(reference.Split('/'));

        AssertAll(text,
            "autonomous: low-confidence, no authoritative source",
            "ESCALATION: hard_block",
            "SOURCES_SEARCHED",
            "COST_OF_WRONG",
            "RECOMMENDED");
        Assert.Contains(
            Path.GetFileName(reference),
            Read(owner.Split('/')),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BugCrusherEscalationFollowsTheSameAutonomyRules()
    {
        string skill = Read("skills", "bug-crusher", "SKILL.md");

        AssertAll(skill,
            "**<autonomy>**",
            "ESCALATION: hard_block",
            "autonomous: recommendation accepted");
    }

    [Fact]
    public void ConductorContractNeverEmitsTheRetiredNeedsHumanVerdict()
    {
        Assert.DoesNotContain("NEEDS_HUMAN", ConductorContract(), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedHardBlockFallbackCarriesTheLowConfidenceMarker()
    {
        const string Marker = "autonomous: low-confidence, no authoritative source";

        Assert.Contains(Marker, Read("agents", "conductor", "references", "autonomy.md"), StringComparison.Ordinal);
        Assert.Contains(Marker, Read("skills", "bug-crusher", "SKILL.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConductorNeverWritesApprovalRowsTheSpecialistRecordsAutonomousApproval()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");
        string plan = Read("agents", "conductor", "references", "plan-path.md");
        string spec = Read("agents", "conductor", "references", "spec-path.md");
        string architect = Read("agents", "architect.md");
        string owner = Read("skills", "product-owner", "SKILL.md");

        Assert.Contains("AUTONOMOUS_APPROVAL: <gate id>", autonomy, StringComparison.Ordinal);
        Assert.Contains("AUTONOMOUS_APPROVAL: <gate id>", plan, StringComparison.Ordinal);
        Assert.Contains("AUTONOMOUS_APPROVAL: <gate id>", spec, StringComparison.Ordinal);
        AssertAll(architect, "AUTONOMOUS_APPROVAL", "autonomous: recommendation accepted");
        AssertAll(owner, "AUTONOMOUS_APPROVAL", "autonomous: recommendation accepted");
    }

    [Fact]
    public void AutonomousEscalationShapesCarryEveryRequiredFieldAndTheDigestEnvelope()
    {
        string planAuthoring = Read("agents", "architect", "references", "plan-authoring.md");
        string architectRef = Read("agents", "architect", "references", "autonomy-decisions.md");
        string ownerRef = Read("skills", "product-owner", "references", "autonomy-decisions.md");

        AssertAll(planAuthoring,
            "ESCALATION: hard_block",
            "SOURCES_SEARCHED",
            "COST_OF_WRONG",
            "autonomy-decisions.md");
        AssertAll(architectRef, "STATUS: NEEDS_DECISION", "PLAN_FILE:", "QUESTION:", "OPTIONS:");
        AssertAll(ownerRef,
            "STATUS: READY_FOR_REVIEW",
            "PHASE: requirements | design | tasks",
            "ARTIFACT: <path resolved from <specification-index>>",
            "SUMMARY:",
            "GAPS:",
            "Discovery: codegraph N, docs N, direct reads N",
            "DIRECT_READ_REASONS:");
    }
}
