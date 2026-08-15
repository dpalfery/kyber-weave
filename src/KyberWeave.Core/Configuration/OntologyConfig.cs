using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// Documentation ontology: closed vocabularies, required-key matrix, docs roots,
/// exclusion lists, and catalog column positions. Product defaults mirror the
/// historical hardcoded behaviour; hosts override via <c>.kyber-weave/kyber-weave.yml</c>.
/// </summary>
public sealed class OntologyConfig
{
    /// <summary>The docs root a repository has when it configures none.</summary>
    internal const string DefaultDocsRoot = "6-Docs";

    private static readonly string[] DefaultDocsRoots = [DefaultDocsRoot];

    private static readonly string[] DefaultDocTypes =
    [
        "architecture", "onboarding", "requirements", "adr", "plan", "spec",
        "runbook", "reference", "rule", "governance", "index"
    ];

    private static readonly string[] DefaultStatuses =
        ["current", "draft", "needs-review", "superseded"];

    private static readonly string[] DefaultBaseRequiredKeys =
        ["id", "title", "owner", "last-reviewed", "doc-type", "status"];

    private static readonly string[] DefaultExcludedSegments =
        ["archive", "node_modules", "obj", "bin"];

    private static readonly string[] DefaultExcludedFiles =
    [
        "DevOps/build-performance.md",
        "DevOps/directory-build-organization.md",
        "DevOps/incremental-build.md",
        "DevOps/msbuild-antipatterns.md",
        "DevOps/msbuild-modernization.md"
    ];

    public IReadOnlyList<string> DocTypes { get; init; } = DefaultDocTypes;

    public IReadOnlyList<string> Statuses { get; init; } = DefaultStatuses;

    /// <summary>
    /// Every documentation root, in configured order. A repository that documents a
    /// component next to its code brings those files under governance by naming the
    /// module here rather than relocating them.
    /// </summary>
    public IReadOnlyList<string> DocsRoots { get; init; } = DefaultDocsRoots;

    /// <summary>
    /// The primary documentation root: where <c>docs init</c> scaffolds, and the root
    /// named in the diagnostics that point an author at the ontology reference.
    /// </summary>
    public string DocsRoot => DocsRoots.Count > 0 ? DocsRoots[0] : DefaultDocsRoot;

    /// <summary>
    /// Repository-relative path of the catalog, when the host puts it somewhere other than
    /// <c>&lt;primary root&gt;/catalog.md</c>. Read through <see cref="ResolvedCatalogPath"/>.
    /// </summary>
    public string? CatalogPath { get; init; }

    /// <summary>
    /// The one catalog this repository has. A <c>catalog.md</c> in any other root is an
    /// ordinary document: the component and owner vocabularies come from this file alone,
    /// because a vocabulary split across files is a vocabulary that cannot be closed.
    /// </summary>
    public string ResolvedCatalogPath =>
        string.IsNullOrWhiteSpace(CatalogPath) ? $"{DocsRoot}/catalog.md" : CatalogPath;

    public IReadOnlyList<string> ExcludedPathSegments { get; init; } = DefaultExcludedSegments;

    public IReadOnlyList<string> ExcludedFiles { get; init; } = DefaultExcludedFiles;

    /// <summary>Index into a pipe-split catalog table row for the Component cell.</summary>
    public int CatalogComponentColumn { get; init; } = 1;

    /// <summary>Index into a pipe-split catalog table row for the Owner cell.</summary>
    public int CatalogOwnerColumn { get; init; } = 6;

    public IReadOnlyList<string> BaseRequiredKeys { get; init; } = DefaultBaseRequiredKeys;

    /// <summary>Per-<see cref="DocType"/> required frontmatter keys (beyond the base set).</summary>
    public IReadOnlyDictionary<DocType, IReadOnlyList<string>> RequiredKeysByType { get; init; } =
        CreateDefaultRequiredKeysByType();

    public static OntologyConfig ProductDefaults { get; } = new();

    public bool IsRequiredForAll(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return BaseRequiredKeys.Contains(key, StringComparer.Ordinal);
    }

    public bool IsRequired(DocType docType, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!RequiredKeysByType.TryGetValue(docType, out IReadOnlyList<string>? keys))
            return false;

        return keys.Contains(key, StringComparer.Ordinal);
    }

    public OntologyConfig WithDocsRoot(string docsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRoot);
        return WithDocsRoots([docsRoot]);
    }

    /// <summary>
    /// Replaces the configured roots. Paths are canonicalized and checked for containment
    /// here as well as at load, so an operator-supplied <c>--docs-root</c> cannot reach a
    /// tree that the same value in <c>kyber-weave.yml</c> would have been refused.
    /// </summary>
    public OntologyConfig WithDocsRoots(IReadOnlyList<string> docsRoots)
    {
        ArgumentNullException.ThrowIfNull(docsRoots);
        return Clone(docsRoots: DocsRootPath.NormalizeRoots(docsRoots, "docs-root"));
    }

    internal OntologyConfig Clone(
        IReadOnlyList<string>? docsRoots = null,
        string? catalogPath = null,
        IReadOnlyList<string>? excludedPathSegments = null,
        IReadOnlyList<string>? excludedFiles = null,
        int? catalogComponentColumn = null,
        int? catalogOwnerColumn = null,
        IReadOnlyList<string>? docTypes = null,
        IReadOnlyList<string>? statuses = null,
        IReadOnlyList<string>? baseRequiredKeys = null,
        IReadOnlyDictionary<DocType, IReadOnlyList<string>>? requiredKeysByType = null) =>
        new()
        {
            DocsRoots = docsRoots ?? DocsRoots,
            CatalogPath = catalogPath ?? CatalogPath,
            ExcludedPathSegments = excludedPathSegments ?? ExcludedPathSegments,
            ExcludedFiles = excludedFiles ?? ExcludedFiles,
            CatalogComponentColumn = catalogComponentColumn ?? CatalogComponentColumn,
            CatalogOwnerColumn = catalogOwnerColumn ?? CatalogOwnerColumn,
            DocTypes = docTypes ?? DocTypes,
            Statuses = statuses ?? Statuses,
            BaseRequiredKeys = baseRequiredKeys ?? BaseRequiredKeys,
            RequiredKeysByType = requiredKeysByType ?? RequiredKeysByType
        };

    private static Dictionary<DocType, IReadOnlyList<string>> CreateDefaultRequiredKeysByType() =>
        new()
        {
            [DocType.Architecture] = ["component"],
            [DocType.Onboarding] = ["component", "source-root"],
            [DocType.Requirements] = ["component"],
            [DocType.Runbook] = ["component"],
            [DocType.Plan] = ["component"],
            [DocType.Spec] = ["component"]
        };
}
