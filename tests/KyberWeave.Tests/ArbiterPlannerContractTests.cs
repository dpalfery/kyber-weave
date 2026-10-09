using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the planner Arbiter contract (packet 7.2): the architect escalation route,
/// the three escalation outcomes, attestation only after <c>NEEDS_DECISION</c>,
/// and investigator-dispatch markers on both planners.
/// </summary>
public sealed class ArbiterPlannerContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string ArchitectPrincipal =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "architect.md"));

    private static string EscalationReference =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "architect", "references", "arbiter-escalation.md"));

    private static string ProductOwnerContract =>
        File.ReadAllText(Path.Combine(ProductRoot, "agents", "product-owner.md"));

    private static string ArchitectContract =>
        ArchitectPrincipal + "\n" + EscalationReference;

    [Fact]
    public void Architect_RoutesArbiterEscalationToReference()
    {
        Assert.Contains("STATUS: ARBITER_ESCALATION", ArchitectPrincipal, StringComparison.Ordinal);
        Assert.Contains("arbiter-escalation.md", ArchitectPrincipal, StringComparison.Ordinal);
        Assert.Contains("STATUS: ESCALATION_RESOLVED", ArchitectPrincipal, StringComparison.Ordinal);
    }

    [Fact]
    public void EscalationReference_DefinesThreeOutcomes()
    {
        string reference = EscalationReference;

        Assert.Contains("STATUS: ESCALATION_RESOLVED", reference, StringComparison.Ordinal);
        Assert.Contains("STATUS: NEEDS_DECISION", reference, StringComparison.Ordinal);
        Assert.Contains("amendment", reference, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("corrected", reference, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved plan", reference, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot authorise", reference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attestation_FollowsNeedsDecisionAfterLedgerLoss()
    {
        string reference = EscalationReference;

        Assert.Contains("STATUS: NEEDS_DECISION", reference, StringComparison.Ordinal);
        Assert.Contains("ATTESTED:", reference, StringComparison.Ordinal);
        Assert.Contains("complete|red", reference, StringComparison.Ordinal);

        int decisionIndex = reference.IndexOf("STATUS: NEEDS_DECISION", StringComparison.Ordinal);
        int attestedIndex = reference.IndexOf("ATTESTED:", StringComparison.Ordinal);
        Assert.True(
            decisionIndex >= 0 && attestedIndex > decisionIndex,
            "ATTESTED must follow NEEDS_DECISION: attestation is returned only after the user confirms.");
    }

    [Fact]
    public void Architect_MarksInvestigatorDispatches()
    {
        Assert.Contains("KYBER-ARBITER: true", ArchitectContract, StringComparison.Ordinal);
        Assert.Contains("investigator", ArchitectContract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductOwner_MarksInvestigatorDispatches()
    {
        Assert.Contains("KYBER-ARBITER: true", ProductOwnerContract, StringComparison.Ordinal);
        Assert.Contains("investigator", ProductOwnerContract, StringComparison.OrdinalIgnoreCase);
    }
}
