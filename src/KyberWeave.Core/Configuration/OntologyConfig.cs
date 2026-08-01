using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// Documentation ontology: closed vocabularies, required-key matrix, docs root,
/// exclusion lists, and catalog column positions. Product defaults mirror the
/// historical hardcoded behaviour; hosts override via <c>.kyber-weave/kyber-weave.yml</c>.
/// </summary>
public sealed class OntologyConfig
{
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

    public string DocsRoot { get; init; } = "6-Docs";

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
        if (!RequiredKeysByType.TryGetValue(docType, out var keys))
            return false;

        return keys.Contains(key, StringComparer.Ordinal);
    }

    public OntologyConfig WithDocsRoot(string docsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRoot);
        return Clone(docsRoot: docsRoot);
    }

    internal OntologyConfig Clone(
        string? docsRoot = null,
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
            DocsRoot = docsRoot ?? DocsRoot,
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
