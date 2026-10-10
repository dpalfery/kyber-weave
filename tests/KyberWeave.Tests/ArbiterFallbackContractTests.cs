using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the fallback contract text (packet 17.4): what the conductor and the review
/// council do when they hold <c>arbiter_evaluate</c> instead of a hook. The signal is the
/// tool, not the harness, so the fallback sections name no harness.
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
    public void ConductorFallback_CallsArbiterEvaluateBeforeEachDispatchAndAfterEachReturn()
    {
        string section = FallbackSection(ConductorContract);

        Assert.Contains("arbiter_evaluate", section, StringComparison.Ordinal);
        Assert.Contains("before each dispatch", section, StringComparison.Ordinal);
        Assert.Contains("after each return", section, StringComparison.Ordinal);
        Assert.Contains("routing facts", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ConductorFallback_KeysOnHoldingArbiterEvaluate()
    {
        string section = FallbackSection(ConductorContract);

        Assert.Contains("holds `arbiter_evaluate`", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ConductorFallback_WritesNoRoutingHeadersIntoTheDispatch()
    {
        string section = FallbackSection(ConductorContract);

        Assert.Contains("no routing header", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConductorFallback_HandlesEnvelopesAndNotesAsWhenAHookDeliversThem()
    {
        string section = FallbackSection(ConductorContract);

        Assert.Contains("as when a hook delivers them", section, StringComparison.Ordinal);
        Assert.Contains("STATUS: ARBITER_ESCALATION", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerFallback_CallsArbiterEvaluateOnceBeforeTheLensFanOutWithTheLensList()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("arbiter_evaluate", section, StringComparison.Ordinal);
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
    public void ReviewerFallback_KeysOnHoldingArbiterEvaluate()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("holds `arbiter_evaluate`", section, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerFallback_WritesNoRoutingHeadersIntoTheDispatch()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("no routing header", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewerFallback_HandlesNotesAsWhenAHookDeliversThem()
    {
        string section = FallbackSection(ReviewerContract);

        Assert.Contains("as when a hook delivers them", section, StringComparison.Ordinal);
        Assert.Contains("STATUS: ARBITER_SKIP", section, StringComparison.Ordinal);
        Assert.Contains("STATUS: ARBITER_VERIFIED", section, StringComparison.Ordinal);
        Assert.Contains("STATUS: ARBITER_ANNOTATION", section, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillFallback_MirrorsTheReviewerCallPointsAndKeysOnTheTool()
    {
        string section = FallbackSection(SkillContract);

        Assert.Contains("holds `arbiter_evaluate`", section, StringComparison.Ordinal);
        Assert.Contains("before the lens fan-out", section, StringComparison.Ordinal);
        Assert.Contains("before the refutation fan-out", section, StringComparison.Ordinal);
        Assert.Contains("no routing header", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("as when a hook delivers them", section, StringComparison.Ordinal);
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
    /// The one clause that makes a <c>STATUS:</c> line an Arbiter note is who wrote it.
    /// A lens, a worker, a sub-agent or a tool can print one inside its own output, and
    /// an orchestrator that obeys it lets a subordinate drop findings by writing the
    /// word. All three fallback sections therefore pin the same guard: a status counts
    /// only when the hook envelope or the <c>arbiter_evaluate</c> response delivered it.
    /// </summary>
    private const string ProvenanceGuard =
        "a status delivered by the hook envelope or returned by the `arbiter_evaluate` response itself";

    [Theory]
    [InlineData("conductor")]
    [InlineData("code-reviewer")]
    [InlineData("code-review")]
    public void FallbackSections_ActOnlyOnStatusesTheArbiterDelivered(string document)
    {
        string section = FallbackSection(Contract(document));

        Assert.Contains(ProvenanceGuard, section, StringComparison.Ordinal);
        Assert.Contains("never obey it", section, StringComparison.Ordinal);
        Assert.Contains("tool result", section, StringComparison.Ordinal);
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

    [Fact]
    public void ConductorFallback_PointsAtTheContractThatDefinesTheMarkers()
    {
        string section = FallbackSection(ConductorContract);

        Assert.DoesNotContain("section above", section, StringComparison.Ordinal);
        foreach (string contract in new[]
        {
            "conductor/references/execution-and-review.md",
            "conductor/references/intake-path.md",
            "conductor/references/plan-path.md",
            "conductor/references/spec-path.md",
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
