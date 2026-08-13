using System.Net;
using System.Net.Sockets;
using KyberWeave.Core.Networking;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads, merges, and validates documentation-analysis configuration.</summary>
public static class DocsAnalysisConfigLoader
{
    internal static DocsAnalysisConfig Merge(
        DocsAnalysisConfig defaults,
        DocsAnalysisYamlSection? section,
        OntologyConfig ontology)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(ontology);

        if (section is null)
        {
            var resolvedDefaults = defaults.Clone(
                resolvedGlossaryPath: defaults.ResolveGlossaryPath(ontology));
            Validate(resolvedDefaults, ontology);
            return resolvedDefaults;
        }

        var search = MergeSearch(defaults.Search, section.Search);
        var embeddings = MergeEmbeddings(defaults.Embeddings, section.Embeddings);
        var statuses = section.Statuses is null
            ? defaults.Statuses
            : section.Statuses.ToArray();
        var glossaryPath = NormalizeGlossaryPath(section.GlossaryPath, ontology.DocsRoots);

        var merged = defaults.Clone(
            statuses: statuses,
            glossaryPath: glossaryPath,
            resolvedGlossaryPath: glossaryPath ?? defaults.ResolveGlossaryPath(ontology),
            verdictConfidence: section.VerdictConfidence,
            search: search,
            embeddings: embeddings);

        Validate(merged, ontology);
        return merged;
    }

    private static DocsAnalysisSearchConfig MergeSearch(
        DocsAnalysisSearchConfig defaults,
        DocsAnalysisSearchYamlSection? section)
    {
        if (section is null)
            return defaults;

        return defaults.Clone(
            mode: ParseSearchMode(section.Mode),
            minClaimTokens: section.MinClaimTokens,
            lexicalCandidateThreshold: section.LexicalCandidateThreshold,
            lexicalDuplicateThreshold: section.LexicalDuplicateThreshold,
            semanticCandidateThreshold: section.SemanticCandidateThreshold,
            semanticDuplicateThreshold: section.SemanticDuplicateThreshold,
            terminologyContextThreshold: section.TerminologyContextThreshold,
            maxNeighborsPerClaim: section.MaxNeighborsPerClaim,
            maxCodeNeighbors: section.MaxCodeNeighbors,
            maxCandidates: section.MaxCandidates);
    }

    private static DocsAnalysisEmbeddingConfig MergeEmbeddings(
        DocsAnalysisEmbeddingConfig defaults,
        DocsAnalysisEmbeddingYamlSection? section)
    {
        if (section is null)
            return defaults;

        return defaults.Clone(
            mode: ParseEmbeddingMode(section.Mode),
            endpoint: ParseEndpoint(section.Endpoint),
            model: section.Model,
            dimensions: section.Dimensions,
            batchSize: section.BatchSize,
            timeoutSeconds: section.TimeoutSeconds,
            apiKeyEnv: section.ApiKeyEnv);
    }

    private static DocsAnalysisSearchMode? ParseSearchMode(string? value)
    {
        if (value is null)
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "graph" => DocsAnalysisSearchMode.Graph,
            "hybrid" => DocsAnalysisSearchMode.Hybrid,
            "high-recall" => DocsAnalysisSearchMode.HighRecall,
            _ => throw new YamlException(
                $"Unknown docs-analysis.search.mode '{value}'. Known modes: graph, hybrid, high-recall.")
        };
    }

    private static DocsAnalysisEmbeddingMode? ParseEmbeddingMode(string? value)
    {
        if (value is null)
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "off" => DocsAnalysisEmbeddingMode.Off,
            "prefer" => DocsAnalysisEmbeddingMode.Prefer,
            "required" => DocsAnalysisEmbeddingMode.Required,
            _ => throw new YamlException(
                $"Unknown docs-analysis.embeddings.mode '{value}'. Known modes: off, prefer, required.")
        };
    }

    private static Uri? ParseEndpoint(string? value)
    {
        if (value is null)
            return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new YamlException(
                "docs-analysis.embeddings.endpoint must be an absolute HTTP endpoint on loopback.");
        }

        return endpoint;
    }

    private static string? NormalizeGlossaryPath(
        string? value,
        IReadOnlyList<string> docsRoots)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string path;
        try
        {
            path = DocsRootPath.Normalize(value, "docs-analysis.glossary-path");
        }
        catch (ArgumentException ex)
        {
            throw new YamlException(ex.Message);
        }

        if (path.Length == 0 || !docsRoots.Any(root => IsUnderRoot(path, root)))
        {
            throw new YamlException(
                $"docs-analysis.glossary-path '{value}' must be under a configured ontology.docs-root.");
        }

        return path;
    }

    private static bool IsUnderRoot(string path, string root) =>
        root == DocsRootPath.RepositoryRoot
        || path.StartsWith(root + "/", DocsRootPath.PathComparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static void Validate(DocsAnalysisConfig config, OntologyConfig ontology)
    {
        foreach (var status in config.Statuses)
        {
            if (!ontology.Statuses.Contains(status, StringComparer.Ordinal))
            {
                throw new YamlException(
                    $"docs-analysis.statuses contains '{status}', which is not in ontology.statuses.");
            }
        }

        RequireThreshold(config.VerdictConfidence, "docs-analysis.verdict-confidence");
        RequireThreshold(
            config.Search.LexicalCandidateThreshold,
            "docs-analysis.search.lexical-candidate-threshold");
        RequireThreshold(
            config.Search.LexicalDuplicateThreshold,
            "docs-analysis.search.lexical-duplicate-threshold");
        RequireThreshold(
            config.Search.SemanticCandidateThreshold,
            "docs-analysis.search.semantic-candidate-threshold");
        RequireThreshold(
            config.Search.SemanticDuplicateThreshold,
            "docs-analysis.search.semantic-duplicate-threshold");
        RequireThreshold(
            config.Search.TerminologyContextThreshold,
            "docs-analysis.search.terminology-context-threshold");

        RequirePositive(config.Search.MinClaimTokens, "docs-analysis.search.min-claim-tokens");
        RequirePositive(
            config.Search.MaxNeighborsPerClaim,
            "docs-analysis.search.max-neighbors-per-claim");
        RequirePositive(config.Search.MaxCodeNeighbors, "docs-analysis.search.max-code-neighbors");
        RequirePositive(config.Search.MaxCandidates, "docs-analysis.search.max-candidates");
        RequirePositive(config.Embeddings.BatchSize, "docs-analysis.embeddings.batch-size");
        RequirePositive(config.Embeddings.TimeoutSeconds, "docs-analysis.embeddings.timeout-seconds");
        if (config.Embeddings.Dimensions is not null)
            RequirePositive(config.Embeddings.Dimensions.Value, "docs-analysis.embeddings.dimensions");

        var embeddingsEnabled = config.Embeddings.Mode is
            DocsAnalysisEmbeddingMode.Prefer or DocsAnalysisEmbeddingMode.Required;
        if (embeddingsEnabled && config.Embeddings.Endpoint is null)
        {
            throw new YamlException(
                "docs-analysis.embeddings.endpoint is required when embeddings mode is prefer or required.");
        }

        if (embeddingsEnabled && string.IsNullOrWhiteSpace(config.Embeddings.Model))
        {
            throw new YamlException(
                "docs-analysis.embeddings.model is required when embeddings mode is prefer or required.");
        }

        if (config.Embeddings.Endpoint is not null && !ResolvesOnlyToLoopback(config.Embeddings.Endpoint))
        {
            throw new YamlException(
                "docs-analysis.embeddings.endpoint must resolve only to loopback addresses.");
        }
    }

    private static void RequireThreshold(double value, string key)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            throw new YamlException($"{key} must be finite and between 0 and 1 inclusive.");
    }

    private static void RequirePositive(int value, string key)
    {
        if (value <= 0)
            throw new YamlException($"{key} must be a positive integer.");
    }

    private static bool ResolvesOnlyToLoopback(Uri endpoint)
    {
        if (IPAddress.TryParse(endpoint.DnsSafeHost, out var address))
            return LoopbackAddress.IsLoopback(address);

        try
        {
            var addresses = Dns.GetHostAddresses(endpoint.DnsSafeHost);
            return addresses.Length > 0 && addresses.All(LoopbackAddress.IsLoopback);
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
