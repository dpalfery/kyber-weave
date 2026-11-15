using System.Text.RegularExpressions;
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
public sealed partial class PlanInventoryValidator
{
    /// <summary>A plan document in the active plans folder that the plan index does not reach.</summary>
    public const string UnlistedPlan = "KW-DOC-LIFECYCLE-001";

    /// <summary>
    /// The plan index claims an open plan that does not exist: a table row with a Draft, Ready, In progress
    /// or Blocked status whose plan file is gone, or a sentence outside the table that restates a plan's
    /// status. Only the table row is kept in step with the plan by the finalize and closeout tools, so any
    /// other restatement goes stale when the plan is archived.
    /// </summary>
    public const string StaleOpenEntry = "KW-DOC-LIFECYCLE-006";

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
            if (doc.DocType != DocType.Plan || !InventoryReachability.IsWithin(doc.RelativePath, folder)
                || InventoryReachability.IsArchived(doc.RelativePath) || listed.Contains(doc.RelativePath))
            {
                continue;
            }

            report.Add(new Diagnostic(
                UnlistedPlan, Severity.Error,
                $"Plan '{doc.Subject}' is in {folder}/ but {indexPath} does not link to it, " +
                "so the plan inventory does not show it as open.",
                doc.Subject, doc.RelativePath,
                $"Link it from {indexPath} while it is open, or move it to " +
                $"{_config.Ontology.DocsRoot}/archive/plans/ once it is closed."));
        }

        CheckOpenClaims(index, byPath, indexPath, report);
        return report;
    }

    private static void CheckOpenClaims(
        DocumentModel index, Dictionary<string, DocumentModel> byPath, string indexPath, DiagnosticReport report)
    {
        string[] lines = index.Body.Split('\n');
        bool fenced = false;
        int statusColumn = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;
            if (fenced)
                continue;

            if (!line.StartsWith('|'))
            {
                statusColumn = -1;
                if (RestatedStatus().Match(line) is { Success: true } prose)
                {
                    report.Add(new Diagnostic(
                        StaleOpenEntry, Severity.Error,
                        $"{indexPath} restates a plan's status in prose ('{prose.Value}'). Nothing keeps that sentence " +
                        "current when the plan is finalized or archived, so it goes stale.",
                        indexPath, indexPath,
                        "Delete the sentence: the table row is the only place the index states a plan's status."));
                }

                continue;
            }

            string[] cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (statusColumn < 0)
            {
                statusColumn = Array.FindIndex(cells, cell => string.Equals(cell, "Status", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (statusColumn >= cells.Length || !OpenStatus().IsMatch(cells[statusColumn])
                || RowLink().Match(line) is not { Success: true } link)
            {
                continue;
            }

            string path = link.Groups[1].Value;
            if (InventoryReachability.ResolveDocument(indexPath, path, byPath) is null)
            {
                report.Add(new Diagnostic(
                    StaleOpenEntry, Severity.Error,
                    $"{indexPath} lists '{path}' as {cells[statusColumn]}, but no such plan document exists.",
                    path, indexPath,
                    "Remove the row if the plan was archived, or restore the plan file."));
            }
        }
    }

    [GeneratedRegex(@"^(?:Draft|Ready|In progress|Blocked)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenStatus();

    [GeneratedRegex(@"\]\(([^)#\s]+\.md)(?:#[^)]*)?\)", RegexOptions.CultureInvariant)]
    private static partial Regex RowLink();

    [GeneratedRegex(@"(?:`[^`]+`|\])\s*\((?:Draft|Ready|In progress|Blocked)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RestatedStatus();
}
