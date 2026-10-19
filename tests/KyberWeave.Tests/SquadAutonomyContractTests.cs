using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the <c>autonomy</c> contract across the portable Squad text. The value is a
/// repository default (Config Reg <c>&lt;autonomy&gt;</c>) overridable per run, and
/// <c>supervised</c> must keep every human gate it has today.
/// </summary>
public sealed class SquadAutonomyContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine([ProductRoot, .. relative]));

    private static string ConductorContract() => string.Join(
        "\n",
        Read("agents", "conductor.md"),
        Read("agents", "conductor", "references", "autonomy.md"),
        Read("agents", "conductor", "references", "intake-path.md"),
        Read("agents", "conductor", "references", "plan-path.md"),
        Read("agents", "conductor", "references", "spec-path.md"),
        Read("agents", "conductor", "references", "execution-and-review.md"));

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
            "the value declared as **<autonomy>** in the repository root `AGENTS.md` Config Reg",
            "`supervised` when absent",
            "does not reopen approval",
            "next gate",
            "`development-mode`");
        Assert.Contains("conductor/references/autonomy.md", conductor, StringComparison.Ordinal);
        Assert.True(
            autonomy.IndexOf("autonomy: supervised|autonomous", StringComparison.Ordinal) <
            autonomy.IndexOf("**<autonomy>**", StringComparison.Ordinal) &&
            autonomy.IndexOf("**<autonomy>**", StringComparison.Ordinal) <
            autonomy.IndexOf("`supervised` when absent", StringComparison.Ordinal),
            "Resolution order must read prompt, then Config Reg, then the supervised default.");
    }

    [Fact]
    public void ConductorAutonomousModeRecordsProvenanceInsteadOfAsking()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");

        AssertAll(autonomy,
            "INTAKE_RECOMMENDATION",
            "autonomous: recommendation accepted",
            "`## Decisions`",
            "ESCALATION: impactful",
            "SOURCES_SEARCHED",
            "COST_OF_WRONG",
            "RECOMMENDED",
            "approve and execute",
            "rejects an impactful escalation",
            "same question-group shape");
    }

    [Fact]
    public void ConductorKeepsTheFourHardStopsHumanInAutonomousMode()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");

        AssertAll(autonomy,
            "Hard stops",
            "NEEDS_HUMAN",
            "commit, push, publish, open a pull request",
            "circuit-breaker",
            "STATUS: BLOCKED",
            "write error");
    }

    [Fact]
    public void AutonomousNeedsHumanParksTheChangeAndFinishesUnaffectedWork()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");
        string execution = Read("agents", "conductor", "references", "execution-and-review.md");

        AssertAll(autonomy, "pending human review", "park", "never push");
        Assert.Contains("is terminal", execution, StringComparison.Ordinal);
        Assert.Contains("autonomy.md", execution, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalReportListsLowConfidenceDecisionsAndAutoApprovedGates()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");

        AssertAll(autonomy,
            "autonomous: low-confidence",
            "why no source",
            "auto-approved gates");
    }

    [Theory]
    [InlineData("agents/architect/references/autonomy-decisions.md", "agents/architect.md")]
    [InlineData("skills/product-owner/references/autonomy-decisions.md", "skills/product-owner/SKILL.md")]
    public void SpecialistsApplyTheThreeCheckDecisionRuleAndTheEscalationMarker(string reference, string owner)
    {
        string text = Read(reference.Split('/'));

        AssertAll(text,
            "Covered?",
            "Microsoft Learn",
            "research-agent",
            "autonomous: low-confidence, no authoritative source",
            "cheap to reverse",
            "expensive to reverse",
            "already-accepted decision",
            "ESCALATION: impactful",
            "SOURCES_SEARCHED",
            "COST_OF_WRONG",
            "RECOMMENDED",
            "never ask a question just to ask");
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
            "autonomy",
            "**<autonomy>**",
            "ESCALATION: impactful",
            "autonomous: recommendation accepted",
            "approve and execute");
    }

    [Fact]
    public void SupervisedGatesRemainInTheContracts()
    {
        string contract = ConductorContract();

        AssertAll(contract,
            "relay all independent questions",
            "require the user to choose plan or spec",
            "Ask for approval or concrete revision feedback",
            "ask the final blocking question as **approve and execute**",
            "explicitly chooses **approve and execute**",
            "`NEEDS_HUMAN` is terminal",
            "reopens approval of the affected Test contract",
            "Supervised behaviour is unchanged");
        AssertAll(Read("skills", "bug-crusher", "SKILL.md"),
            "Relay its decision digests and final **approve and execute** gate",
            "surface every grouped question");
        AssertAll(Read("agents", "architect.md"), "never prompt the user directly", "**approve and execute**");
        AssertAll(Read("skills", "product-owner", "SKILL.md"), "never prompts the user", "**approve and execute**");
    }

    [Fact]
    public void MalformedImpactfulEscalationIsRepairedOnceNeverDecidedOrAskedOfTheUser()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");
        string bug = Read("skills", "bug-crusher", "SKILL.md");

        AssertAll(autonomy,
            "ONCE to be REPAIRED",
            "same question, same ids",
            "autonomous: low-confidence, no authoritative source",
            "Never ask the user for a malformed block");
        Assert.DoesNotContain("sends it back to the specialist to decide", autonomy, StringComparison.Ordinal);
        AssertAll(bug, "ONCE to be REPAIRED", "autonomous: low-confidence, no authoritative source");
        Assert.DoesNotContain("sending it back to `architect` to decide", bug, StringComparison.Ordinal);
    }

    [Fact]
    public void ConductorNeverWritesApprovalRowsTheSpecialistRecordsAutonomousApproval()
    {
        string autonomy = Read("agents", "conductor", "references", "autonomy.md");
        string plan = Read("agents", "conductor", "references", "plan-path.md");
        string spec = Read("agents", "conductor", "references", "spec-path.md");
        string architect = Read("agents", "architect.md");
        string owner = Read("skills", "product-owner", "SKILL.md");

        AssertAll(autonomy, "AUTONOMOUS_APPROVAL: <gate id>", "never writes the row");
        AssertAll(plan, "AUTONOMOUS_APPROVAL: <gate id>", "`architect` records");
        AssertAll(spec, "AUTONOMOUS_APPROVAL: <gate id>", "`product-owner` records");
        Assert.DoesNotContain("conductor supplies the approval and records it", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("record the approval yourself", spec, StringComparison.Ordinal);
        AssertAll(architect, "Never infer approval", "AUTONOMOUS_APPROVAL", "autonomous: recommendation accepted", "from silence or from the mode alone");
        AssertAll(owner, "AUTONOMOUS_APPROVAL", "autonomous: recommendation accepted", "from silence or from the mode alone");
        Assert.DoesNotContain("conductor supplies those approvals itself", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomousEscalationShapesCarryEveryRequiredFieldAndTheDigestEnvelope()
    {
        string planAuthoring = Read("agents", "architect", "references", "plan-authoring.md");
        string architectRef = Read("agents", "architect", "references", "autonomy-decisions.md");
        string ownerRef = Read("skills", "product-owner", "references", "autonomy-decisions.md");

        AssertAll(planAuthoring,
            "ESCALATION: impactful",
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
