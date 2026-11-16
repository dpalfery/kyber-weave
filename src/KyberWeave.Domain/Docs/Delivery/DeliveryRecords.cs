using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>What the conductor asks the deliver step for.</summary>
/// <param name="Summary">The problem and the outcome, in one to three plain sentences.</param>
/// <param name="Notes">Extra notes for reviewers, or null.</param>
/// <param name="Title">A title that replaces the composed one, or null.</param>
/// <param name="Draft">Whether a new pull request is opened as a draft.</param>
/// <param name="DocumentationImpact">The documentation-impact outcome as docs-dev returned it, or null.</param>
/// <param name="PlanFile">The archived plan, when the branch archived more than one; null to find it.</param>
public sealed record DeliveryRequest(
    string Summary,
    string? Notes = null,
    string? Title = null,
    bool Draft = true,
    string? DocumentationImpact = null,
    string? PlanFile = null);

/// <summary>What a delivery did.</summary>
/// <param name="Report">The findings, and one Info line per step taken.</param>
/// <param name="Delivered">Whether the branch was pushed and its pull request created or updated.</param>
/// <param name="Created">True when this call created the pull request, false when it updated an open one.</param>
/// <param name="Link">The pull request's address.</param>
/// <param name="Number">The pull request's number.</param>
/// <param name="IsDraft">Whether the pull request is a draft, as read back.</param>
/// <param name="Base">The branch it merges into.</param>
/// <param name="Head">The run branch.</param>
/// <param name="Checks">One line on the CI checks reported on it.</param>
/// <param name="Title">The title sent.</param>
/// <param name="Body">The description sent.</param>
public sealed record DeliveryOutcome(
    DiagnosticReport Report,
    bool Delivered,
    bool Created = false,
    string? Link = null,
    int? Number = null,
    bool? IsDraft = null,
    string? Base = null,
    string? Head = null,
    string? Checks = null,
    string? Title = null,
    string? Body = null)
{
    /// <summary>The refusal code for a host other than github.com or a missing GitHub CLI, which sends the conductor to the delivery agent.</summary>
    public const string ProviderUnavailableCode = "KW-DELIVER-005";

    /// <summary>Whether the refusal is the provider one, which sends the conductor to the delivery agent instead.</summary>
    public bool NeedsDeliveryAgent => Report.Items.Any(item => item.Code == ProviderUnavailableCode);
}
