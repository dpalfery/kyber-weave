using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the conductor Arbiter contract (packet 7.1): the routing header block and
/// planner markers, the worker packet instruction, escalation handling by
/// <c>NEXT</c> and <c>REPEAT</c>, and target-neutral wording.
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
        Assert.Contains("task-reviewer", contract, StringComparison.Ordinal);
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
        Assert.Contains("never", contract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerPackets_AreWholeContextAndForbidPlanSpecOrTodoAccess()
    {
        string contract = ConductorContract;

        Assert.Contains("whole context", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not open plan", contract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escalations_FollowNextAndNeverRetryUnchanged()
    {
        string contract = ConductorContract;

        Assert.Contains("STATUS: ARBITER_ESCALATION", contract, StringComparison.Ordinal);
        Assert.Contains("NEXT", contract, StringComparison.Ordinal);
        Assert.Contains("REPEAT: 1", contract, StringComparison.Ordinal);
        Assert.Contains("dispatch architect with", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run finding", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not retry", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-issue", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recognised marker", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("malformed", contract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceHeaderRules_QualifyEveryDispatchWithTheFallbackOverride()
    {
        // Review 20.1, item a: the references stated the header unconditionally while
        // conductor.md allows the arbiter_evaluate fallback to replace it.
        string referencesRoot = Path.Combine(ProductRoot, "agents", "conductor", "references");
        foreach (string file in new[] { "execution-and-review.md", "plan-path.md", "spec-path.md", "intake-path.md" })
        {
            string text = File.ReadAllText(Path.Combine(referencesRoot, file));
            Assert.Contains("Unless you hold `arbiter_evaluate`", text, StringComparison.Ordinal);
        }
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
            Path.Combine(referencesRoot, "execution-and-review.md"),
            Path.Combine(referencesRoot, "plan-path.md"),
            Path.Combine(referencesRoot, "spec-path.md"),
            Path.Combine(referencesRoot, "intake-path.md"),
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
