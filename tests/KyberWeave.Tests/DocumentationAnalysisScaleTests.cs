using System.Collections.ObjectModel;
using System.Diagnostics;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Candidates;
using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using Xunit;
using Xunit.Abstractions;

namespace KyberWeave.Tests;

/// <summary>
/// Guards the documented default-analysis scale envelope with deterministic data. The
/// algorithmic assertions are the primary regression signal; elapsed time and allocation
/// ceilings are Release-only acceptance checks so debug instrumentation does not make the
/// ordinary inner loop flaky.
/// </summary>
[Collection(ScaleAcceptanceCollection.Name)]
public sealed class DocumentationAnalysisScaleTests(ITestOutputHelper output)
{
    private const int DocumentCount = 1_000;
    private const int ClaimsPerDocument = 10;
    private const int ClaimCount = DocumentCount * ClaimsPerDocument;
    private const int MaximumReviewCandidates = 500;

    [Fact]
    [Trait("Category", "Scale")]
    public void Analyze_DefaultHybridTenThousandClaims_RemainsBoundedAndWithinReleaseEnvelope()
    {
        var documents = ScaleCorpus.Create(DocumentCount, ClaimsPerDocument);
        var graph = DocGraphProjection.Build(documents, FakeCodeGraphResolver.WithSymbols());
        var config = DefaultHybridConfig();
        var graphSource = new RecordingCandidateSource(new GraphClaimCandidateSource());
        var lexicalSource = new RecordingCandidateSource(new SparseLexicalCandidateSource());
        var analyzer = new DocumentationAnalyzer(
            new ClaimExtractor(),
            [graphSource, lexicalSource],
            embeddingGenerator: null,
            persistence: null);

        WarmCandidateGeneration();
        var measurement = Measure(() => analyzer.Analyze(documents, graph, config));
        var result = measurement.Value;
        var allPairs = (long)ClaimCount * (ClaimCount - 1) / 2;
        var configuredBound = (long)ClaimCount * config.Search.MaxNeighborsPerClaim;

        output.WriteLine(
            "Hybrid scale: {0} claims, {1} graph comparisons, {2} lexical comparisons, " +
            "{3} candidates, {4:F3}s, {5:F1} MiB peak working set, {6:F1} MiB allocated.",
            result.Metrics.ExtractedClaims,
            result.Metrics.GraphComparisons,
            result.Metrics.LexicalComparisons,
            result.Candidates.Count,
            measurement.Elapsed.TotalSeconds,
            measurement.PeakWorkingSetBytes / 1024d / 1024d,
            measurement.AllocatedBytes / 1024d / 1024d);

        Assert.Equal(ClaimCount, result.Metrics.ExtractedClaims);
        Assert.Equal(DocsAnalysisEmbeddingMode.Off, config.Embeddings.Mode);
        Assert.Equal(0, result.Metrics.EmbeddingComparisons);
        Assert.Equal(0, result.Metrics.EmbeddingCandidates);

        Assert.NotNull(graphSource.LastResult);
        Assert.NotNull(lexicalSource.LastResult);
        Assert.Equal(graphSource.LastResult.ComparisonCount, result.Metrics.GraphComparisons);
        Assert.Equal(lexicalSource.LastResult.ComparisonCount, result.Metrics.LexicalComparisons);
        Assert.Equal(graphSource.LastResult.Pairs.Count, result.Metrics.GraphCandidates);
        Assert.Equal(lexicalSource.LastResult.Pairs.Count, result.Metrics.LexicalCandidates);

        Assert.True(
            result.Metrics.GraphComparisons <= configuredBound,
            $"Graph source performed {result.Metrics.GraphComparisons:N0} comparisons; " +
            $"the configured top-k bound is {configuredBound:N0}.");
        Assert.True(
            result.Metrics.LexicalComparisons <= configuredBound,
            $"Lexical source performed {result.Metrics.LexicalComparisons:N0} comparisons; " +
            $"the deterministic sparse-neighborhood bound is {configuredBound:N0}.");
        Assert.True(
            (long)result.Metrics.GraphComparisons + result.Metrics.LexicalComparisons < allPairs,
            $"Default hybrid analysis approached the {allPairs:N0}-pair all-pairs space.");
        Assert.Equal(MaximumReviewCandidates, result.Candidates.Count);
        Assert.True(graphSource.LastResult.Pairs.Count <= MaximumReviewCandidates);
        Assert.True(lexicalSource.LastResult.Pairs.Count <= MaximumReviewCandidates);
        Assert.True(
            result.Metrics.Truncated,
            "The bounded sources discarded eligible pairs at the 500-candidate review cap, " +
            "so the reported truncation metric must not claim the result was complete.");

#if !DEBUG
        Assert.True(
            measurement.Elapsed < TimeSpan.FromSeconds(10),
            $"Default hybrid analysis took {measurement.Elapsed.TotalSeconds:F3}s; the Release target is under 10s.");
        Assert.True(
            measurement.PeakWorkingSetBytes < 512L * 1024 * 1024,
            $"Default hybrid analysis used {measurement.PeakWorkingSetBytes / 1024d / 1024d:F1} MiB peak working set; " +
            "the Release target is under 512 MiB.");
#endif
    }

    [Fact]
    [Trait("Category", "Scale")]
    public void HighRecall_ReportsItsExplicitQuadraticFirstPassAndIsOutsideDefaultSla()
    {
        const int documentsCount = 160;
        var documents = ScaleCorpus.Create(documentsCount, claimsPerDocument: 1);
        var claims = Extract(documents);
        var graph = DocGraphProjection.Build(documents, FakeCodeGraphResolver.WithSymbols());
        var source = new SparseLexicalCandidateSource();
        var hybridRequest = new ClaimCandidateSourceRequest(
            claims,
            graph,
            Search(DocsAnalysisSearchMode.Hybrid));
        var highRecallRequest = new ClaimCandidateSourceRequest(
            claims,
            graph,
            Search(DocsAnalysisSearchMode.HighRecall));

        var hybrid = source.FindCandidates(hybridRequest);
        var highRecall = source.FindCandidates(highRecallRequest);
        var quadraticFirstPass = documentsCount * (documentsCount - 1) / 2;

        output.WriteLine(
            "High-recall first pass: {0:N0} comparisons versus {1:N0} for hybrid; " +
            "the quadratic pass is intentionally outside the default SLA.",
            highRecall.ComparisonCount,
            hybrid.ComparisonCount);

        Assert.Equal(quadraticFirstPass, highRecall.ComparisonCount);
        Assert.True(highRecall.ComparisonCount > hybrid.ComparisonCount);
        Assert.True(highRecall.Pairs.Count <= highRecallRequest.Search.MaxCandidates);
        Assert.True(
            highRecall.Pairs.Count
            <= claims.Count * highRecallRequest.Search.MaxNeighborsPerClaim);
    }

    private static DocsAnalysisConfig DefaultHybridConfig() => new()
    {
        Statuses = ["current"],
        Search = Search(DocsAnalysisSearchMode.Hybrid),
        Embeddings = DocsAnalysisEmbeddingConfig.ProductDefaults
    };

    private static DocsAnalysisSearchConfig Search(DocsAnalysisSearchMode mode) => new()
    {
        Mode = mode,
        MinClaimTokens = 5,
        LexicalCandidateThreshold = 0.45,
        LexicalDuplicateThreshold = 0.90,
        SemanticCandidateThreshold = 0.78,
        SemanticDuplicateThreshold = 0.92,
        TerminologyContextThreshold = 0.30,
        MaxNeighborsPerClaim = 10,
        MaxCodeNeighbors = 50,
        MaxCandidates = MaximumReviewCandidates
    };

    private static IReadOnlyList<Claim> Extract(DocumentSet documents)
    {
        var extractor = new ClaimExtractor();
        return documents.Documents
            .SelectMany(document => extractor.Extract(document).Claims)
            .ToArray();
    }

    private static void WarmCandidateGeneration()
    {
        var documents = ScaleCorpus.Create(documentCount: 2, claimsPerDocument: 2);
        var graph = DocGraphProjection.Build(documents, FakeCodeGraphResolver.WithSymbols());
        var claims = Extract(documents);
        var request = new ClaimCandidateSourceRequest(
            claims,
            graph,
            Search(DocsAnalysisSearchMode.Hybrid));
        _ = new GraphClaimCandidateSource().FindCandidates(request);
        _ = new SparseLexicalCandidateSource().FindCandidates(request);
    }

    private static Measurement<T> Measure<T>(Func<T> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        var peakWorkingSet = process.WorkingSet64;
        using var samplingCancellation = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    process.Refresh();
                    InterlockedExtensions.Max(ref peakWorkingSet, process.WorkingSet64);
                    await Task.Delay(TimeSpan.FromMilliseconds(5), samplingCancellation.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (samplingCancellation.IsCancellationRequested)
            {
                // The measurement completed normally.
            }
        });
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var value = action();
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        samplingCancellation.Cancel();
        sampler.GetAwaiter().GetResult();
        process.Refresh();
        InterlockedExtensions.Max(ref peakWorkingSet, process.WorkingSet64);
        return new Measurement<T>(value, stopwatch.Elapsed, peakWorkingSet, allocated);
    }

    private sealed record Measurement<T>(
        T Value,
        TimeSpan Elapsed,
        long PeakWorkingSetBytes,
        long AllocatedBytes);

    private static class InterlockedExtensions
    {
        public static void Max(ref long location, long candidate)
        {
            var current = Volatile.Read(ref location);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref location, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private sealed class RecordingCandidateSource(IClaimCandidateSource inner) : IClaimCandidateSource
    {
        public CandidateSourceKind Kind => inner.Kind;

        public ClaimCandidateSourceResult? LastResult { get; private set; }

        public ClaimCandidateSourceResult FindCandidates(ClaimCandidateSourceRequest request)
        {
            LastResult = inner.FindCandidates(request);
            return LastResult;
        }
    }

    private static class ScaleCorpus
    {
        public static DocumentSet Create(int documentCount, int claimsPerDocument) => new()
        {
            Documents = Enumerable.Range(0, documentCount)
                .Select(documentIndex => Document(documentIndex, claimsPerDocument))
                .ToArray()
        };

        private static DocumentModel Document(int documentIndex, int claimsPerDocument)
        {
            var paragraphs = Enumerable.Range(0, claimsPerDocument)
                .Select(claimIndex =>
                    $"Analyzer groupdoc{documentIndex} retains bounded evidence itemclaim{documentIndex * claimsPerDocument + claimIndex} " +
                    "while producing deterministic review candidates.");
            var body = $"## Behavior\n\n{string.Join("\n\n", paragraphs)}\n";
            return new DocumentModel
            {
                RelativePath = $"docs/scale-{documentIndex:D4}.md",
                FilePath = $"/repo/docs/scale-{documentIndex:D4}.md",
                HasFrontmatter = true,
                Frontmatter = new DocumentFrontmatter
                {
                    Id = $"scale-{documentIndex:D4}",
                    Title = $"Scale {documentIndex:D4}",
                    DocType = "reference",
                    Status = "current",
                    Component = $"ScaleComponent{documentIndex:D4}",
                    CodeRefs = new Collection<string>()
                },
                DocType = DocType.Reference,
                Status = DocStatus.Current,
                Body = body,
                RawMarkdown = body,
                BodyStartLine = 1
            };
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScaleAcceptanceCollection
{
    public const string Name = "Documentation analysis scale acceptance";
}
