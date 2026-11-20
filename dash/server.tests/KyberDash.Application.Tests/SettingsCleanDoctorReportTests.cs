using System.Globalization;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Validated partial settings updates: every field shape, bound, and rejection case, all over a
/// mocked settings repository.
/// </summary>
public class SettingsServiceTests
{
    [Fact]
    public void GetSettingsReadsStraightThroughTheRepository()
    {
        SettingsView stored = new(
            FolderImportScheduled: true,
            JobsPaused: false,
            RefreshCadenceMinutes: 15,
            ReceiverHosted: true,
            DashboardPort: 5000);
        Mock<ISettingsRepository> repository = new();
        repository.Setup(settings => settings.ReadSettings()).Returns(stored);

        SettingsView view = new SettingsService(repository.Object).GetSettings();

        Assert.Equal(stored, view);
        repository.Verify(settings => settings.ReadSettings(), Times.Once);
    }

    [Fact]
    public void BooleanPatchWritesTheRecognizedKeysAndLeavesTheRestAlone()
    {
        RecordingSettingsRepository repository = new();
        SettingsService service = new(repository);

        SettingsView updated = service.UpdateSettings(Parse("{\"folderImportScheduled\":true,\"jobsPaused\":true}"));

        Assert.True(updated.FolderImportScheduled);
        Assert.True(updated.JobsPaused);
        Assert.Equal(["on"], repository.Metadata.Values.Distinct(StringComparer.Ordinal).ToList());
        Assert.Equal(2, repository.Metadata.Count);
    }

    [Fact]
    public void IntegerPatchAcceptsInclusiveBounds()
    {
        RecordingSettingsRepository repository = new();
        SettingsService service = new(repository);

        SettingsView lowest = service.UpdateSettings(Parse("{\"refreshCadenceMinutes\":1,\"dashboardPort\":1}"));
        Assert.Equal(1, lowest.RefreshCadenceMinutes);
        Assert.Equal(1, lowest.DashboardPort);

        SettingsView highest = service.UpdateSettings(Parse("{\"refreshCadenceMinutes\":1440,\"dashboardPort\":65535}"));
        Assert.Equal(1440, highest.RefreshCadenceMinutes);
        Assert.Equal(65535, highest.DashboardPort);
    }

    [Theory]
    [InlineData("[]", "Settings patch must be a JSON object")]
    [InlineData("{\"refreshCadenceMinutes\":0}", "between 1 and 1440")]
    [InlineData("{\"refreshCadenceMinutes\":1441}", "between 1 and 1440")]
    [InlineData("{\"refreshCadenceMinutes\":1.5}", "between 1 and 1440")]
    [InlineData("{\"refreshCadenceMinutes\":\"5\"}", "between 1 and 1440")]
    [InlineData("{\"dashboardPort\":0}", "between 1 and 65535")]
    [InlineData("{\"dashboardPort\":65536}", "between 1 and 65535")]
    [InlineData("{\"folderImportScheduled\":\"yes\"}", "must be a boolean")]
    [InlineData("{\"jobsPaused\":1}", "must be a boolean")]
    [InlineData("{\"receiverHosted\":null}", "must be a boolean")]
    [InlineData("{\"unknownSetting\":true}", "Unknown setting")]
    public void InvalidPatchIsRejectedAsAValidationFailure(string patch, string expectedFragment)
    {
        RecordingSettingsRepository repository = new();
        SettingsService service = new(repository);

        DashValidationException exception = Assert.Throws<DashValidationException>(() => service.UpdateSettings(Parse(patch)));

        Assert.Equal(SettingsService.ValidationErrorCode, exception.Code);
        Assert.Contains(expectedFragment, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchWithOneBadValueWritesNothing()
    {
        RecordingSettingsRepository repository = new();
        SettingsService service = new(repository);

        Assert.Throws<DashValidationException>(
            () => service.UpdateSettings(Parse("{\"jobsPaused\":true,\"refreshCadenceMinutes\":0}")));

        Assert.Empty(repository.Metadata);
        Assert.DoesNotContain(repository.Writes, write => write.Contains("jobsPaused", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstructorRejectsANullRepository()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsService(null!));
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Settings repository that records every write it receives.</summary>
    private class RecordingSettingsRepository : ISettingsRepository
    {
        private readonly FakeSettingsRepository inner = new();

        public IReadOnlyDictionary<string, string> Metadata => inner.Metadata;

        public List<string> Writes { get; } = [];

        public SettingsView ReadSettings() => inner.ReadSettings();

        public void WriteBooleanSetting(string key, bool value)
        {
            Writes.Add(string.Create(CultureInfo.InvariantCulture, $"{key}={value}"));
            inner.WriteBooleanSetting(key, value);
        }

        public void WriteIntSetting(string key, int value)
        {
            Writes.Add(string.Create(CultureInfo.InvariantCulture, $"{key}={value}"));
            inner.WriteIntSetting(key, value);
        }

        public bool ReadFactoryContentConsent(string stateDir) => inner.ReadFactoryContentConsent(stateDir);

        public void WriteFactoryContentConsent(string stateDir, bool enabled) => inner.WriteFactoryContentConsent(stateDir, enabled);

        public void AppendAntigravityStatusLineEvent(string stateDir, JsonElement payload) =>
            inner.AppendAntigravityStatusLineEvent(stateDir, payload);
    }
}

/// <summary>
/// Clean lifecycle: ingestion is paused before the wipe and resumed afterwards, even when the wipe
/// throws, and nothing is deleted until the scope has been validated.
/// </summary>
public class CleanServiceTests
{
    [Fact]
    public void AllScopeWipesEverythingAndReprojects()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICanonProjectionService> projection = new();
        Mock<ITranscriptIngestionService> ingestion = new();
        StoreWipeReport wipe = new(1, 2, 3, 4, 5, 6, 7, 8, 9, "2026-11-15T00:00:00.000Z");
        store.Setup(repository => repository.WipeAll()).Returns(wipe);
        projection.Setup(service => service.ProjectIncremental()).Returns(EmptyProjection);
        projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);

        List<string> lifecycle = [];
        CleanReport report = new CleanService(store.Object, projection.Object, ingestion.Object).CleanDatabase(
            new CleanScope(All: true),
            pauseIngestion: () => lifecycle.Add("pause"),
            resumeIngestion: () => lifecycle.Add("resume"));

        Assert.Equal(["pause", "resume"], lifecycle);
        Assert.Equal([CleanService.AllHarnessesMarker], report.Harnesses);
        Assert.Equal(wipe, report.Wipe);
        Assert.False(report.Reingested);
        Assert.Null(report.HistoryWeeks);
        store.Verify(repository => repository.WipeAll(), Times.Once);
        store.Verify(repository => repository.WipeHarnesses(It.IsAny<IEnumerable<string>>()), Times.Never);
        projection.Verify(service => service.ProjectIncremental(), Times.Once);
        projection.Verify(service => service.ProjectRunsAndHarnesses(), Times.Once);
        ingestion.Verify(service => service.RefreshHarnessSources(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void HarnessScopeWipesOnlyTheNormalizedSelection()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICanonProjectionService> projection = new();
        Mock<ITranscriptIngestionService> ingestion = new();
        IReadOnlyList<string>? wiped = null;
        store
            .Setup(repository => repository.WipeHarnesses(It.IsAny<IEnumerable<string>>()))
            .Callback<IEnumerable<string>>(harnesses => wiped = harnesses.ToList())
            .Returns(new StoreWipeReport(0, 0, 0, 0, 0, 0, 0, 0, 0, "2026-11-15T00:00:00.000Z"));
        projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);

        CleanReport report = new CleanService(store.Object, projection.Object, ingestion.Object).CleanDatabase(
            new CleanScope(Harnesses: ["  claude-code ", "not-a-harness"]));

        Assert.Equal(["claude-code"], wiped);
        Assert.Equal(["claude-code"], report.Harnesses);
        store.Verify(repository => repository.WipeAll(), Times.Never);
    }

    [Fact]
    public void IngestionAlwaysResumesEvenWhenTheWipeThrows()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICanonProjectionService> projection = new();
        Mock<ITranscriptIngestionService> ingestion = new();
        store.Setup(repository => repository.WipeAll()).Throws(new InvalidOperationException("disk full"));

        List<string> lifecycle = [];
        Assert.Throws<InvalidOperationException>(() => new CleanService(store.Object, projection.Object, ingestion.Object).CleanDatabase(
            new CleanScope(All: true),
            pauseIngestion: () => lifecycle.Add("pause"),
            resumeIngestion: () => lifecycle.Add("resume")));

        Assert.Equal(["pause", "resume"], lifecycle);
        projection.Verify(service => service.ProjectIncremental(), Times.Never);
    }

    [Fact]
    public void ReingestUsesTheCallerCallbackWhenSupplied()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICanonProjectionService> projection = new();
        Mock<ITranscriptIngestionService> ingestion = new();
        store.Setup(repository => repository.WipeHarnesses(It.IsAny<IEnumerable<string>>()))
            .Returns(new StoreWipeReport(0, 0, 0, 0, 0, 0, 0, 0, 0, "2026-11-15T00:00:00.000Z"));
        projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);

        IReadOnlyList<string>? reingestedHarnesses = null;
        int reingestedWeeks = 0;
        CleanReport report = new CleanService(store.Object, projection.Object, ingestion.Object).CleanDatabase(
            new CleanScope(Harnesses: ["codex"], ReingestWeeks: 4),
            reingest: (harnesses, weeks) =>
            {
                reingestedHarnesses = harnesses;
                reingestedWeeks = weeks;
                return 0;
            });

        Assert.True(report.Reingested);
        Assert.Equal(4, report.HistoryWeeks);
        Assert.Equal(["codex"], reingestedHarnesses);
        Assert.Equal(4, reingestedWeeks);
        ingestion.Verify(service => service.RefreshHarnessSources(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void ReingestFallsBackToTheIngestionService()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICanonProjectionService> projection = new();
        Mock<ITranscriptIngestionService> ingestion = new();
        store.Setup(repository => repository.WipeAll())
            .Returns(new StoreWipeReport(0, 0, 0, 0, 0, 0, 0, 0, 0, "2026-11-15T00:00:00.000Z"));
        projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);
        ingestion.Setup(service => service.RefreshHarnessSources(2)).Returns(EmptyRefreshReport);

        CleanReport report = new CleanService(store.Object, projection.Object, ingestion.Object).CleanDatabase(
            new CleanScope(All: true, ReingestWeeks: 2));

        Assert.True(report.Reingested);
        ingestion.Verify(service => service.RefreshHarnessSources(2), Times.Once);
    }

    [Theory]
    [InlineData("{\"all\":true,\"harnesses\":[\"codex\"]}")]
    [InlineData("{\"all\":false,\"harnesses\":[]}")]
    [InlineData("{\"all\":false,\"harnesses\":[\"unknown-harness\"]}")]
    public void InvalidScopesAreRefusedBeforeAnythingIsDeleted(string scopeJson)
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICanonProjectionService> projection = new();
        Mock<ITranscriptIngestionService> ingestion = new();

        bool paused = false;
        using JsonDocument document = JsonDocument.Parse(scopeJson);
        CleanScope scope = new(
            All: document.RootElement.GetProperty("all").GetBoolean(),
            Harnesses: [.. document.RootElement.GetProperty("harnesses").EnumerateArray().Select(item => item.GetString()!)]);

        Assert.Throws<ArgumentException>(() => new CleanService(store.Object, projection.Object, ingestion.Object).CleanDatabase(
            scope,
            pauseIngestion: () => paused = true));

        Assert.False(paused);
        store.Verify(repository => repository.WipeAll(), Times.Never);
        store.Verify(repository => repository.WipeHarnesses(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public void ReingestWeeksOutsideOneTo104IsRefused()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICanonProjectionService> projection = new();
        Mock<ITranscriptIngestionService> ingestion = new();

        Assert.Throws<ArgumentException>(() => new CleanService(store.Object, projection.Object, ingestion.Object)
            .CleanDatabase(new CleanScope(All: true, ReingestWeeks: 105)));
        Assert.Throws<ArgumentException>(() => new CleanService(store.Object, projection.Object, ingestion.Object)
            .CleanDatabase(new CleanScope(All: true, ReingestWeeks: 0)));
    }

    [Fact]
    public void ValidateScopeNormalizesDeduplicatesAndOrdersHarnesses()
    {
        IReadOnlyList<string>? scope = CleanService.ValidateScope(
            new CleanScope(Harnesses: ["copilot-vscode", "copilot-chat", "codex"]));

        Assert.Equal(["codex", "copilot-vscode"], scope);
        Assert.Null(CleanService.ValidateScope(new CleanScope(All: true)));
    }

    private static ProjectionPassReport EmptyProjection => new(0, 0, 0, 0, [], []);

    private static RefreshReport EmptyRefreshReport => new(
        RunId: "refresh-test",
        HistoryWeeks: 2,
        CommandStartedAt: "2026-11-15T00:00:00.000Z",
        Rows: [],
        PurgedContentCount: 0,
        Sessions: 0,
        Runs: 0,
        Executions: 0,
        Rollups: 0,
        ExitCode: 0);
}

/// <summary>Doctor diagnostics report the store figures and the breaker state verbatim.</summary>
public class DoctorServiceTests
{
    [Theory]
    [InlineData(DashCircuitState.Closed, "closed")]
    [InlineData(DashCircuitState.Open, "open")]
    [InlineData(DashCircuitState.HalfOpen, "half-open")]
    [InlineData(DashCircuitState.Isolated, "isolated")]
    public void DiagnosticsSurfaceTheCircuitBreakerStateAccurately(DashCircuitState state, string expected)
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICircuitBreaker> breaker = new();
        store.Setup(repository => repository.DatabasePath).Returns("/tmp/canon.db");
        store.Setup(repository => repository.CountRecords()).Returns(42);
        store.Setup(repository => repository.ListSessions()).Returns([SampleSession("s1"), SampleSession("s2")]);
        store.Setup(repository => repository.ListObservedHarnesses()).Returns(["claude-code", "codex"]);
        breaker.SetupGet(candidate => candidate.State).Returns(state);

        DoctorDiagnosticsReport report = new DoctorService(store.Object, breaker.Object).RunDiagnostics();

        Assert.Equal("/tmp/canon.db", report.DatabasePath);
        Assert.Equal(42, report.Records);
        Assert.Equal(2, report.Sessions);
        Assert.Equal(["claude-code", "codex"], report.ObservedHarnesses);
        Assert.Equal(expected, report.CircuitBreakerState);
        store.Verify(repository => repository.EnsureInitialized(), Times.Once);
    }

    [Fact]
    public void VacuumDelegatesToTheStore()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICircuitBreaker> breaker = new();

        new DoctorService(store.Object, breaker.Object).RunVacuum();

        store.Verify(repository => repository.Vacuum(), Times.Once);
    }

    [Fact]
    public void ConstructorRejectsNullDependencies()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ICircuitBreaker> breaker = new();

        Assert.Throws<ArgumentNullException>(() => new DoctorService(null!, breaker.Object));
        Assert.Throws<ArgumentNullException>(() => new DoctorService(store.Object, null!));
    }

    private static SessionSummaryRow SampleSession(string sessionId) => new(
        SessionId: sessionId,
        Harness: "claude-code",
        Label: null,
        IsSubagent: false,
        ParentSession: null,
        AgentName: null,
        Repo: null,
        Branch: null,
        Started: "2026-11-14T10:00:00.000Z",
        Ended: "2026-11-14T10:05:00.000Z",
        SummaryJson: null,
        ProblemsCount: null,
        ContextLimitSource: null,
        Payload: JsonSerializer.SerializeToElement(new { }));
}

/// <summary>Report rendering over a mocked query service and store; nothing touches disk.</summary>
public class ReportServiceTests
{
    [Fact]
    public void JsonFormatPrintsTheQueryPayload()
    {
        Harness harness = new();

        (int exitCode, string output) = harness.Reports.GenerateReport("json", harness: "claude-code", limit: 7);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(3, document.RootElement.GetProperty("sessionCount").GetInt32());
        harness.Query.Verify(service => service.GetReport("claude-code", null, null, 7), Times.Once);
    }

    [Fact]
    public void MarkdownRendersHarnessesAndFindings()
    {
        Harness harness = new();

        (int exitCode, string output) = harness.Reports.GenerateReport("markdown", limit: 1);

        Assert.Equal(0, exitCode);
        Assert.Contains("# KyberDash report", output, StringComparison.Ordinal);
        Assert.Contains("| Harness | Sessions |", output, StringComparison.Ordinal);
        Assert.Contains("| claude-code | 4 |", output, StringComparison.Ordinal);
        Assert.Contains("absent", output, StringComparison.Ordinal);
        Assert.Contains("**dormant-tool-schema**", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TextRendersEmptySectionsHonestly()
    {
        Harness harness = new();
        harness.Rollups.Clear();
        harness.Findings.Clear();

        (int exitCode, string output) = harness.Reports.GenerateReport("text");

        Assert.Equal(0, exitCode);
        Assert.Contains("KyberDash report", output, StringComparison.Ordinal);
        Assert.Contains("(none recorded)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFormatExitsTwoWithGuidance()
    {
        Harness harness = new();

        (int exitCode, string output) = harness.Reports.GenerateReport("yaml");

        Assert.Equal(2, exitCode);
        Assert.Contains("json, markdown, text", output, StringComparison.Ordinal);
        harness.Query.Verify(
            service => service.GetReport(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public void LimitIsClampedToOneHundred()
    {
        Harness harness = new();

        _ = harness.Reports.GenerateReport("text", limit: 10_000);

        harness.Query.Verify(service => service.GetReport(null, null, null, 200), Times.Once);
    }

    [Fact]
    public void FormatPercentPrintsWholePercentagesOrAbsent()
    {
        Assert.Equal("42.5%", ReportService.FormatPercent(0.425));
        Assert.Equal(ReportService.AbsentCell, ReportService.FormatPercent(null));
    }

    private class Harness
    {
        public Harness()
        {
            Query.Setup(service => service.GetReport(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .Returns(JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 1,
                    generatedAt = "2026-11-15T00:00:00.000Z",
                    storeGeneration = 7,
                    sessionCount = 3,
                    findingCount = 1,
                    harnessCount = 1,
                }));
            Store.Setup(repository => repository.ListHarnessRollups()).Returns(Rollups);
            Store.Setup(repository => repository.ListFindings(It.IsAny<string?>(), It.IsAny<string?>())).Returns(Findings);
            Reports = new ReportService(Query.Object, Store.Object);
        }

        public Mock<IDashboardQueryService> Query { get; } = new();

        public Mock<ICanonStoreRepository> Store { get; } = new();

        public List<HarnessRollupRow> Rollups { get; } =
        [
            new(
                Harness: "claude-code",
                SampleCount: 4,
                ContextPressureMedian: null,
                ContextPressureP95: null,
                CacheHitRate: null,
                ToolYield: null,
                DelegationOverhead: null,
                FieldCoverage: null,
                Measurability: new Dictionary<string, JsonElement>(StringComparer.Ordinal)),
        ];

        public List<Finding> Findings { get; } =
        [
            new(
                Id: "finding-1",
                DetectorId: "dormant-tool-schema",
                Title: "Tool schema never invoked",
                Mechanism: "mcp",
                EvidenceLinks: [],
                Confidence: "measured",
                EstimatedWasteTokens: 600,
                Recommendation: "Relocate dormant tool schemas to on-demand deferred loading.",
                ErrorBar: null,
                OutcomeRiskCaveat: "No comparable runs."),
        ];

        public ReportService Reports { get; }
    }
}

/// <summary>Analytics operations delegate to the bridge engine, with the engine's collaborators mocked.</summary>
public class AnalyticsServiceTests
{
    [Fact]
    public void EveryAdvertisedOperationReachesTheEngineWithA200()
    {
        Harness harness = new();

        foreach (string operation in AnalyticsService.Operations)
        {
            if (string.Equals(operation, "inspect", StringComparison.Ordinal))
            {
                continue;
            }

            AnalyticsExecutionResult result = harness.Analytics.Execute(operation, null);
            Assert.Equal(200, result.StatusCode);
        }

        Assert.Equal(0, harness.Store.CountRows("record"));
        Assert.Equal(0, harness.Store.CountRows("session"));

        harness.Store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: "sess-inspect",
                    HarnessId: "claude-code",
                    NativeSessionId: "sess-inspect",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records:
            [
                new CollectedRecord(
                    RecordId: "span-inspect-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm.invoke",
                    StartedAt: new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-inspect",
                    ParentRecordId: null,
                    TraceId: null,
                    Model: null,
                    EndedAt: null,
                    ReportedDurationMs: 100,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 10),
                        new CollectedTokenCount(TokenClass.CacheRead, 0),
                        new CollectedTokenCount(TokenClass.CacheWrite, 0),
                        new CollectedTokenCount(TokenClass.Output, 5),
                    ],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
                    ReportedInputTokens: 10,
                    ReportedOutputTokens: 5),
            ],
            Problems: []));
        using JsonDocument inspectBody = JsonDocument.Parse("""{"sessionId":"sess-inspect"}""");
        AnalyticsExecutionResult inspectResult = harness.Analytics.Execute("inspect", inspectBody.RootElement);
        Assert.Equal(200, inspectResult.StatusCode);
    }

    [Fact]
    public void UnknownOperationAnswers400AndNamesIt()
    {
        Harness harness = new();

        AnalyticsExecutionResult result = harness.Analytics.Execute("frobnicate", null);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("frobnicate", result.Envelope.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BlankOperationIsRejected()
    {
        Harness harness = new();

        Assert.Throws<ArgumentException>(() => harness.Analytics.Execute("  ", null));
    }

    [Fact]
    public void IsSupportedIgnoresCaseAndSurroundingSpace()
    {
        Assert.True(AnalyticsService.IsSupported(" Status "));
        Assert.True(AnalyticsService.IsSupported("search-sessions"));
        Assert.False(AnalyticsService.IsSupported("everything"));
        Assert.False(AnalyticsService.IsSupported(null));
    }

    [Fact]
    public void UnsupportedBuildsTheSameEnvelopeTheEngineProduces()
    {
        AnalyticsExecutionResult result = AnalyticsService.Unsupported("nope");

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("nope", result.Envelope.GetProperty("operation").GetString());
        Assert.NotEmpty(result.Envelope.GetProperty("operations").EnumerateArray());
    }

    private class Harness
    {
        public Harness()
        {
            KyberBridgeEngine bridge = new(
                Store,
                Store,
                Store,
                Pricing,
                new CanonSessionProjectorEngine(Store, Store, Store, Pricing),
                new CanonRollupsEngine(Store, Store, Pricing));
            Analytics = new AnalyticsService(bridge);
        }

        public InMemoryRelationalStore Store { get; } = new();

        public FakePricingRepository Pricing { get; } = new();

        public AnalyticsService Analytics { get; }
    }
}
