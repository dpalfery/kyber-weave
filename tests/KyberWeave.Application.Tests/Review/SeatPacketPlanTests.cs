using KyberWeave.Application.Review;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// A seat packet names the plan of the change under review, so a lens can judge each finding against the request's
/// acceptance criteria and the plan's approved decisions.
/// </summary>
public sealed class SeatPacketPlanTests
{
    private const string OnePlan = "docs/plans/2026-01-01-feature.md";
    private const string OtherPlan = "docs/plans/2026-01-02-other.md";

    [Fact]
    public void ThePacketNamesTheOnePlanTheWholeChangeTouches()
    {
        string packet = Packet(["src/A.cs", OnePlan], (OnePlan, PlanMarkdown("In progress")));

        Assert.Contains(
            $"- Request and plan: {OnePlan}. Its Request section holds the acceptance criteria; its Decisions are approved choices. Classify each finding's kind against them.",
            packet,
            StringComparison.Ordinal);
        int guidance = packet.IndexOf("- Review guidance:", StringComparison.Ordinal);
        int plan = packet.IndexOf("- Request and plan:", StringComparison.Ordinal);
        Assert.True(guidance >= 0 && guidance < plan);
    }

    /// <summary>Closeout marks the plan Completed before the review cycle runs, so the lookup reads the change.</summary>
    [Fact]
    public void APlanAlreadyMarkedCompletedIsStillFound()
    {
        string packet = Packet([OnePlan], (OnePlan, PlanMarkdown("Completed")));

        Assert.Contains($"- Request and plan: {OnePlan}.", packet, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeWithNoPlanSaysNoneWasFound()
    {
        string packet = Packet(["src/A.cs", "docs/guide.md"]);

        Assert.Contains(
            "- Request and plan: none found in the change. Classify each finding's kind from the diff and the evidence, and say so.",
            packet,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeTouchingSeveralPlansNamesEach()
    {
        string packet = Packet(
            [OtherPlan, OnePlan],
            (OnePlan, PlanMarkdown("Completed")),
            (OtherPlan, PlanMarkdown("Completed")));

        Assert.Contains(
            $"- Request and plan: several: {OnePlan}, {OtherPlan}. Classify each finding's kind from the diff and the evidence, and say so.",
            packet,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AMarkdownFileUnderThePlanFolderWithoutDocTypePlanIsIgnored()
    {
        string packet = Packet(["docs/plans/notes.md"], ("docs/plans/notes.md", PlanMarkdown("reference", isPlan: false)));

        Assert.Contains("- Request and plan: none found in the change.", packet, StringComparison.Ordinal);
    }

    private static string Packet(IReadOnlyList<string> changed, params (string Path, string Text)[] files)
    {
        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        foreach ((string path, string text) in files)
            texts[path] = text;

        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfigDefaults.Product,
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };
        Assert.Equal("docs/plans", ChangePlanLookup.PlanFolder(config));

        IReadOnlyList<string> plans = ChangePlanLookup.Find(
            changed,
            ChangePlanLookup.PlanFolder(config),
            path => texts.GetValueOrDefault(path));
        CouncilSeatContext context = new(
            "/repo",
            "/repo/evidence.md",
            true,
            "balanced",
            7,
            "minor",
            "/repo/REVIEW.md",
            null,
            ChangedPaths: changed);
        return CouncilSeating.Packet(ChangePlanLookup.WithPlan(context, plans), "intent-alignment");
    }

    private static string PlanMarkdown(string status, bool isPlan = true) =>
        $"---\nid: plan-1\ntitle: A plan\ndoc-type: {(isPlan ? "plan" : "reference")}\nstatus: {status}\n---\n\n# Plan\n";
}
