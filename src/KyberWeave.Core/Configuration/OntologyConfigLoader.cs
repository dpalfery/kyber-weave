using KyberWeave.Core.Docs.Model;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads and merges ontology overrides from <c>kyber-weave.yml</c>.</summary>
public static class OntologyConfigLoader
{
    /// <summary>Key names as an operator wrote them, so diagnostics name the real line.</summary>
    internal const string DocsRootKey = "ontology.docs-root";

    internal const string CatalogPathKey = "ontology.catalog-path";

    public static OntologyConfig LoadMerged(OntologyConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        var document = KyberWeaveYamlParser.ParseFile(yamlPath);
        return Merge(defaults, document.Ontology);
    }

    public static OntologyConfigLoadResult TryLoad(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        try
        {
            var document = KyberWeaveYamlParser.ParseFile(yamlPath);
            return OntologyConfigLoadResult.Ok(Merge(OntologyConfig.ProductDefaults, document.Ontology));
        }
        catch (YamlException ex)
        {
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
            var merged = new Dictionary<DocType, IReadOnlyList<string>>(defaults.RequiredKeysByType);
            foreach (var (typeName, keys) in section.RequiredKeys)
            {
                if (!TryParseDocType(typeName, out var docType))
                {
                    throw new YamlException(
                        $"Unknown ontology required-keys doc type '{typeName}'. " +
                        "Known types: architecture, onboarding, requirements, adr, plan, spec, " +
                        "runbook, reference, rule, governance, index.");
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
            baseRequiredKeys: section.BaseRequiredKeys,
            requiredKeysByType: requiredKeys);
    }

    /// <summary>
    /// Reads <c>ontology.docs-root</c> in either of its shapes — a directory, or a list of
    /// them — into the canonical list form. A path failure becomes a
    /// <see cref="YamlException"/> so it reaches the operator as <c>KW-CONFIG-001</c>
    /// against the file they wrote, rather than as an unhandled argument error.
    /// </summary>
    internal static IReadOnlyList<string>? NormalizeDocsRoots(object? value)
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

    private static bool TryParseDocType(string name, out DocType docType)
    {
        docType = name?.Trim().ToLowerInvariant() switch
        {
            "architecture" => DocType.Architecture,
            "onboarding" => DocType.Onboarding,
            "requirements" => DocType.Requirements,
            "adr" => DocType.Adr,
            "plan" => DocType.Plan,
            "spec" => DocType.Spec,
            "runbook" => DocType.Runbook,
            "reference" => DocType.Reference,
            "rule" => DocType.Rule,
            "governance" => DocType.Governance,
            "index" => DocType.Index,
            _ => DocType.Unknown
        };
        return docType != DocType.Unknown;
    }
}
