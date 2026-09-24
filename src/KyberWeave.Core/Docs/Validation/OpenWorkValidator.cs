using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks that no plan or specification is still open: the merge gate that
/// <c>docs validate --merge-ready</c> adds.
/// </summary>
/// <remarks>
/// <para>
/// A plan or specification sits in its active folder only while it is being built. The
/// change that finishes the work archives it, so a pull request that merges one still open
/// either merges unfinished work or leaves finished work looking live to retrieval. Both
/// happened: a specification merged in stages with its closeout never done, and three
/// delivered plans stayed in the active folder marked Ready.
/// </para>
/// <para>
/// The rule is opt-in because an open plan is correct while the build is under way — only
/// the merge must not carry one. Each piece of work is reported once: a specification
/// folder is one specification however many documents it holds. The folders are those of
/// the <c>plan-index</c> and <c>specification-index</c> registry properties, and the
/// indexes themselves are never open work.
/// </para>
/// <para>
/// An entry is open work only when it holds a document of that inventory's type — a
/// <c>plan</c>, or a <c>spec</c> or <c>requirements</c> document. Location alone is not
/// enough: a host may point an index at a shared folder, and the catalog beside it is not
/// a plan.
/// </para>
/// </remarks>
public sealed class OpenWorkValidator
{
    /// <summary>A plan or specification still in its active folder when the change merges.</summary>
    public const string OpenWork = "KW-DOC-LIFECYCLE-003";

    private const string FolderIndexName = "README.md";

    private static readonly (string Property, string Kind, string ArchiveFolder, DocType[] WorkTypes)[] Inventories =
    [
        (ConfigRegConfig.PlanIndexProperty, "Plan", "plans", [DocType.Plan]),
        (ConfigRegConfig.SpecificationIndexProperty, "Specification", "specs", [DocType.Spec, DocType.Requirements])
    ];

    private readonly KyberWeaveConfig _config;

    /// <summary>Creates the validator over the registry and ontology in <paramref name="config"/>.</summary>
    public OpenWorkValidator(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>One error per plan or specification still open in its active folder.</summary>
    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new DiagnosticReport();
        IReadOnlyList<ConfigRegEntry> registry = _config.ConfigReg.Resolve(_config.Ontology);

        foreach ((string property, string kind, string archiveFolder, DocType[] workTypes) in Inventories)
        {
            string? indexPath = registry
                .FirstOrDefault(e => string.Equals(e.Name, property, StringComparison.Ordinal))
                ?.Path;
            if (indexPath is null)
                continue;

            string folder = Path.GetDirectoryName(indexPath)?.Replace('\\', '/') ?? string.Empty;
            foreach (IGrouping<string, DocumentModel> work in OpenEntries(set, folder, indexPath, workTypes))
            {
                DocumentModel first = work.FirstOrDefault(d => d.RelativePath.EndsWith(
                    "/" + FolderIndexName, StringComparison.OrdinalIgnoreCase)) ?? work.First();

                report.Add(new Diagnostic(
                    OpenWork, Severity.Error,
                    $"{kind} '{work.Key}' is still open in {folder}/. A merge must not carry open " +
                    "work: finish it and archive it, or keep it on its branch.",
                    work.Key, first.RelativePath,
                    $"Once the work is done, move it to {_config.Ontology.DocsRoot}/archive/{archiveFolder}/ " +
                    $"and update {indexPath}."));
            }
        }

        return report;
    }

    /// <summary>
    /// Live documents inside <paramref name="folder"/>, grouped by the entry directly below it
    /// — a plan file, or a folder holding one piece of work — keeping only entries that hold a
    /// document of one of <paramref name="workTypes"/>.
    /// </summary>
    private static IEnumerable<IGrouping<string, DocumentModel>> OpenEntries(
        DocumentSet set,
        string folder,
        string indexPath,
        DocType[] workTypes) =>
        set.Documents
            .Where(d => InventoryReachability.IsWithin(d.RelativePath, folder)
                && !InventoryReachability.IsArchived(d.RelativePath)
                && !DocsRootPath.PathComparer.Equals(d.RelativePath, indexPath))
            .OrderBy(d => d.RelativePath, StringComparer.Ordinal)
            .GroupBy(d => EntryBelow(d.RelativePath, folder), StringComparer.Ordinal)
            .Where(entry => entry.Any(d => workTypes.Contains(d.DocType)));

    /// <summary>The first path segment of <paramref name="relativePath"/> below <paramref name="folder"/>.</summary>
    private static string EntryBelow(string relativePath, string folder)
    {
        string below = folder.Length == 0 ? relativePath : relativePath[(folder.Length + 1)..];
        int slash = below.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? below : below[..slash];
    }
}
