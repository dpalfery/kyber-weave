using System.Globalization;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Delivery;

namespace KyberWeave.Mcp;

/// <summary>
/// The run delivery behind the deliver tool, for one repository root: a thin wrapper over <see cref="RunDelivery"/>,
/// which <c>kyber-weave run deliver</c> also runs.
/// </summary>
public sealed class RunDeliveryService(
    string repositoryRoot,
    IDocsQueryService query,
    IDocsHostConfiguration hostConfiguration,
    RunDeliveryPorts ports) : IRunDeliveryService
{
    private const string DeliverFallbackNext =
        "next: dispatch " + RunDelivery.DeliveryAgents +
        " with the create-pull-request skill to open the pull request. Nothing was pushed.";

    private readonly string _repositoryRoot = repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot));
    private readonly IDocsQueryService _query = query ?? throw new ArgumentNullException(nameof(query));

    /// <summary>Runs git and gh for the deliver tool; null for the bounded default. Tests set it so nothing leaves the machine.</summary>
    public IProcessExecutor? DeliveryExecutor { get; set; }

    /// <summary>The home directory holding the run records; null for the user profile. Tests point it at a scratch folder.</summary>
    public string? DeliveryHome { get; set; }

    /// <inheritdoc />
    public Task<string> DeliverRunAsync(
        string summary,
        string? notes,
        string? title,
        bool draft,
        string? documentationImpact,
        string? planFile,
        CancellationToken cancellationToken = default) =>
        DeliverHereAsync(summary, notes, title, draft, documentationImpact, planFile, cancellationToken);

    private async Task<string> DeliverHereAsync(
        string summary,
        string? notes,
        string? title,
        bool draft,
        string? documentationImpact,
        string? planFile,
        CancellationToken cancellationToken)
    {
        string provenance = await _query.ProvenanceAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticReport report = new();
        if (!hostConfiguration.TryLoad(_repositoryRoot, report, out KyberWeaveConfig config))
            return PlanLifecycleService.LifecycleResponse(provenance, "refused", report, null);

        DeliveryOutcome outcome = new RunDelivery(_repositoryRoot, config, DeliveryExecutor, DeliveryHome, ports)
            .Run(new DeliveryRequest(summary, notes, title, draft, documentationImpact, planFile));
        report.AddRange(outcome.Report.Items);
        if (!outcome.Delivered)
            return PlanLifecycleService.LifecycleResponse(provenance, "refused", report, outcome.NeedsDeliveryAgent ? DeliverFallbackNext : null);

        StringBuilder next = new();
        next.AppendLine(CultureInfo.InvariantCulture, $"pull request: {outcome.Link}");
        next.AppendLine(CultureInfo.InvariantCulture, $"number: {outcome.Number}");
        next.AppendLine(CultureInfo.InvariantCulture, $"draft: {(outcome.IsDraft == true ? "yes" : "no")}");
        next.AppendLine(CultureInfo.InvariantCulture, $"base: {outcome.Base}");
        next.AppendLine(CultureInfo.InvariantCulture, $"head: {outcome.Head}");
        next.AppendLine(CultureInfo.InvariantCulture, $"checks: {outcome.Checks}");
        next.Append("next: report the pull request link and its checks. No re-check is needed: this was read back from GitHub.");
        return PlanLifecycleService.LifecycleResponse(provenance, outcome.Created ? "created" : "updated", report, next.ToString());
    }
}
