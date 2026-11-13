using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the fallback sections (packet 17.4) the conductor and the review council follow when
/// they hold <c>arbiter_evaluate_agent_dispatch_or_return</c> instead of a hook: the tool, marker
/// and status tokens they name, the review call points, the no-routing-header rule, and no harness
/// names (the signal is the tool, not the harness). Keying on the tool, handling notes as a hook
/// would, and status provenance are graded from the prose rule catalog.
/// </summary>
public sealed class ArbiterFallbackContractTests
{
    private const string FallbackHeading = "## Arbiter fallback";

    private static readonly string[] HarnessNames =
    [
        "claude",
        "copilot",
        "cursor",
        "codex",
        "opencode",
        "antigravity",
        "devin",
        "zcode",
        "factory",
        "warp",
        "pi",
    ];

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string ConductorContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "conductor.md"));

    private static string ReviewerContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "code-reviewer.md"));

    private static string SkillContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "skills", "code-review", "SKILL.md"));

    [Fact]
    public void ConductorFallback_NamesArbiterEvaluate()
    {
        Assert.Contains("arbiter_evaluate_agent_dispatch_or_return", FallbackSection(ConductorContract), StringComparison.Ordinal);
    }

    [Fact]
    public void ConductorFallback_WritesNoRoutingHeadersIntoTheDispatch()
    {
        string section = FallbackSection(ConductorContract);

        Assert.Contains("no routing header", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConductorFallback_NamesTheEscalationStatus()
    {
        Assert.Contains("STATUS: ARBITER_ESCALATION", FallbackSection(ConductorContract), StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerFallback_CallsArbiterEvaluateOnceBeforeTheLensFanOutWithTheLensList()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("arbiter_evaluate_agent_dispatch_or_return", section, StringComparison.Ordinal);
        Assert.Contains("once before the lens fan-out", section, StringComparison.Ordinal);
        Assert.Contains("list of lenses", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerFallback_CallsArbiterEvaluateOnceBeforeTheRefutationFanOut()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("once before the refutation fan-out", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerFallback_WritesNoRoutingHeadersIntoTheDispatch()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("no routing header", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewerFallback_NamesEveryReviewNoteStatus()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("STATUS: ARBITER_SKIP", section, StringComparison.Ordinal);
        Assert.Contains("STATUS: ARBITER_VERIFIED", section, StringComparison.Ordinal);
        Assert.Contains("STATUS: ARBITER_ANNOTATION", section, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillFallback_MirrorsTheReviewerCallPointsAndWritesNoRoutingHeader()
    {
        string section = FallbackSection(SkillContract);

        Assert.Contains("before the lens fan-out", section, StringComparison.Ordinal);
        Assert.Contains("before the refutation fan-out", section, StringComparison.Ordinal);
        Assert.Contains("no routing header", section, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("conductor")]
    [InlineData("code-reviewer")]
    [InlineData("code-review")]
    public void FallbackSections_NameNoHarness(string document)
    {
        string section = FallbackSection(Contract(document));

        foreach (string harness in HarnessNames)
        {
            Assert.DoesNotMatch(
                $@"\b{Regex.Escape(harness)}\b",
                section);
        }
    }

    /// <summary>
    /// The fallback replaces every routing marker the path contracts define, so the
    /// sentence that says so has to name them. It used to point at a "routing headers
    /// section above" that does not exist in this file, and listed three of the eight.
    /// </summary>
    [Theory]
    [InlineData("KYBER-ARBITER")]
    [InlineData("PLAN_FILE:")]
    [InlineData("TASK:")]
    [InlineData("INTAKE:")]
    [InlineData("FEATURE:")]
    [InlineData("PHASE:")]
    [InlineData("FINALIZE")]
    [InlineData("FINDINGS:")]
    public void ConductorFallback_NamesEveryRoutingMarkerTheCallReplaces(string marker)
    {
        Assert.Contains(marker, FallbackSection(ConductorContract), StringComparison.Ordinal);
    }

    /// <summary>
    /// The fallback names every contract whose markers it replaces, so a reader can find the rule
    /// the call stands in for. The execution rules moved into the shared <c>delivery-execution</c>
    /// skill; the path contracts keep their own references beside the conductor.
    /// </summary>
    /// <remarks>
    /// Each contract is named, not linked by a path into another directory: the conductor's
    /// body ships to targets that mount it away from this tree, so a relative link out of
    /// <c>agents/</c> would break there. The names are what a reader follows.
    /// </remarks>
    [Fact]
    public void ConductorFallback_PointsAtTheContractThatDefinesTheMarkers()
    {
        string section = FallbackSection(ConductorContract);

        Assert.DoesNotContain("section above", section, StringComparison.Ordinal);
        foreach (string contract in new[]
        {
            "`delivery-execution` skill",
            "intake-path contract",
            "plan-path contract",
            "spec-path contract",
        })
        {
            Assert.Contains(contract, section, StringComparison.Ordinal);
        }
    }

    private static string Contract(string document) => document switch
    {
        "conductor" => ConductorContract,
        "code-reviewer" => ReviewerContract,
        _ => SkillContract,
    };

    private static string FallbackSection(string document)
    {
        int start = document.IndexOf(FallbackHeading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Document has no '{FallbackHeading}' section.");

        int end = document.IndexOf("\n## ", start + FallbackHeading.Length, StringComparison.Ordinal);
        return end < 0 ? document[start..] : document[start..end];
    }
}
