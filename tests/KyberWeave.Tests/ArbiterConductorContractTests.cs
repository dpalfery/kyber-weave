using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the conductor Arbiter contract's protocol tokens (packet 7.1): the routing header
/// block and planner markers, the escalation <c>STATUS</c>, <c>NEXT</c> and <c>REPEAT</c>
/// tokens, and target-neutral wording. The prose rules around them (whole-context packets,
/// escalation handling, the fallback override, repository-relative paths) are graded from
/// the prose rule catalog.
/// </summary>
public sealed class ArbiterConductorContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string ConductorContract => ReadConductorContract();

    [Fact]
    public void Dispatches_StartWithKyberArbiterMarker()
    {
        Assert.Contains("KYBER-ARBITER: true", ConductorContract, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskDispatches_CarryPlanFileAndTask()
    {
        string contract = ConductorContract;

        Assert.Contains("PLAN_FILE:", contract, StringComparison.Ordinal);
        Assert.Contains("TASK:", contract, StringComparison.Ordinal);
        Assert.Contains("review-triage", contract, StringComparison.Ordinal);
        Assert.Contains("docs-dev", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerDispatches_CarryMarkersAndNeverTask()
    {
        string contract = ConductorContract;

        Assert.Contains("INTAKE:", contract, StringComparison.Ordinal);
        Assert.Contains("FINALIZE", contract, StringComparison.Ordinal);
        Assert.Contains("FINDINGS:", contract, StringComparison.Ordinal);
        Assert.Contains("STATUS: ARBITER_ESCALATION", contract, StringComparison.Ordinal);
        Assert.Contains("FEATURE:", contract, StringComparison.Ordinal);
        Assert.Contains("PHASE:", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void Escalations_NameTheStatusNextAndRepeatTokens()
    {
        string contract = ConductorContract;

        Assert.Contains("STATUS: ARBITER_ESCALATION", contract, StringComparison.Ordinal);
        Assert.Contains("NEXT", contract, StringComparison.Ordinal);
        Assert.Contains("REPEAT: 1", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanFileHeaders_HaveNoInvalidPathStatus()
    {
        // A PLAN_FILE spelled any other way is not read as a header, and the Arbiter falls back to the file on
        // disk or its decision model (ADR 0072); there is no plan-file-invalid status for the conductor to handle.
        Assert.DoesNotContain("plan-file-invalid", ConductorContract, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_NamesNoHarness()
    {
        string contract = ConductorContract;

        foreach (string harness in new[]
        {
            "claude",
            "copilot",
            "cursor",
            "codex",
            "opencode",
            "antigravity",
            "devin",
            "zcode",
            "factory",
        })
        {
            Assert.DoesNotContain(harness, contract, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ReadConductorContract()
    {
        string agentRoot = Path.Combine(ProductRoot, "agents");
        string principal = Path.Combine(agentRoot, "conductor.md");
        string referencesRoot = Path.Combine(agentRoot, "conductor", "references");
        string[] files =
        [
            principal,
            Path.Combine(referencesRoot, "plan-path.md"),
            Path.Combine(referencesRoot, "spec-path.md"),
            Path.Combine(referencesRoot, "intake-path.md"),
            Path.Combine(ProductRoot, "skills", "delivery-execution", "SKILL.md"),
        ];

        return string.Join("\n", files.Select(path => StripFrontmatter(File.ReadAllText(path))));
    }

    private static string StripFrontmatter(string text)
    {
        // The frontmatter schema key `copilot-tools` predates this contract and is not
        // prose naming a deployment target; scope the harness check to instruction text.
        const string delimiter = "---\n";
        if (!text.StartsWith(delimiter, StringComparison.Ordinal))
        {
            return text;
        }

        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        return end < 0 ? text : text[(end + 5)..];
    }
}
