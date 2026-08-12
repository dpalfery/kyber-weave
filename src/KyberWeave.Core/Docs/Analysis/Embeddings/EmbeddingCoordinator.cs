using System.Text.Json;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Model;

namespace KyberWeave.Core.Docs.Analysis.Embeddings;

/// <summary>Applies embedding mode and safe-cache policy around a provider.</summary>
public sealed class EmbeddingCoordinator
{
    private readonly IEmbeddingGenerator _generator;
    private readonly IAnalysisPersistence _persistence;

    public EmbeddingCoordinator(IEmbeddingGenerator generator, IAnalysisPersistence persistence)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public EmbeddingResolutionResult Resolve(
        IReadOnlyCollection<EmbeddingWorkItem> workItems,
        DocsAnalysisEmbeddingConfig config)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(config);

        if (config.Mode == DocsAnalysisEmbeddingMode.Off)
            return Empty();
        if (!_persistence.IsAvailable)
        {
            return Unavailable(
                config.Mode,
                "Embedding generation was skipped because the analysis cache is not safely ignored.");
        }

        try
        {
            return ResolveFromCacheOrProvider(workItems, config);
        }
        catch (HttpRequestException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (IOException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (JsonException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (OverflowException ex)
        {
            return Unavailable(config.Mode, ex.Message);
        }
        catch (FormatException)
        {
            // Header parsing is the only provider-boundary FormatException expected here.
            // Use a fixed reason so malformed secret text can never reach diagnostics.
            return Unavailable(
                config.Mode,
                "The configured embedding credential is not a valid HTTP bearer token.");
        }
    }

    private EmbeddingResolutionResult ResolveFromCacheOrProvider(
        IReadOnlyCollection<EmbeddingWorkItem> workItems,
        DocsAnalysisEmbeddingConfig config)
    {
        var orderedWork = workItems.ToArray();
        if (orderedWork.Length == 0) return Empty();
        if (orderedWork.Any(item => string.IsNullOrWhiteSpace(item.ContextualHash)))
            throw new ArgumentException("Embedding work items require a contextual hash.", nameof(workItems));

        var model = string.IsNullOrWhiteSpace(config.Model)
            ? throw new ArgumentException("An embedding model is required.", nameof(config))
            : config.Model;
        var provider = _generator.GetProviderFingerprint(config);
        var orderedKeys = orderedWork
            .Select(item => new EmbeddingCacheKey(
                item.ContextualHash,
                provider,
                model,
                config.Dimensions,
                "float"))
            .ToArray();
        var uniqueKeys = orderedKeys.Distinct().ToArray();
        var cached = _persistence.LoadEmbeddings(uniqueKeys);
        var misses = uniqueKeys.Where(key => !cached.ContainsKey(key)).ToArray();
        var generatedByKey = new Dictionary<EmbeddingCacheKey, StoredEmbedding>();
        var usage = EmbeddingUsage.None;

        if (misses.Length > 0)
        {
            var firstInputByKey = orderedKeys
                .Select((key, index) => new { key, orderedWork[index].Input })
                .GroupBy(item => item.key)
                .ToDictionary(group => group.Key, group => group.First().Input);
            var generated = _generator.Generate(
                misses,
                misses.Select(key => firstInputByKey[key]).ToArray(),
                config);
            if (generated.Embeddings.Count != misses.Length)
                throw new InvalidDataException("The embedding provider returned an incomplete result set.");

            generatedByKey = generated.Embeddings.ToDictionary(embedding => embedding.Key);
            if (generatedByKey.Count != misses.Length
                || misses.Any(key => !generatedByKey.ContainsKey(key)))
            {
                throw new InvalidDataException("The embedding provider returned mismatched cache keys.");
            }

            _persistence.SaveEmbeddings(generated.Embeddings);
            usage = generated.Usage;
        }

        var ordered = orderedKeys.Select(key => cached.TryGetValue(key, out var hit)
            ? hit
            : generatedByKey[key]).ToArray();
        return new EmbeddingResolutionResult(
            ordered,
            new DiagnosticReport(),
            orderedKeys.Count(key => cached.ContainsKey(key)),
            orderedKeys.Count(key => !cached.ContainsKey(key)),
            usage);
    }

    private static EmbeddingResolutionResult Empty() =>
        new([], new DiagnosticReport(), 0, 0, EmbeddingUsage.None);

    internal static EmbeddingResolutionResult Unavailable(
        DocsAnalysisEmbeddingMode mode,
        string reason)
    {
        var diagnostics = new DiagnosticReport();
        diagnostics.Add(new Diagnostic(
            DocumentationAnalyzer.EmbeddingUnavailableRuleCode,
            mode == DocsAnalysisEmbeddingMode.Required ? Severity.Error : Severity.Warning,
            $"Embedding analysis is unavailable: {reason}",
            "docs-analysis.embeddings",
            Hint: mode == DocsAnalysisEmbeddingMode.Required
                ? "Restore the configured local embedding endpoint and safe analysis cache, or set embeddings.mode to prefer/off."
                : "Lexical analysis will continue without embeddings."));
        return new EmbeddingResolutionResult([], diagnostics, 0, 0, EmbeddingUsage.None);
    }
}
