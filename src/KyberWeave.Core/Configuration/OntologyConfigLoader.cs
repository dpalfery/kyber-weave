using KyberWeave.Core.Docs.Model;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads and merges ontology overrides from <c>kyber-weave.yml</c>.</summary>
public static class OntologyConfigLoader
{
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
            docsRoot: section.DocsRoot,
            excludedPathSegments: section.ExcludedSegments,
            excludedFiles: section.ExcludedFiles,
            catalogComponentColumn: section.Catalog?.ComponentColumn,
            catalogOwnerColumn: section.Catalog?.OwnerColumn,
            docTypes: section.DocTypes,
            statuses: section.Statuses,
            baseRequiredKeys: section.BaseRequiredKeys,
            requiredKeysByType: requiredKeys);
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
