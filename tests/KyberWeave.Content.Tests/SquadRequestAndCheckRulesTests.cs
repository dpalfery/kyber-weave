using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the three prompt rules the run-11 conductor loop produced: a dispatch forwards the user's
/// request instead of restating it, the architect traces every explicit requirement before
/// <c>PLAN_READY</c> and records a conflict with a repository rule as a decision, and an agent fixes
/// a check finding and re-runs the check instead of reporting it as a blocker.
/// </summary>
/// <remarks>
/// The rules are stated once in <c>docs/kyber-squad/architecture.md</c> under
/// "The request, the trace, and the check". These cases read the shipped prompt that each rule
/// governs, through the corpus seam, so a later edit that restores the summarising dispatch, the
/// silent reconciliation or the blocker wording fails here.
/// </remarks>
public sealed class SquadRequestAndCheckRulesTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ConductorPath = "products/kyber-squad/agents/conductor.md";

    private const string IntakePath = "products/kyber-squad/agents/conductor/references/intake-path.md";

    private const string SpecPath = "products/kyber-squad/agents/conductor/references/spec-path.md";

    private const string PlanAuthoringPath = "products/kyber-squad/agents/architect/references/plan-authoring.md";

    private const string ArchitectPath = "products/kyber-squad/agents/architect.md";

    private const string DocsDevPath = "products/kyber-squad/agents/docs-dev.md";

    private const string AgentsRoot = "products/kyber-squad/agents";

    /// <summary>
    ///     The conductor's dispatch guidance forwards the request and forbids restating it. No
    ///     directive that asks for a summary of the request survives anywhere in the body.
    /// </summary>
    [Fact]
    public void TheConductorForwardsTheRequestAndNeverSummarizesIt()
    {
        string dispatch = Section(ConductorPath, "What a dispatch carries");

        Assert.Contains("verbatim", dispatch, StringComparison.Ordinal);
        Assert.Contains("Do not summarize, paraphrase, shorten", dispatch, StringComparison.Ordinal);
        Assert.Contains("routing facts", dispatch, StringComparison.Ordinal);

        string body = Corpus.ReadText(ConductorPath);
        string[] summarising = [.. SummarisingInstructions()
            .Where(phrase => body.Contains(phrase, StringComparison.OrdinalIgnoreCase))];

        Assert.True(
            summarising.Length == 0,
            "The conductor forwards the user's request; it never asks for or produces a summary of it. " +
            "Still carrying: " + string.Join(", ", summarising));
    }

    /// <summary>
    ///     The paths that hand a user request to a specialist carry the same instruction, so a path
    ///     contract cannot reintroduce a filtered or summarised request.
    /// </summary>
    [Theory]
    [InlineData(IntakePath, "Assessment")]
    [InlineData(SpecPath, "Route by persisted state")]
    public void EveryPathThatCarriesAUserRequestForwardsItVerbatim(string path, string heading)
    {
        string section = Section(path, heading);

        Assert.Contains("verbatim", section, StringComparison.Ordinal);
        Assert.Contains("never your summary", section, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Before <c>PLAN_READY</c> the plan carries a trace of every explicit instruction, and a
    ///     conflict with a repository rule is a decision with a recommendation, never a silent split.
    /// </summary>
    [Fact]
    public void TheArchitectTracesEveryExplicitRequirementBeforePlanReady()
    {
        string contract = Corpus.ReadText(PlanAuthoringPath);
        string instructions = Section(PlanAuthoringPath, "The request's explicit instructions");

        Assert.Contains("## Request trace", contract, StringComparison.Ordinal);
        Assert.Contains("| Request instruction | Covered by |", contract, StringComparison.Ordinal);
        Assert.Contains("one row for each explicit instruction", instructions, StringComparison.Ordinal);
        Assert.Contains("## Decisions", instructions, StringComparison.Ordinal);
        Assert.Contains("recommended", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hard repository ban", instructions, StringComparison.Ordinal);
        Assert.Contains("never reconcile the conflict silently", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request-traced", Corpus.ReadText(ArchitectPath), StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>docs-dev</c> fixes a check finding, including a warning and a pre-existing one, and repeats
    ///     until the checks are clean; <c>BLOCKED</c> names what it cannot fix and who can.
    /// </summary>
    [Fact]
    public void DocsDevFixesEveryCheckFindingAndRechecks()
    {
        string closeout = Section(DocsDevPath, "Plan closeout");
        string body = Corpus.ReadText(DocsDevPath);

        Assert.Contains("Fix every finding", closeout, StringComparison.Ordinal);
        Assert.Contains("warnings alike", closeout, StringComparison.Ordinal);
        Assert.Contains("repeat until both are clean", closeout, StringComparison.Ordinal);
        Assert.Contains("not a blocker", closeout, StringComparison.Ordinal);
        Assert.Contains("why you cannot fix it, and who can", closeout, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs standards migrate", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Report any finding as a blocker", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     No shipped prompt keeps the retired wording that turns a check finding into a blocker, which
    ///     is what made run 11's closeout wait on a person over fifteen warnings.
    /// </summary>
    [Fact]
    public void NoAgentTreatsACheckFindingAsABlocker()
    {
        string[] repeating = [.. Corpus.EnumerateFiles(AgentsRoot, "*.md")
            .Where(path => Corpus.ReadText(path).Contains("Report any finding as a blocker", StringComparison.OrdinalIgnoreCase))];

        Assert.True(
            repeating.Length == 0,
            "A check finding is fixed and re-checked, not reported as a blocker. Still carrying the " +
            "retired wording: " + string.Join(", ", repeating));
    }

    /// <summary>The design note states all three rules in the one section that governs the prompts.</summary>
    [Fact]
    public void TheDesignNoteStatesTheThreeRules()
    {
        string note = Section("docs/kyber-squad/architecture.md", "The request, the trace, and the check");

        Assert.Contains("verbatim", note, StringComparison.Ordinal);
        Assert.Contains("## Request trace", note, StringComparison.Ordinal);
        Assert.Contains("BLOCKED", note, StringComparison.Ordinal);
    }

    /// <summary>The section a heading names, matched the way a brief map spells headings.</summary>
    private static string Section(string path, string heading)
    {
        MarkdownSection? section = MarkdownSections.Split(Corpus.ReadText(path))
            .FirstOrDefault(candidate => candidate.Heading is not null
                && MarkdownSections.Normalize(candidate.Heading) == MarkdownSections.Normalize(heading));

        Assert.True(section is not null, $"{path} has no section '{heading}'.");
        return section!.Text;
    }

    /// <summary>The directive forms that would make the conductor restate a request instead of forwarding it.</summary>
    private static string[] SummarisingInstructions() =>
    [
        "summarize the request",
        "summarise the request",
        "summary of the request",
        "summarize the user's request",
        "summarise the user's request",
    ];
}
