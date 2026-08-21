using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Analysis;

/// <summary>Runs graph-first, bounded documentation analysis over a parsed corpus.</summary>
public sealed partial class DocumentationAnalyzer(
    ClaimExtractor extractor,
    IReadOnlyList<IClaimCandidateSource> candidateSources,
    IEmbeddingGenerator? embeddingGenerator,
    IAnalysisPersistence? persistence)
{
    public const string DuplicateRuleCode = "KW-DOC-ANALYSIS-001";
    public const string ConflictRuleCode = "KW-DOC-ANALYSIS-002";
    public const string TerminologyRuleCode = "KW-DOC-ANALYSIS-003";
    public const string IgnoreMarkupRuleCode = "KW-DOC-ANALYSIS-004";
    public const string CodeGraphUnavailableRuleCode = "KW-DOC-ANALYSIS-005";
    public const string EmbeddingUnavailableRuleCode = "KW-DOC-ANALYSIS-006";

    public const string AnalyzerVersion = "analyzer/v1";
    public const string RubricVersion = "rubric/v1";

    private static readonly IReadOnlySet<string> TerminologyStopWords =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "about", "after", "again", "before", "being", "could", "every", "from",
            "into", "must", "should", "that", "their", "there", "these", "this", "those",
            "using", "with", "would", "runner", "record", "report", "request", "analysis",
            "behavior", "documentation", "reference"
        };

    private readonly ClaimExtractor _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    private readonly IReadOnlyList<IClaimCandidateSource> _candidateSources = candidateSources ?? throw new ArgumentNullException(nameof(candidateSources));

    /// <summary>Analyzes eligible documents without changing the corpus.</summary>
    public DocumentationAnalysisResult Analyze(
        DocumentSet documents,
        DocGraphProjection graph,
        DocsAnalysisConfig config,
        AnalysisGlossary? glossary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        DiagnosticReport diagnostics = new DiagnosticReport();
        IReadOnlyList<Claim> claims = ExtractEligibleClaims(documents, config, diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, AnalysisCandidate> candidates = new Dictionary<string, AnalysisCandidate>(StringComparer.Ordinal);

        AddExactDuplicateClusters(claims, candidates);

        int graphComparisons = 0;
        int lexicalComparisons = 0;
        int embeddingComparisons = 0;
        int graphCandidates = 0;
        int lexicalCandidates = 0;
        int embeddingCandidates = 0;
        int embeddingCacheHits = 0;
        int embeddingCacheMisses = 0;
        int embeddingPromptTokens = 0;
        int embeddingTotalTokens = 0;
        bool sourceTruncated = false;
        List<ClaimPairCandidate> semanticSeedPairs = new List<ClaimPairCandidate>();

        ClaimCandidateSourceRequest request = new ClaimCandidateSourceRequest(claims, graph, config.Search);
        foreach (IClaimCandidateSource source in _candidateSources.Where(source => ShouldRun(source.Kind, config)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaimCandidateSourceResult result = source.FindCandidates(request)
                ?? throw new InvalidOperationException("A claim candidate source returned null.");
            sourceTruncated |= result.Truncated;
            switch (source.Kind)
            {
                case CandidateSourceKind.Graph:
                    graphComparisons += result.ComparisonCount;
                    graphCandidates += result.Pairs.Count;
                    break;
                case CandidateSourceKind.Lexical:
                    lexicalComparisons += result.ComparisonCount;
                    lexicalCandidates += result.Pairs.Count;
                    break;
                case CandidateSourceKind.Embedding:
                    embeddingComparisons += result.ComparisonCount;
                    embeddingCandidates += result.Pairs.Count;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown candidate source kind '{source.Kind}'.");
            }

            foreach (ClaimPairCandidate pair in result.Pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Kind != CandidateSourceKind.Embedding)
                    semanticSeedPairs.Add(pair);
                ClassifyPair(pair, config.Search, glossary, candidates);
            }
        }

        if (config.Embeddings.Mode != DocsAnalysisEmbeddingMode.Off)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmbeddingResolutionResult resolution = ResolveEmbeddings(claims, config.Embeddings);
            diagnostics.AddRange(resolution.Diagnostics.Items);
            embeddingCacheHits = resolution.CacheHits;
            embeddingCacheMisses = resolution.CacheMisses;
            embeddingPromptTokens = resolution.Usage.PromptTokens;
            embeddingTotalTokens = resolution.Usage.TotalTokens;

            if (resolution.Embeddings.Count == claims.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClaimCandidateSourceResult result = EmbeddingCandidateBuilder.Build(
                    claims,
                    resolution.Embeddings,
                    semanticSeedPairs,
                    config.Search);
                sourceTruncated |= result.Truncated;
                embeddingComparisons += result.ComparisonCount;
                embeddingCandidates += result.Pairs.Count;
                foreach (ClaimPairCandidate pair in result.Pairs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ClassifyPair(pair, config.Search, glossary, candidates);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AnalysisCandidate> consolidated = ConsolidateTerminology(candidates.Values);
        IReadOnlyList<AnalysisCandidate> reviewed = ApplyVerdicts(consolidated, config.VerdictConfidence);
        AnalysisCandidate[] ordered = reviewed
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.Term, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
        bool truncated = sourceTruncated || ordered.Length > config.Search.MaxCandidates;
        AnalysisCandidate[] visible = ordered.Take(config.Search.MaxCandidates).ToArray();

        foreach (AnalysisCandidate candidate in visible)
            diagnostics.Add(ToDiagnostic(candidate, config.VerdictConfidence));

        AnalysisMetrics metrics = new AnalysisMetrics(
            claims.Count,
            graphComparisons,
            lexicalComparisons,
            embeddingComparisons,
            graphCandidates,
            lexicalCandidates,
            embeddingCandidates,
            truncated);
        AddMetrics(diagnostics, metrics);
        diagnostics.AddMetric("embeddingCacheHits", embeddingCacheHits);
        diagnostics.AddMetric("embeddingCacheMisses", embeddingCacheMisses);
        diagnostics.AddMetric("embeddingPromptTokens", embeddingPromptTokens);
        diagnostics.AddMetric("embeddingTotalTokens", embeddingTotalTokens);
        return new DocumentationAnalysisResult(visible, diagnostics, metrics);
    }

    private EmbeddingResolutionResult ResolveEmbeddings(
        IReadOnlyList<Claim> claims,
        DocsAnalysisEmbeddingConfig config)
    {
        if (embeddingGenerator is null)
        {
            return EmbeddingCoordinator.Unavailable(
                config.Mode,
                "No embedding provider is configured in this host.");
        }
        if (persistence is null)
        {
            return EmbeddingCoordinator.Unavailable(
                config.Mode,
                "No safe analysis persistence provider is configured in this host.");
        }

        EmbeddingCoordinator coordinator = new EmbeddingCoordinator(embeddingGenerator, persistence);
        return coordinator.Resolve(
            claims.Select(claim => new EmbeddingWorkItem(
                claim.ContextualHash,
                claim.ContextualText)).ToArray(),
            config);
    }

    private IReadOnlyList<Claim> ExtractEligibleClaims(
        DocumentSet documents,
        DocsAnalysisConfig config,
        DiagnosticReport diagnostics)
    {
        HashSet<string> statuses = new HashSet<string>(config.Statuses, StringComparer.OrdinalIgnoreCase);
        string? glossaryPath = NormalizePath(config.GlossaryPath ?? config.ResolvedGlossaryPath);
        List<Claim> claims = new List<Claim>();

        foreach (DocumentModel document in documents.Documents)
        {
            if (!statuses.Contains(document.Frontmatter.Status ?? string.Empty)) continue;
            if (glossaryPath is not null
                 && StringComparer.OrdinalIgnoreCase.Equals(
                     glossaryPath,
                     NormalizePath(document.RelativePath)))
            {
                continue;
            }

            ClaimExtractionResult extraction = _extractor.Extract(document);
            diagnostics.AddRange(extraction.Diagnostics.Items);
            claims.AddRange(extraction.Claims.Where(claim =>
                TokenPattern().Count(claim.Text) >= config.Search.MinClaimTokens));
        }

        return claims;
    }

    private static void AddExactDuplicateClusters(
        IReadOnlyList<Claim> claims,
        IDictionary<string, AnalysisCandidate> candidates)
    {
        foreach (IGrouping<string, Claim> group in claims
                     .Where(claim => !claim.IgnoreRules.HasFlag(IgnoreRule.Duplicate))
                     .GroupBy(claim => claim.ContentHash, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            Claim[] groupedClaims = group
                .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
                .ThenBy(claim => claim.StartLine)
                .ToArray();
            string id = AnalysisCandidateId.Compute(
                AnalysisRuleKind.Duplicate,
                null,
                groupedClaims.Select(claim => claim.ContentHash),
                AnalyzerVersion,
                RubricVersion);
            candidates[id] = new AnalysisCandidate(
                id,
                AnalysisRuleKind.Duplicate,
                groupedClaims,
                new CandidateScore(1, null, 0),
                IsExact: true);
        }
    }

    private static void ClassifyPair(
        ClaimPairCandidate pair,
        DocsAnalysisSearchConfig search,
        AnalysisGlossary? glossary,
        IDictionary<string, AnalysisCandidate> candidates)
    {
        if (StringComparer.Ordinal.Equals(pair.Left.ContentHash, pair.Right.ContentHash)) return;

        bool ordinaryCandidate = IsOrdinaryCandidate(pair.Score, search);
        if (ordinaryCandidate
            && !pair.Left.IgnoreRules.HasFlag(IgnoreRule.Duplicate)
            && !pair.Right.IgnoreRules.HasFlag(IgnoreRule.Duplicate)
            && IsNearDuplicate(pair.Score, search))
        {
            AddOrMerge(candidates, CreateCandidate(AnalysisRuleKind.Duplicate, pair, null));
        }

        if (ordinaryCandidate
            && !pair.Left.IgnoreRules.HasFlag(IgnoreRule.Conflict)
            && !pair.Right.IgnoreRules.HasFlag(IgnoreRule.Conflict)
            && pair.Score.Graph > 0
            && HasConflictSignal(pair.Left, pair.Right))
        {
            AddOrMerge(candidates, CreateCandidate(AnalysisRuleKind.Conflict, pair, null));
        }

        if (pair.Left.IgnoreRules.HasFlag(IgnoreRule.Terminology)
            || pair.Right.IgnoreRules.HasFlag(IgnoreRule.Terminology)
            || pair.Score.Lexical > search.TerminologyContextThreshold)
        {
            return;
        }

        foreach (string term in SharedInformativeTerms(pair.Left.Text, pair.Right.Text))
        {
            Claim[] claims = [pair.Left, pair.Right];
            if (glossary?.Covers(term, claims) == true) continue;
            AddOrMerge(candidates, CreateCandidate(AnalysisRuleKind.Terminology, pair, term));
        }
    }

    private static bool IsNearDuplicate(CandidateScore score, DocsAnalysisSearchConfig search) =>
        score.Lexical >= search.LexicalDuplicateThreshold
        || score.Semantic >= search.SemanticDuplicateThreshold;

    private static bool IsOrdinaryCandidate(CandidateScore score, DocsAnalysisSearchConfig search) =>
        score.Lexical >= search.LexicalCandidateThreshold
        || score.Semantic >= search.SemanticCandidateThreshold;

    private static AnalysisCandidate CreateCandidate(
        AnalysisRuleKind kind,
        ClaimPairCandidate pair,
        string? term)
    {
        Claim[] claims = new[] { pair.Left, pair.Right }
            .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
            .ThenBy(claim => claim.StartLine)
            .ToArray();
        string id = AnalysisCandidateId.Compute(
            kind,
            term,
            claims.Select(claim => claim.ContentHash),
            AnalyzerVersion,
            RubricVersion);
        return new AnalysisCandidate(
            id,
            kind,
            claims,
            pair.Score,
            Term: term,
            Sources: [pair.Source]);
    }

    private static void AddOrMerge(
        IDictionary<string, AnalysisCandidate> candidates,
        AnalysisCandidate candidate)
    {
        if (!candidates.TryGetValue(candidate.Id, out AnalysisCandidate? existing))
        {
            candidates[candidate.Id] = candidate;
            return;
        }

        candidates[candidate.Id] = existing with
        {
            Claims = existing.Claims.Concat(candidate.Claims)
                .Distinct()
                .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
                .ThenBy(claim => claim.StartLine)
                .ToArray(),
            Score = new CandidateScore(
                Math.Max(existing.Score.Lexical, candidate.Score.Lexical),
                Max(existing.Score.Semantic, candidate.Score.Semantic),
                Math.Max(existing.Score.Graph, candidate.Score.Graph)),
            Sources = existing.Sources.Concat(candidate.Sources).Distinct().ToArray()
        };
    }

    private static IReadOnlyList<AnalysisCandidate> ConsolidateTerminology(
        IEnumerable<AnalysisCandidate> candidates)
    {
        AnalysisCandidate[] materialized = candidates.ToArray();
        List<AnalysisCandidate> result = materialized
            .Where(candidate => candidate.Kind != AnalysisRuleKind.Terminology)
            .ToList();
        foreach (IGrouping<string, AnalysisCandidate> group in materialized
                     .Where(candidate => candidate.Kind == AnalysisRuleKind.Terminology)
                     .GroupBy(candidate => candidate.Term!, StringComparer.Ordinal))
        {
            Claim[] claims = group.SelectMany(candidate => candidate.Claims)
                .Distinct()
                .OrderBy(claim => claim.FilePath, StringComparer.Ordinal)
                .ThenBy(claim => claim.StartLine)
                .ToArray();
            AnalysisCandidate first = group.First();
            string id = AnalysisCandidateId.Compute(
                AnalysisRuleKind.Terminology,
                group.Key,
                claims.Select(claim => claim.ContentHash),
                AnalyzerVersion,
                RubricVersion);
            result.Add(first with
            {
                Id = id,
                Claims = claims,
                Score = new CandidateScore(
                    group.Max(candidate => candidate.Score.Lexical),
                    MaximumSemantic(group),
                    group.Max(candidate => candidate.Score.Graph)),
                Sources = group.SelectMany(candidate => candidate.Sources).Distinct().ToArray()
            });
        }

        return result;
    }

    private static double? MaximumSemantic(IEnumerable<AnalysisCandidate> candidates)
    {
        double[] scores = candidates
            .Select(candidate => candidate.Score.Semantic)
            .Where(score => score.HasValue)
            .Select(score => score!.Value)
            .ToArray();
        return scores.Length == 0 ? null : scores.Max();
    }

    private IReadOnlyList<AnalysisCandidate> ApplyVerdicts(
        IEnumerable<AnalysisCandidate> candidates,
        double confidenceThreshold)
    {
        AnalysisCandidate[] materialized = candidates.ToArray();
        if (persistence?.IsAvailable != true || materialized.Length == 0) return materialized;

        IReadOnlyDictionary<string, AnalysisVerdict> verdicts = persistence.LoadVerdicts(materialized.Select(candidate => candidate.Id).ToArray());
        return materialized
            .Where(candidate => !IsSuppressed(candidate, verdicts, confidenceThreshold))
            .Select(candidate => verdicts.TryGetValue(candidate.Id, out AnalysisVerdict? verdict)
                ? candidate with { Verdict = verdict }
                : candidate)
            .ToArray();
    }

    private static bool IsSuppressed(
        AnalysisCandidate candidate,
        IReadOnlyDictionary<string, AnalysisVerdict> verdicts,
        double confidenceThreshold) =>
        verdicts.TryGetValue(candidate.Id, out AnalysisVerdict? verdict)
        && verdict.Confidence >= confidenceThreshold
        && verdict.Label == AnalysisVerdictLabel.Benign;

    private static Diagnostic ToDiagnostic(AnalysisCandidate candidate, double confidenceThreshold)
    {
        Claim primary = candidate.Claims[0];
        DiagnosticLocation[] related = candidate.Claims.Skip(1)
            .Select(claim => new DiagnosticLocation(
                claim.FilePath,
                claim.StartLine,
                claim.EndLine,
                "Related analysis evidence."))
            .ToArray();

        (string code, Severity severity, string message, string hint) = candidate.Kind switch
        {
            AnalysisRuleKind.Duplicate => (
                DuplicateRuleCode,
                DuplicateSeverity(candidate, confidenceThreshold),
                candidate.IsExact
                    ? $"The same documentation claim appears in {candidate.Claims.Count} locations."
                    : "These documentation claims may express the same requirement.",
                "Review the evidence and designate a canonical source; do not remove intentional repetition without review."),
            AnalysisRuleKind.Conflict => (
                ConflictRuleCode,
                IsConfirmed(candidate, AnalysisVerdictLabel.Conflict, confidenceThreshold)
                    ? Severity.Error
                    : Severity.Info,
                "These related documentation claims contain potentially incompatible values or obligations.",
                "Review scope and time applicability, then import a conflict, benign, or uncertain verdict."),
            AnalysisRuleKind.Terminology => (
                TerminologyRuleCode,
                Severity.Warning,
                $"The term '{candidate.Term}' appears to have distinct meanings in divergent contexts.",
                "Define approved, scope-qualified senses in the managed glossary."),
            _ => throw new InvalidOperationException($"Unknown analysis rule kind '{candidate.Kind}'.")
        };

        return new Diagnostic(
            code,
            severity,
            message,
            primary.DocumentIdentity,
            primary.FilePath,
            hint,
            primary.StartLine,
            primary.EndLine,
            related);
    }

    private static Severity DuplicateSeverity(AnalysisCandidate candidate, double confidenceThreshold) =>
        candidate.IsExact || IsConfirmed(candidate, AnalysisVerdictLabel.Duplicate, confidenceThreshold)
            ? Severity.Warning
            : Severity.Info;

    private static bool IsConfirmed(
        AnalysisCandidate candidate,
        AnalysisVerdictLabel label,
        double confidenceThreshold) =>
        candidate.Verdict is { } verdict
        && verdict.Label == label
        && verdict.Confidence >= confidenceThreshold;

    private static bool HasConflictSignal(Claim left, Claim right)
    {
        if (left.Kind == ClaimKind.CodeBlock && right.Kind == ClaimKind.CodeBlock)
        {
            if (IsShellFence(left) && IsShellFence(right))
                return IsDifferentShellCommand(left, right);
        }

        if (NegationPattern().IsMatch(left.Text) != NegationPattern().IsMatch(right.Text)) return true;

        HashSet<string> leftModal = ModalPattern().Matches(left.Text).Select(match => match.Value.ToLowerInvariant()).ToHashSet();
        HashSet<string> rightModal = ModalPattern().Matches(right.Text).Select(match => match.Value.ToLowerInvariant()).ToHashSet();
        if (leftModal.Count > 0 && rightModal.Count > 0 && !leftModal.SetEquals(rightModal)) return true;

        if (DifferentCapturedValues(NumberPattern(), left.Text, right.Text)) return true;
        if (DifferentCapturedValues(PathPattern(), left.Text, right.Text)) return true;
        if (DifferentCapturedValues(CodeLiteralPattern(), left.Text, right.Text)) return true;
        if (CommandPattern().IsMatch(left.Text)
            && CommandPattern().IsMatch(right.Text)
            && !StringComparer.OrdinalIgnoreCase.Equals(left.Text, right.Text))
        {
            return true;
        }

        return false;
    }

    private static bool IsDifferentShellCommand(Claim left, Claim right)
    {
        IReadOnlyList<string> leftCommands = MeaningfulShellLines(left);
        IReadOnlyList<string> rightCommands = MeaningfulShellLines(right);
        return leftCommands.Count > 0
            && rightCommands.Count > 0
            && !leftCommands.SequenceEqual(rightCommands, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsShellFence(Claim claim) => FenceLanguage(claim) is
        "sh" or "shell" or "bash" or "zsh" or "fish" or "powershell" or "pwsh" or "cmd" or "bat" or "batch";

    private static string FenceLanguage(Claim claim) =>
        (claim.FenceInfo ?? string.Empty)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .ToLowerInvariant() ?? string.Empty;

    private static IReadOnlyList<string> MeaningfulShellLines(Claim claim) =>
        claim.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !IsShellComment(claim, line))
            .ToArray();

    private static bool IsShellComment(Claim claim, string line)
    {
        if (FenceLanguage(claim) is "cmd" or "bat" or "batch")
        {
            return line.StartsWith("::", StringComparison.Ordinal)
                || line.Equals("rem", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase);
        }

        return line.StartsWith('#');
    }

    private static bool DifferentCapturedValues(Regex pattern, string left, string right)
    {
        HashSet<string> leftValues = pattern.Matches(left).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> rightValues = pattern.Matches(right).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftValues.Count > 0 && rightValues.Count > 0 && !leftValues.SetEquals(rightValues);
    }

    private static IReadOnlyList<string> SharedInformativeTerms(string left, string right)
    {
        HashSet<string> leftTerms = TermPattern().Matches(left.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(IsInformativeTerm)
            .ToHashSet(StringComparer.Ordinal);
        return TermPattern().Matches(right.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(IsInformativeTerm)
            .Where(leftTerms.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsInformativeTerm(string term) =>
        term.Length >= 4 && !TerminologyStopWords.Contains(term);

    private bool ShouldRun(CandidateSourceKind kind, DocsAnalysisConfig config) => kind switch
    {
        CandidateSourceKind.Graph => true,
        CandidateSourceKind.Lexical => config.Search.Mode != DocsAnalysisSearchMode.Graph,
        CandidateSourceKind.Embedding => embeddingGenerator is not null
            && config.Embeddings.Mode != DocsAnalysisEmbeddingMode.Off,
        _ => false
    };

    private static double? Max(double? left, double? right) => (left, right) switch
    {
        (null, null) => null,
        (not null, null) => left,
        (null, not null) => right,
        _ => Math.Max(left.Value, right.Value)
    };

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static void AddMetrics(DiagnosticReport diagnostics, AnalysisMetrics metrics)
    {
        diagnostics.AddMetric("extractedClaims", metrics.ExtractedClaims);
        diagnostics.AddMetric("graphComparisons", metrics.GraphComparisons);
        diagnostics.AddMetric("lexicalComparisons", metrics.LexicalComparisons);
        diagnostics.AddMetric("embeddingComparisons", metrics.EmbeddingComparisons);
        diagnostics.AddMetric("graphCandidates", metrics.GraphCandidates);
        diagnostics.AddMetric("lexicalCandidates", metrics.LexicalCandidates);
        diagnostics.AddMetric("embeddingCandidates", metrics.EmbeddingCandidates);
        diagnostics.AddMetric("truncated", metrics.Truncated);
    }

    [GeneratedRegex("[\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("\\b(?:not|never|no|cannot|can't|won't|isn't|aren't|doesn't|don't)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegationPattern();

    [GeneratedRegex("\\b(?:must|may|should|shall|can|will)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModalPattern();

    [GeneratedRegex("\\b(\\d+(?:\\.\\d+)*)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("(?<!:)\\b(/(?:[A-Za-z0-9._~-]+/?)+)", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeLiteralPattern();

    [GeneratedRegex("^\\s*(?:run|set)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex("[\\p{L}][\\p{L}\\p{N}-]*", RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();
}
