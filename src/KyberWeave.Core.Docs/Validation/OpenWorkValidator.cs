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

    /// <summary>A task file left behind when the change merges.</summary>
    public const string LeftoverTaskFile = "KW-DOC-LIFECYCLE-005";

    private const string FolderIndexName = "README.md";

    // The task file a specification folder carries is `tasks.json`; a plan's is
    // `<plan>.tasks.json` beside the plan. Both names are left behind the same way.
    private const string FolderTaskFileName = "tasks.json";
    private const string PlanTaskFileSuffix = ".tasks.json";
    private const string JsonSearchPattern = "*.json";

    private static readonly (string Property, string Kind, string ArchiveFolder, DocType[] WorkTypes)[] Inventories =
    [
        (ConfigRegConfig.PlanIndexProperty, "Plan", "plans", [DocType.Plan]),
        (ConfigRegConfig.SpecificationIndexProperty, "Specification", "specs", [DocType.Spec, DocType.Requirements])
    ];

    private readonly KyberWeaveConfig _config;
    private readonly string? _repoRoot;

    /// <summary>Creates the validator over the registry and ontology in <paramref name="config"/>.</summary>
    /// <param name="config">The effective host configuration, with its ontology applied.</param>
    /// <param name="repoRoot">
    /// The absolute repository root, needed to find a task file left behind: those are not
    /// Markdown and so are not loaded as documents. Null skips that rule, which is what a caller
    /// checking a document set alone asks for.
    /// </param>
    public OpenWorkValidator(KyberWeaveConfig config, string? repoRoot = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _repoRoot = repoRoot;
    }

    /// <summary>One error per plan or specification still open in its active folder.</summary>
    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new();
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

        report.AddRange(LeftoverTaskFiles().Items);
        return report;
    }

    /// <summary>One error per task file still in the corpus when the change merges.</summary>
    /// <remarks>
    /// <para>
    /// A task file has a shelf life of exactly one plan: the commit that archives the plan deletes
    /// it. One left behind describes work nobody is doing, and the next architect reads it as
    /// live — so the merge is the last moment it can be caught.
    /// </para>
    /// <para>
    /// The archive is inside the walk on purpose. A specification's task file lives in its
    /// specification folder, so the closeout moves it with the folder; looking only in the active
    /// folders would let a file ride into the archive beside its specification, where it stays
    /// readable and forever stale.
    /// </para>
    /// <para>
    /// A task file is not Markdown, so it is not a document and no document set can find it. The
    /// walk is over the configured docs root, one level at a time and without following a link
    /// (see <see cref="DocsRootPath" />), because the corpus is user-controlled.
    /// </para>
    /// </remarks>
    private DiagnosticReport LeftoverTaskFiles()
    {
        DiagnosticReport report = new();
        if (_repoRoot is null)
            return report;

        string docsRoot = Path.Combine(_repoRoot, _config.Ontology.DocsRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(docsRoot))
            return report;

        foreach (string file in DocsRootPath.EnumerateContainedFiles(docsRoot, JsonSearchPattern))
        {
            string name = Path.GetFileName(file);
            if (!name.Equals(FolderTaskFileName, StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(PlanTaskFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(_repoRoot, file).Replace('\\', '/');
            report.Add(new Diagnostic(
                LeftoverTaskFile, Severity.Error,
                $"Task file '{relativePath}' is left behind. A task file lives only as long as the plan or " +
                "specification it was written from, and a merge must not carry one.",
                relativePath, relativePath,
                "Remove it with `git rm` in the same commit that archives the plan. The archived plan keeps " +
                "the decisions, tasks and test contract, so nothing durable is lost."));
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
        DocType[] workTypes)
    {
        return set.Documents
            .Where(d => InventoryReachability.IsWithin(d.RelativePath, folder)
                        && !InventoryReachability.IsArchived(d.RelativePath)
                        && !DocsRootPath.PathComparer.Equals(d.RelativePath, indexPath))
            .OrderBy(d => d.RelativePath, StringComparer.Ordinal)
            .GroupBy(d => InventoryReachability.EntryBelow(d.RelativePath, folder), StringComparer.Ordinal)
            .Where(entry => entry.Any(d => workTypes.Contains(d.DocType)));
    }
}
