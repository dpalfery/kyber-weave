using System.Diagnostics.CodeAnalysis;

using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Domain.CodeGraph;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Cli.Tests.Commands.Docs;

/// <summary>
/// Specifies the CLI's adapter lifetime and cost boundary. Unsafe persistence must prevent
/// both cache writes and embedding construction, and every constructed disposable is owned
/// by the command runtime.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Tests intentionally transfer fake adapter ownership to the runtime and assert disposal.")]
public sealed class DocsAnalysisCompositionTests
{
    private readonly MemoryDocsHost _host = new();

    private IKyberWeaveConfigRepository Configs =>
        _host.Services().GetRequiredService<IKyberWeaveConfigRepository>();

    /// <summary>The resolver a docs command gets is the one the injected ports factory answers.</summary>
    [Fact]
    public async Task TheResolverComesFromTheInjectedPortsFactory()
    {
        FakeCodeGraphResolver resolver = FakeCodeGraphResolver.WithNodes();
        FakeCodeGraphPortsFactory codeGraph = new(resolver);

        ICodeGraphResolver actual = await DocsCommandComposition.CreateResolverAsync(
            new DocsSettings { Path = "/some/repo" },
            codeGraph,
            CancellationToken.None);

        Assert.Same(resolver, actual);
        Assert.Equal(["/some/repo"], codeGraph.Roots);
    }

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Off, false, true, 0, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, false, true, 0, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, false, false, 0, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Off, true, true, 1, 0)]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, true, true, 1, 1)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, true, true, 1, 1)]
    public async Task CompositionEnforcesEmbeddingModeAndSafePersistenceBeforeConstruction(
        DocsAnalysisEmbeddingMode mode,
        bool cacheSafe,
        bool expectedSuccess,
        int expectedPersistenceConstructions,
        int expectedEmbeddingConstructions)
    {
        WriteConfig(mode);
        DisposablePersistence persistence = new DisposablePersistence(isAvailable: true);
        DisposableEmbeddingGenerator embedding = new DisposableEmbeddingGenerator();
        FactoryProbe factories = Factories(cacheSafe, persistence, embedding, new AvailableResolver());
        DiagnosticReport report = new DiagnosticReport();

        DocsAnalysisRuntime? runtime = await DocsCommandComposition.TryCreateAnalysisRuntimeAsync(
            Configs,
            new DocsIntegrityCheckSettings { Path = _host.Repo },
            report,
            factories.Factories,
            CancellationToken.None);

        bool success = runtime is not null;
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
            Diagnostic finding = Assert.Single(report.Items, item =>
                item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
            Assert.Equal(Severity.Error, finding.Severity);
        }
        else if (mode == DocsAnalysisEmbeddingMode.Prefer && !cacheSafe)
        {
            Diagnostic finding = Assert.Single(report.Items, item =>
                item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
            Assert.Equal(Severity.Warning, finding.Severity);
        }
    }

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, true)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, false)]
    public async Task CompositionUnavailablePersistenceFallsBackOnlyForPreferAndDisposesPartialRuntime(
        DocsAnalysisEmbeddingMode mode,
        bool expectedSuccess)
    {
        WriteConfig(mode);
        DisposablePersistence persistence = new DisposablePersistence(isAvailable: false);
        DisposableEmbeddingGenerator embedding = new DisposableEmbeddingGenerator();
        FactoryProbe factories = Factories(cacheSafe: true, persistence, embedding, new AvailableResolver());
        DiagnosticReport report = new DiagnosticReport();

        DocsAnalysisRuntime? runtime = await DocsCommandComposition.TryCreateAnalysisRuntimeAsync(
            Configs,
            new DocsIntegrityCheckSettings { Path = _host.Repo },
            report,
            factories.Factories,
            CancellationToken.None);

        bool success = runtime is not null;
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
        Diagnostic finding = Assert.Single(report.Items, item =>
            item.Code == DocumentationAnalyzer.EmbeddingUnavailableRuleCode);
        Assert.Equal(expectedSuccess ? Severity.Warning : Severity.Error, finding.Severity);
    }

    [Fact]
    public async Task CompositionSuccessfulRuntimeDisposesEveryConstructedDisposableExactlyOnce()
    {
        WriteConfig(DocsAnalysisEmbeddingMode.Prefer);
        DisposablePersistence persistence = new DisposablePersistence(isAvailable: true);
        DisposableEmbeddingGenerator embedding = new DisposableEmbeddingGenerator();
        FactoryProbe factories = Factories(cacheSafe: true, persistence, embedding, new AvailableResolver());

        DocsAnalysisRuntime? runtime = await DocsCommandComposition.TryCreateAnalysisRuntimeAsync(
            Configs,
            new DocsIntegrityCheckSettings { Path = _host.Repo },
            new DiagnosticReport(),
            factories.Factories,
            CancellationToken.None);

        Assert.NotNull(runtime);
        runtime.Dispose();
        runtime.Dispose();

        Assert.Equal(1, persistence.DisposeCount);
        Assert.Equal(1, embedding.DisposeCount);
    }

    [Fact]
    public async Task CompositionMissingCodeGraphWarnsExactlyOnceAndContinues()
    {
        WriteConfig(DocsAnalysisEmbeddingMode.Off);
        DiagnosticReport report = new DiagnosticReport();
        FactoryProbe factories = Factories(
            cacheSafe: false,
            new DisposablePersistence(isAvailable: true),
            new DisposableEmbeddingGenerator(),
            new UnavailableResolver());

        DocsAnalysisRuntime? runtime = await DocsCommandComposition.TryCreateAnalysisRuntimeAsync(
            Configs,
            new DocsIntegrityCheckSettings { Path = _host.Repo },
            report,
            factories.Factories,
            CancellationToken.None);

        Assert.NotNull(runtime);
        runtime.Dispose();
        Diagnostic warning = Assert.Single(report.Items, item =>
            item.Code == DocumentationAnalyzer.CodeGraphUnavailableRuleCode);
        Assert.Equal(Severity.Warning, warning.Severity);
    }

    [Fact]
    public async Task CompositionUnsafeOrdinaryAnalysisCreatesNoCacheOrTrackedFiles()
    {
        WriteConfig(DocsAnalysisEmbeddingMode.Off);
        string[] before = Files();
        FactoryProbe factories = Factories(
            cacheSafe: false,
            new DisposablePersistence(isAvailable: true),
            new DisposableEmbeddingGenerator(),
            new AvailableResolver());

        DocsAnalysisRuntime? runtime = await DocsCommandComposition.TryCreateAnalysisRuntimeAsync(
            Configs,
            new DocsIntegrityCheckSettings { Path = _host.Repo },
            new DiagnosticReport(),
            factories.Factories,
            CancellationToken.None);

        Assert.NotNull(runtime);
        runtime.Dispose();

        Assert.Equal(before, Files());
        Assert.False(_host.Files.DirectoryExists(Path.Combine(_host.Repo, ".kyber-weave", "cache")));
        Assert.Equal(0, factories.PersistenceConstructions);
        Assert.Equal(0, factories.EmbeddingConstructions);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("glossary")]
    public async Task RepositoryServiceRequiredEmbeddingFailureStopsEveryRequestedWrite(string operation)
    {
        WriteAnalysisFixture(DocsAnalysisEmbeddingMode.Required);
        DisposablePersistence persistence = new DisposablePersistence(isAvailable: true);
        FactoryProbe factories = Factories(
            cacheSafe: true,
            persistence,
            new DisposableEmbeddingGenerator(),
            new AvailableResolver());
        RepositoryDocsAnalysisCommandService service = AnalysisService(factories.Factories);
        string output = Path.Combine(_host.Repo, "candidates.json");
        string input = Path.Combine(_host.Repo, "verdicts.json");
        _host.Files.WriteAllText(input, "{}");

        int exitCode = await (operation switch
        {
            "export" => new DocsReviewExportCommand(service, _host.Files, _host.Console).ExecuteAsync(
                null!,
                new DocsReviewExportSettings { Path = _host.Repo, OutputPath = output, Format = "json" },
                CancellationToken.None),
            "import" => new DocsReviewImportCommand(service, _host.Files, _host.Console).ExecuteAsync(
                null!,
                new DocsReviewImportSettings { Path = _host.Repo, InputPath = input, Format = "json" },
                CancellationToken.None),
            "glossary" => new DocsGlossaryCommand(service, _host.Console).ExecuteAsync(
                null!,
                new DocsGlossarySettings { Path = _host.Repo, Write = true, Format = "json" },
                CancellationToken.None),
            _ => throw new InvalidOperationException("Unknown operation.")
        });

        Assert.Equal(1, exitCode);
        Assert.False(_host.Files.FileExists(output));
        Assert.False(_host.Files.FileExists(Path.Combine(_host.Repo, "docs", "glossary.md")));
        Assert.Equal(0, persistence.SavedVerdictCount);
    }

    [Fact]
    public async Task RepositoryServicePreferAndCodeGraphWarningsFlowOnceThroughEveryResult()
    {
        WriteAnalysisFixture(DocsAnalysisEmbeddingMode.Prefer);
        DisposablePersistence persistence = new DisposablePersistence(isAvailable: true);
        FactoryProbe factories = Factories(
            cacheSafe: true,
            persistence,
            new DisposableEmbeddingGenerator(),
            new UnavailableResolver());
        RepositoryDocsAnalysisCommandService service = AnalysisService(factories.Factories);

        ReviewExportResult exported = await service.ExportReviewAsync(new DocsReviewExportSettings { Path = _host.Repo });
        ReviewImportResult imported = await service.ImportReviewAsync(new DocsReviewImportSettings { Path = _host.Repo }, "{}");
        GlossaryUpdateResult glossary = await service.UpdateGlossaryAsync(new DocsGlossarySettings { Path = _host.Repo });

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
            Factories = new DocsAnalysisCompositionFactories(new FakeCodeGraphPortsFactory(resolver), new UnreachedAnalysisPersistenceFactory(), new UnreachedEmbeddingGeneratorFactory())
            {
                IsCacheSafeAsync = (_, _) => Task.FromResult(cacheSafe),
                CreatePersistenceAsync = (_, _) =>
                {
                    PersistenceConstructions++;
                    return Task.FromResult<IAnalysisPersistence>(persistence);
                },
                CreateEmbeddingGenerator = () =>
                {
                    EmbeddingConstructions++;
                    return embedding;
                },
                CreateResolverAsync = (_, _) => Task.FromResult(resolver)
            };
        }

        public DocsAnalysisCompositionFactories Factories { get; }
        public int PersistenceConstructions { get; private set; }
        public int EmbeddingConstructions { get; private set; }
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "The YAML enum vocabulary this fixture writes is lowercase; an "
            + "uppercased value would not round-trip through the config loader.")]
    private void WriteConfig(DocsAnalysisEmbeddingMode mode)
    {
        string state = Path.Combine(_host.Repo, ".kyber-weave");
        _host.Files.CreateDirectory(state);
        _host.Files.WriteAllText(
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
        string docs = Path.Combine(_host.Repo, "docs");
        _host.Files.CreateDirectory(docs);
        _host.Files.WriteAllText(
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

    private RepositoryDocsAnalysisCommandService AnalysisService(DocsAnalysisCompositionFactories factories)
    {
        IServiceProvider provider = _host.Services();
        return new RepositoryDocsAnalysisCommandService(
            factories,
            provider.GetRequiredService<IManagedGlossaryFactory>(),
            provider.GetRequiredService<IDocumentSourceFactory>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    private string[] Files() => _host.Files
        .EnumerateFiles(_host.Repo, "*", recursive: true)
        .Select(path => Path.GetRelativePath(_host.Repo, path).Replace('\\', '/'))
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

        public Task<EmbeddingGenerationResult> GenerateAsync(
            IReadOnlyCollection<EmbeddingCacheKey> keys,
            IReadOnlyCollection<string> inputs,
            DocsAnalysisEmbeddingConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingGenerationResult([], EmbeddingUsage.None));

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
        public override string UnavailableReason => "No CodeGraph index.";
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
