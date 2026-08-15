using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Specifies the CLI's adapter lifetime and cost boundary. Unsafe persistence must prevent
/// both cache writes and embedding construction, and every constructed disposable is owned
/// by the command runtime.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Tests intentionally transfer fake adapter ownership to the runtime and assert disposal.")]
public sealed class DocsAnalysisCompositionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Off, false, true, 0, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, false, true, 0, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, false, false, 0, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Off, true, true, 1, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, true, true, 1, 1)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, true, true, 1, 1)]
    public void Composition_EnforcesEmbeddingModeAndSafePersistenceBeforeConstruction(
        DocsAnalysisEmbeddingMode mode,
        bool cacheSafe,
        bool expectedSuccess,
        int expectedPersistenceConstructions,
        int expectedEmbeddingConstructions)
    {
        WriteConfig(mode);
        var persistence = new DisposablePersistence(isAvailable: true);
        var embedding = new DisposableEmbeddingGenerator();
        var factories = Factories(cacheSafe, persistence, embedding, new AvailableResolver());
        var report = new DiagnosticReport();

        var success = DocsCommandComposition.TryCreateAnalysisRuntime(
            new DocsIntegrityCheckSettings { Path = _temp.Path },
            report,
            factories.Factories,
            out var runtime);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedPersistenceConstructions, factories.PersistenceConstructions);
        Assert.Equal(expectedEmbeddingConstructions, factories.EmbeddingConstructions);
        if (success)
        {
            Assert.NotNull(runtime);
            runtime.Dispose();
        }
        else
        {
            Assert.Null(runtime);
        }

        if (mode == DocsAnalysisEmbeddingMode.Required && !cacheSafe)
        {
            var finding = Assert.Single(report.Items, item =>
                item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
            Assert.Equal(Severity.Error, finding.Severity);
        }
        else if (mode == DocsAnalysisEmbeddingMode.Prefer && !cacheSafe)
        {
            var finding = Assert.Single(report.Items, item =>
                item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
            Assert.Equal(Severity.Warning, finding.Severity);
        }
    }

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, true)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, false)]
    public void Composition_UnavailablePersistenceFallsBackOnlyForPreferAndDisposesPartialRuntime(
        DocsAnalysisEmbeddingMode mode,
        bool expectedSuccess)
    {
        WriteConfig(mode);
        var persistence = new DisposablePersistence(isAvailable: false);
        var embedding = new DisposableEmbeddingGenerator();
        var factories = Factories(cacheSafe: true, persistence, embedding, new AvailableResolver());
        var report = new DiagnosticReport();

        var success = DocsCommandComposition.TryCreateAnalysisRuntime(
            new DocsIntegrityCheckSettings { Path = _temp.Path },
            report,
            factories.Factories,
            out var runtime);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(1, factories.PersistenceConstructions);
        Assert.Equal(0, factories.EmbeddingConstructions);
        Assert.Equal(1, persistence.DisposeCount);
        if (expectedSuccess)
        {
            Assert.NotNull(runtime);
            runtime.Dispose();
        }
        else
        {
            Assert.Null(runtime);
        }
        var finding = Assert.Single(report.Items, item =>
            item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
        Assert.Equal(expectedSuccess ? Severity.Warning : Severity.Error, finding.Severity);
    }

    [Fact]
    public void Composition_SuccessfulRuntimeDisposesEveryConstructedDisposableExactlyOnce()
    {
        WriteConfig(DocsAnalysisEmbeddingMode.Prefer);
        var persistence = new DisposablePersistence(isAvailable: true);
        var embedding = new DisposableEmbeddingGenerator();
        var factories = Factories(cacheSafe: true, persistence, embedding, new AvailableResolver());

        var success = DocsCommandComposition.TryCreateAnalysisRuntime(
            new DocsIntegrityCheckSettings { Path = _temp.Path },
            new DiagnosticReport(),
            factories.Factories,
            out var runtime);
        runtime!.Dispose();
        runtime.Dispose();

        Assert.True(success);
        Assert.Equal(1, persistence.DisposeCount);
        Assert.Equal(1, embedding.DisposeCount);
    }

    [Fact]
    public void Composition_MissingCodeGraphWarnsExactlyOnceAndContinues()
    {
        WriteConfig(DocsAnalysisEmbeddingMode.Off);
        var report = new DiagnosticReport();
        var factories = Factories(
            cacheSafe: false,
            new DisposablePersistence(isAvailable: true),
            new DisposableEmbeddingGenerator(),
            new UnavailableResolver());

        var success = DocsCommandComposition.TryCreateAnalysisRuntime(
            new DocsIntegrityCheckSettings { Path = _temp.Path },
            report,
            factories.Factories,
            out var runtime);

        Assert.True(success);
        runtime!.Dispose();
        var warning = Assert.Single(report.Items, item =>
            item.Code == DocumentationAnalyzer.CodeGraphUnavailableRuleCode);
        Assert.Equal(Severity.Warning, warning.Severity);
    }

    [Fact]
    public void Composition_UnsafeOrdinaryAnalysisCreatesNoCacheOrTrackedFiles()
    {
        WriteConfig(DocsAnalysisEmbeddingMode.Off);
        var before = Files();
        var factories = Factories(
            cacheSafe: false,
            new DisposablePersistence(isAvailable: true),
            new DisposableEmbeddingGenerator(),
            new AvailableResolver());

        var success = DocsCommandComposition.TryCreateAnalysisRuntime(
            new DocsIntegrityCheckSettings { Path = _temp.Path },
            new DiagnosticReport(),
            factories.Factories,
            out var runtime);
        runtime!.Dispose();

        Assert.True(success);
        Assert.Equal(before, Files());
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, ".kyber-weave", "cache")));
        Assert.Equal(0, factories.PersistenceConstructions);
        Assert.Equal(0, factories.EmbeddingConstructions);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("glossary")]
    public void RepositoryService_RequiredEmbeddingFailureStopsEveryRequestedWrite(string operation)
    {
        WriteAnalysisFixture(DocsAnalysisEmbeddingMode.Required);
        var persistence = new DisposablePersistence(isAvailable: true);
        var factories = Factories(
            cacheSafe: true,
            persistence,
            new DisposableEmbeddingGenerator(),
            new AvailableResolver());
        var service = new RepositoryDocsAnalysisCommandService(factories.Factories);
        var output = Path.Combine(_temp.Path, "candidates.json");
        var input = Path.Combine(_temp.Path, "verdicts.json");
        File.WriteAllText(input, "{}");

        var exitCode = ProcessConsoleCapture.Run(() => operation switch
        {
            "export" => new DocsReviewExportCommand(service).Execute(
                null!,
                new DocsReviewExportSettings { Path = _temp.Path, OutputPath = output, Format = "json" }),
            "import" => new DocsReviewImportCommand(service).Execute(
                null!,
                new DocsReviewImportSettings { Path = _temp.Path, InputPath = input, Format = "json" }),
            "glossary" => new DocsGlossaryCommand(service).Execute(
                null!,
                new DocsGlossarySettings { Path = _temp.Path, Write = true, Format = "json" }),
            _ => throw new InvalidOperationException("Unknown operation.")
        }).Result;

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "docs", "glossary.md")));
        Assert.Equal(0, persistence.SavedVerdictCount);
    }

    [Fact]
    public void RepositoryService_PreferAndCodeGraphWarningsFlowOnceThroughEveryResult()
    {
        WriteAnalysisFixture(DocsAnalysisEmbeddingMode.Prefer);
        var persistence = new DisposablePersistence(isAvailable: true);
        var factories = Factories(
            cacheSafe: true,
            persistence,
            new DisposableEmbeddingGenerator(),
            new UnavailableResolver());
        var service = new RepositoryDocsAnalysisCommandService(factories.Factories);

        var exported = service.ExportReview(new DocsReviewExportSettings { Path = _temp.Path });
        var imported = service.ImportReview(new DocsReviewImportSettings { Path = _temp.Path }, "{}");
        var glossary = service.UpdateGlossary(new DocsGlossarySettings { Path = _temp.Path });

        AssertWarningsOnce(exported.Diagnostics);
        AssertWarningsOnce(imported.Diagnostics);
        AssertWarningsOnce(glossary.Diagnostics);
    }

    private sealed class FactoryProbe
    {
        public FactoryProbe(
            bool cacheSafe,
            DisposablePersistence persistence,
            DisposableEmbeddingGenerator embedding,
            ICodeGraphResolver resolver)
        {
            Factories = new DocsAnalysisCompositionFactories
            {
                IsCacheSafe = _ => cacheSafe,
                CreatePersistence = _ =>
                {
                    PersistenceConstructions++;
                    return persistence;
                },
                CreateEmbeddingGenerator = () =>
                {
                    EmbeddingConstructions++;
                    return embedding;
                },
                CreateResolver = _ => resolver
            };
        }

        public DocsAnalysisCompositionFactories Factories { get; }
        public int PersistenceConstructions { get; private set; }
        public int EmbeddingConstructions { get; private set; }
    }

    private void WriteConfig(DocsAnalysisEmbeddingMode mode)
    {
        var state = Path.Combine(_temp.Path, ".kyber-weave");
        Directory.CreateDirectory(state);
        File.WriteAllText(
            Path.Combine(state, "kyber-weave.yml"),
            $$"""
            ontology:
              docs-root: docs
              excluded-files: []
            docs-analysis:
              embeddings:
                mode: {{mode.ToString().ToLowerInvariant()}}
                endpoint: http://127.0.0.1:1234/v1/embeddings
                model: local-test-model
            """);
    }

    private void WriteAnalysisFixture(DocsAnalysisEmbeddingMode mode)
    {
        WriteConfig(mode);
        WriteDocument("gameplay", "The gameplay loop measures live-test runtime.", "Gameplay");
        WriteDocument("automation", "The Codex loop consumes model tokens.", "Automation");
    }

    private void WriteDocument(string id, string claim, string component)
    {
        var docs = Path.Combine(_temp.Path, "docs");
        Directory.CreateDirectory(docs);
        File.WriteAllText(
            Path.Combine(docs, $"{id}.md"),
            $$"""
            ---
            id: reference/{{id}}
            title: {{id}}
            doc-type: reference
            status: current
            owner: Maintainers
            last-reviewed: 2026-08-12
            component: {{component}}
            ---

            # {{id}}

            ## Behavior

            {{claim}}
            """);
    }

    private static void AssertWarningsOnce(DiagnosticReport report)
    {
        Assert.Single(report.Items, item =>
            item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
        Assert.Single(report.Items, item =>
            item.Code == DocumentationAnalyzer.CodeGraphUnavailableRuleCode);
    }

    private static FactoryProbe Factories(
        bool cacheSafe,
        DisposablePersistence persistence,
        DisposableEmbeddingGenerator embedding,
        ICodeGraphResolver resolver) => new(cacheSafe, persistence, embedding, resolver);

    private string[] Files() => Directory
        .GetFiles(_temp.Path, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(_temp.Path, path).Replace('\\', '/'))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private sealed class DisposablePersistence(bool isAvailable) : IAnalysisPersistence, IDisposable
    {
        public bool IsAvailable { get; } = isAvailable;
        public int DisposeCount { get; private set; }
        public int SavedVerdictCount { get; private set; }

        public IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
            IReadOnlyCollection<string> candidateIds) =>
            new Dictionary<string, AnalysisVerdict>(StringComparer.Ordinal);

        public IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
            IReadOnlyCollection<EmbeddingCacheKey> keys) =>
            new Dictionary<EmbeddingCacheKey, StoredEmbedding>();

        public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddings)
        {
        }

        public void SaveVerdicts(IReadOnlyCollection<AnalysisVerdict> verdicts) =>
            SavedVerdictCount += verdicts.Count;

        public void Dispose() => DisposeCount++;
    }

    private sealed class DisposableEmbeddingGenerator : IEmbeddingGenerator, IDisposable
    {
        public int DisposeCount { get; private set; }

        public string GetProviderFingerprint(DocsAnalysisEmbeddingConfig config) => "fake-provider";

        public EmbeddingGenerationResult Generate(
            IReadOnlyCollection<EmbeddingCacheKey> keys,
            IReadOnlyCollection<string> inputs,
            DocsAnalysisEmbeddingConfig config) =>
            new([], EmbeddingUsage.None);

        public void Dispose() => DisposeCount++;
    }

    private sealed class AvailableResolver : EmptyResolver
    {
        public override bool IsAvailable => true;
        public override string? UnavailableReason => null;
    }

    private sealed class UnavailableResolver : EmptyResolver
    {
        public override bool IsAvailable => false;
        public override string? UnavailableReason => "No CodeGraph index.";
    }

    private abstract class EmptyResolver : ICodeGraphResolver
    {
        public abstract bool IsAvailable { get; }
        public abstract string? UnavailableReason { get; }
        public string DatabasePath => ".codegraph/codegraph.db";
        public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name) => [];
        public IReadOnlyList<CodeGraphNode> ResolveRoute(string route) => [];
        public bool HasFilesUnder(string relativePathPrefix) => false;
        public IReadOnlyList<string> CandidateNames(string like) => [];
        public IReadOnlyList<string> AllRoutes() => [];
    }
}
