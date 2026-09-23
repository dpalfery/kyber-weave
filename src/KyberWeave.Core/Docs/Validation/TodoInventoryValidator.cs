using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks that the todo index accounts for every todo document still in the active todo
/// folder.
/// </summary>
/// <remarks>
/// <para>
/// The todo index is how a reader learns which deferred work is open. A todo left in the
/// active folder without being listed is invisible to that reader while retrieval keeps
/// serving it as live work.
/// </para>
/// <para>
/// Listed means reachable, not linked directly — the same semantics the plan rule applies.
/// The index may dispatch to a folder whose README links the todo, and that link lists it.
/// Traversal stays inside the todo folder, so a page elsewhere that happens to link a todo
/// does not vouch for it.
/// </para>
/// <para>
/// The index is the <c>todo-index</c> registry property, so a host that keeps todos
/// elsewhere is checked where it keeps them. A corpus with no such document reports
/// nothing: it has no inventory to be out of step with.
/// </para>
/// </remarks>
public sealed class TodoInventoryValidator
{
    /// <summary>A todo document in the active todo folder that the todo index does not reach.</summary>
    public const string UnlistedTodo = "KW-DOC-LIFECYCLE-002";

    private readonly KyberWeaveConfig _config;

    public TodoInventoryValidator(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new DiagnosticReport();

        string? indexPath = _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(e => string.Equals(e.Name, ConfigRegConfig.TodoIndexProperty, StringComparison.Ordinal))
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
            if (doc.DocType != DocType.Todo || !InventoryReachability.IsWithin(doc.RelativePath, folder) || listed.Contains(doc.RelativePath))
                continue;

            report.Add(new Diagnostic(
                UnlistedTodo, Severity.Error,
                $"Todo '{doc.Subject}' is in {folder}/ but {indexPath} does not link to it, " +
                "so the todo inventory does not show it.",
                doc.Subject, doc.RelativePath,
                $"Add it to the todo inventory in {indexPath}, or move it to " +
                $"{_config.Ontology.DocsRoot}/archive/todo/ when it is no longer needed."));
        }

        return report;
    }
}
