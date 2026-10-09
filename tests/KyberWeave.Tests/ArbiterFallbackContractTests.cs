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
        string section = document switch
        {
            "conductor" => FallbackSection(ConductorContract),
            "code-reviewer" => FallbackSection(ReviewerContract),
            _ => FallbackSection(SkillContract),
        };

        foreach (string harness in HarnessNames)
        {
            Assert.DoesNotMatch(
                $@"\b{Regex.Escape(harness)}\b",
                section);
        }
    }

    private static string FallbackSection(string document)
    {
        int start = document.IndexOf(FallbackHeading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Document has no '{FallbackHeading}' section.");

        int end = document.IndexOf("\n## ", start + FallbackHeading.Length, StringComparison.Ordinal);
        return end < 0 ? document[start..] : document[start..end];
    }
}
