using KyberWeave.Core.Docs.Model;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads and merges ontology overrides from <c>kyber-weave.yml</c>.</summary>
public static class OntologyConfigLoader
{
    /// <summary>Key names as an operator wrote them, so diagnostics name the real line.</summary>
    private const string DocsRootKey = "ontology.docs-root";

    private const string CatalogPathKey = "ontology.catalog-path";

    private const string TechnologiesKey = "ontology.technologies";

    public static OntologyConfig LoadMerged(OntologyConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        KyberWeaveYamlDocument document = KyberWeaveYamlParser.ParseFile(yamlPath);
        return Merge(defaults, document.Ontology);
    }

    public static OntologyConfigLoadResult TryLoad(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        try
        {
            KyberWeaveYamlDocument document = KyberWeaveYamlParser.ParseFile(yamlPath);
            return OntologyConfigLoadResult.Ok(Merge(OntologyConfig.ProductDefaults, document.Ontology));
        }
        catch (YamlException ex)
        {
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            // YamlDotNet throws ArgumentException for a null mapping key (e.g. required-keys
            // with an empty key). Surface it as a config failure rather than an unhandled crash.
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OntologyConfigLoadResult.Fail(ex.Message);
        }
    }

    internal static OntologyConfig Merge(OntologyConfig defaults, OntologyYamlSection? section)
    {
        if (section is null)
            return defaults;

        IReadOnlyDictionary<DocType, IReadOnlyList<string>>? requiredKeys = null;
        if (section.RequiredKeys is not null)
        {
            Dictionary<DocType, IReadOnlyList<string>> merged = new Dictionary<DocType, IReadOnlyList<string>>(defaults.RequiredKeysByType);
            foreach ((string? typeName, List<string>? keys) in section.RequiredKeys)
            {
                if (!TryParseDocType(typeName, out DocType docType))
                {
                    throw new YamlException(
                        $"Unknown ontology required-keys doc type '{typeName}'. " +
                        "Known types: architecture, onboarding, requirements, adr, plan, spec, todo, " +
                        "runbook, reference, rule, governance, index, coding-standard.");
                }

                merged[docType] = keys is null
                    ? Array.Empty<string>()
                    : keys.ToArray();
            }

            requiredKeys = merged;
        }

        return defaults.Clone(
            docsRoots: NormalizeDocsRoots(section.DocsRoot),
            catalogPath: NormalizeCatalogPath(section.CatalogPath),
            excludedPathSegments: section.ExcludedSegments,
            excludedFiles: section.ExcludedFiles,
            catalogComponentColumn: section.Catalog?.ComponentColumn,
            catalogOwnerColumn: section.Catalog?.OwnerColumn,
            docTypes: section.DocTypes,
            statuses: section.Statuses,
            technologies: NormalizeTechnologies(section.Technologies),
            baseRequiredKeys: section.BaseRequiredKeys,
            requiredKeysByType: requiredKeys);
    }

    /// <summary>
    /// Reads <c>ontology.docs-root</c> in either of its shapes — a directory, or a list of
    /// them — into the canonical list form. A path failure becomes a
    /// <see cref="YamlException"/> so it reaches the operator as <c>KW-CONFIG-001</c>
    /// against the file they wrote, rather than as an unhandled argument error.
    /// </summary>
    private static IReadOnlyList<string>? NormalizeDocsRoots(object? value)
    {
        if (value is null) return null;

        List<string> raw = value switch
        {
            string scalar => [scalar],
            IEnumerable<object?> sequence => sequence.Select(ToRootScalar).ToList(),
            _ => throw InvalidDocsRoot()
        };

        try
        {
            return DocsRootPath.NormalizeRoots(raw, DocsRootKey);
        }
        catch (ArgumentException ex)
        {
            throw new YamlException(ex.Message);
        }
    }

    private static string ToRootScalar(object? entry) =>
        entry as string ?? throw InvalidDocsRoot();

    private static YamlException InvalidDocsRoot() =>
        new($"{DocsRootKey} takes a directory, or a list of directories. " +
            "Write 'docs-root: docs' for one root, or a '- ' item per root for several.");

    private static string? NormalizeCatalogPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string path;
        try
        {
            path = DocsRootPath.Normalize(value, CatalogPathKey);
        }
        catch (ArgumentException ex)
        {
            throw new YamlException(ex.Message);
        }

        return path.Length > 0
            ? path
            : throw new YamlException(
                $"{CatalogPathKey} '{value}' names no file. Give a repository-relative " +
                "path to the catalog, e.g. 'docs/catalog.md'.");
    }

    /// <summary>
    /// Validates the declared technology vocabulary. Each value becomes a directory name
    /// under <c>&lt;docs-root&gt;/standards/</c> and the legal value of a standard's
    /// <c>technology</c> key, so it is constrained to a slug here — at the point the operator
    /// wrote it — rather than at the point something tries to create a path out of it.
    /// </summary>
    private static IReadOnlyList<string>? NormalizeTechnologies(List<string>? values)
    {
        if (values is null) return null;

        List<string> normalized = new List<string>(values.Count);
        foreach (string? value in values)
        {
            if (value is null)
            {
                throw new YamlException(
                    $"{TechnologiesKey} contains a null entry. Use a slug such as 'csharp'.");
            }
            string technology = value.Trim();
            if (!ConfigSlug.IsValid(technology))
            {
                throw new YamlException(
                    $"{TechnologiesKey} entry '{value}' is not a slug. Use lowercase letters, " +
                    "digits and single hyphens — 'github-actions', not 'GitHub Actions' or " +
                    "'standards/github'. The value names a directory and legalizes a " +
                    "standard's 'technology' key.");
            }

            if (normalized.Contains(technology, StringComparer.Ordinal))
            {
                throw new YamlException(
                    $"{TechnologiesKey} declares '{technology}' more than once.");
            }

            normalized.Add(technology);
        }

        return normalized;
    }

    private static bool TryParseDocType(string? name, out DocType docType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            docType = DocType.Unknown;
            return false;
        }

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
        docType = name.Trim().ToLowerInvariant() switch
#pragma warning restore CA1308
        {
            "architecture" => DocType.Architecture,
            "onboarding" => DocType.Onboarding,
            "requirements" => DocType.Requirements,
            "adr" => DocType.Adr,
            "plan" => DocType.Plan,
            "spec" => DocType.Spec,
            "todo" => DocType.Todo,
            "runbook" => DocType.Runbook,
            "reference" => DocType.Reference,
            "rule" => DocType.Rule,
            "governance" => DocType.Governance,
            "index" => DocType.Index,
            "coding-standard" => DocType.CodingStandard,
            _ => DocType.Unknown
        };
        return docType != DocType.Unknown;
    }
}
