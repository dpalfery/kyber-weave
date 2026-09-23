using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks that the plan index accounts for every plan document still in the active plans
/// folder.
/// </summary>
/// <remarks>
/// <para>
/// A plan closes by being archived, and the inventory is how a reader learns which plans are
/// open. A plan left in the active folder without being listed is invisible to that reader
/// while retrieval keeps serving it as a live plan. This happened: a plan was archived and
/// its inventory said "None", while the nine-document dispatch pack beneath it stayed in the
/// active folder, unlisted and marked <c>needs-review</c>.
/// </para>
/// <para>
/// Listed means reachable, not linked directly. The index links a plan and a plan may link
/// the documents it dispatches, so a pack is listed through its own README. Traversal stays
/// inside the plans folder: a canonical page that happens to link a plan does not vouch for
/// it. Reading the inventory's prose for "None" was rejected, because rewording the heading
/// would silence the check.
/// </para>
/// <para>
/// The index is the <c>plan-index</c> registry property, so a host that keeps plans elsewhere
/// is checked where it keeps them. A corpus with no such document reports nothing: it has no
/// inventory to be out of step with.
/// </para>
/// </remarks>
public sealed class PlanInventoryValidator
{
    /// <summary>A plan document in the active plans folder that the plan index does not reach.</summary>
    public const string UnlistedPlan = "KW-DOC-LIFECYCLE-001";

    private readonly KyberWeaveConfig _config;

    public PlanInventoryValidator(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new DiagnosticReport();

        string? indexPath = _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(e => string.Equals(e.Name, ConfigRegConfig.PlanIndexProperty, StringComparison.Ordinal))
            ?.Path;

        Dictionary<string, DocumentModel> byPath = new Dictionary<string, DocumentModel>(DocsRootPath.PathComparer);
        foreach (DocumentModel doc in set.Documents)
            byPath.TryAdd(doc.RelativePath, doc);

        if (indexPath is null || !byPath.TryGetValue(indexPath, out DocumentModel? index))
            return report;

        string folder = Path.GetDirectoryName(indexPath)?.Replace('\\', '/') ?? string.Empty;
        HashSet<string> listed = InventoryReachability.ReachableWithin(index, folder, byPath);

        foreach (DocumentModel doc in set.Documents)
        {
            if (doc.DocType != DocType.Plan || !InventoryReachability.IsWithin(doc.RelativePath, folder) || listed.Contains(doc.RelativePath))
                continue;

            report.Add(new Diagnostic(
                UnlistedPlan, Severity.Error,
                $"Plan '{doc.Subject}' is in {folder}/ but {indexPath} does not link to it, " +
                "so the plan inventory does not show it as open.",
                doc.Subject, doc.RelativePath,
                $"Link it from {indexPath} while it is open, or move it to " +
                $"{_config.Ontology.DocsRoot}/archive/plans/ once it is closed."));
        }

        return report;
    }
}
