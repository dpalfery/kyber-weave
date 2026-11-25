using System.Globalization;
using System.Reflection;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Settings, clean, doctor, report, job-scheduler, and composition tests for the Application
/// services over the relational store contracts (<see cref="InMemoryRelationalStore"/> for unit
/// scope and <see cref="SqliteRelationalStore"/> where only the real store answers).
/// </summary>
public static class SettingsCleanDoctorReportTests
{
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
    /// Clean lifecycle over the relational store: ingestion is paused before the wipe and resumed
    /// afterwards even when the wipe throws, nothing is deleted until the scope has been validated,
    /// and the BL-3 pruned identities (<c>aider</c>, <c>windsurf</c>) are refused as a scope.
    /// </summary>
    public class CleanServiceTests
    {
        [Fact]
        public void AllScopeWipesCollectedDataAndReportsEveryDeletedFigure()
        {
            InMemoryRelationalStore store = SeedTwoHarnesses();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            projection.Setup(service => service.ProjectIncremental()).Returns(EmptyProjection);
            projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);

            List<string> lifecycle = [];
            long markerBeforeClean = store.GetWriteMarker();
            CleanReport report = new CleanService(store, store, store, store, projection.Object, ingestion.Object).CleanDatabase(
                new CleanScope(All: true),
                pauseIngestion: () => lifecycle.Add("pause"),
                resumeIngestion: () => lifecycle.Add("resume"));

            Assert.Equal(["pause", "resume"], lifecycle);
            Assert.Equal([CleanService.AllHarnessesMarker], report.Harnesses);
            Assert.False(report.Reingested);
            Assert.Null(report.HistoryWeeks);
            Assert.Equal(3, report.Wipe.RecordsDeleted);
            Assert.Equal(0, report.Wipe.SessionsDeleted);
            Assert.Equal(2, report.Wipe.TurnsDeleted);
            Assert.Equal(2, report.Wipe.RunsDeleted);
            Assert.Equal(2, report.Wipe.ExecutionsDeleted);
            Assert.Equal(2, report.Wipe.FindingsDeleted);
            Assert.Equal(2, report.Wipe.RollupsDeleted);
            Assert.Equal(0, report.Wipe.ProblemsDeleted);
            Assert.Equal(0, report.Wipe.QuarantineDeleted);
            Assert.False(string.IsNullOrWhiteSpace(report.Wipe.LastCleanAt));

            Assert.Equal(0, store.CountRecords());
            Assert.Empty(store.Sources());
            Assert.Empty(store.ListSessionProjections());
            Assert.Empty(store.ListRuns());
            Assert.Empty(store.ListHarnessRollups());
            Assert.Empty(store.ListFindings());
            Assert.Equal(2, store.Sessions().Count);
            Assert.True(store.GetWriteMarker() > markerBeforeClean);
            Assert.Null(store.GetProjectionDirtyState());
            Assert.Equal(report.Wipe.LastCleanAt, store.GetLastCleanAtUtc());
            projection.Verify(service => service.ProjectIncremental(), Times.Once);
            projection.Verify(service => service.ProjectRunsAndHarnesses(), Times.Once);
            ingestion.Verify(service => service.RefreshHarnessSources(It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public void CleanRetainsIngestProblemsAndQuarantinedSpansAsDiagnostics()
        {
            InMemoryRelationalStore store = SeedTwoHarnesses();

            _ = new CleanService(store, store, store, store, new Mock<ICanonProjectionService>().Object, new Mock<ITranscriptIngestionService>().Object)
                .CleanDatabase(new CleanScope(All: true));

            Assert.Equal(1, store.CountIngestProblems());
            Assert.Equal(1, store.CountQuarantinedSpans());
        }

        [Fact]
        public void HarnessScopeWipesOnlyTheNormalizedSelection()
        {
            InMemoryRelationalStore store = SeedTwoHarnesses();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            projection.Setup(service => service.ProjectIncremental()).Returns(EmptyProjection);
            projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);

            CleanReport report = new CleanService(store, store, store, store, projection.Object, ingestion.Object).CleanDatabase(
                new CleanScope(Harnesses: ["  claude-code ", "not-a-harness"]));

            Assert.Equal(["claude-code"], report.Harnesses);
            Assert.Equal(2, report.Wipe.RecordsDeleted);
            Assert.Equal(0, report.Wipe.SessionsDeleted);
            Assert.Equal(2, report.Wipe.TurnsDeleted);
            Assert.Equal(1, report.Wipe.RunsDeleted);
            Assert.Equal(1, report.Wipe.ExecutionsDeleted);
            Assert.Equal(1, report.Wipe.FindingsDeleted);
            Assert.Equal(1, report.Wipe.RollupsDeleted);

            Assert.Equal(0, store.CountRecords("claude-code"));
            Assert.Equal(1, store.CountRecords("codex"));
            Assert.Empty(store.Sources("claude-code"));
            Assert.Single(store.Sources("codex"));
            Assert.Empty(store.ListSessionProjections("claude-code"));
            Assert.Single(store.ListSessionProjections("codex"));
            Assert.Empty(store.ListRuns("claude-code"));
            Assert.Single(store.ListRuns("codex"));
            Assert.Empty(store.ListFindings(null, "sess-claude"));
            Assert.Single(store.ListFindings(null, "sess-codex"));
            Assert.Equal(2, store.Sessions().Count);
        }

        [Fact]
        public void IngestionAlwaysResumesEvenWhenTheWipeThrows()
        {
            InMemoryRelationalStore store = SeedTwoHarnesses();
            ThrowingWriter writer = new();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();

            List<string> lifecycle = [];
            Assert.Throws<IOException>(() => new CleanService(store, writer, store, store, projection.Object, ingestion.Object).CleanDatabase(
                new CleanScope(All: true),
                pauseIngestion: () => lifecycle.Add("pause"),
                resumeIngestion: () => lifecycle.Add("resume")));

            Assert.Equal(["pause", "resume"], lifecycle);
            projection.Verify(service => service.ProjectIncremental(), Times.Never);
        }

        [Fact]
        public void ReingestUsesTheCallerCallbackWhenSupplied()
        {
            InMemoryRelationalStore store = SeedTwoHarnesses();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            projection.Setup(service => service.ProjectIncremental()).Returns(EmptyProjection);
            projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);

            IReadOnlyList<string>? reingestedHarnesses = null;
            int reingestedWeeks = 0;
            CleanReport report = new CleanService(store, store, store, store, projection.Object, ingestion.Object).CleanDatabase(
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
            InMemoryRelationalStore store = SeedTwoHarnesses();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            projection.Setup(service => service.ProjectIncremental()).Returns(EmptyProjection);
            projection.Setup(service => service.ProjectRunsAndHarnesses()).Returns([]);
            ingestion.Setup(service => service.RefreshHarnessSources(2)).Returns(new RefreshReport(string.Empty, 0, string.Empty, [], 0, 0, 0, 0, 0, 0));

            CleanReport report = new CleanService(store, store, store, store, projection.Object, ingestion.Object).CleanDatabase(
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
            InMemoryRelationalStore store = SeedTwoHarnesses();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();

            bool paused = false;
            using JsonDocument document = JsonDocument.Parse(scopeJson);
            CleanScope scope = new(
                All: document.RootElement.GetProperty("all").GetBoolean(),
                Harnesses: [.. document.RootElement.GetProperty("harnesses").EnumerateArray().Select(item => item.GetString()!)]);

            Assert.Throws<ArgumentException>(() => new CleanService(store, store, store, store, projection.Object, ingestion.Object).CleanDatabase(
                scope,
                pauseIngestion: () => paused = true));

            Assert.False(paused);
            Assert.Equal(3, store.CountRecords());
            Assert.Equal(2, store.Sources().Count);
        }

        [Fact]
        public void ReingestWeeksOutsideOneTo104IsRefused()
        {
            InMemoryRelationalStore store = SeedTwoHarnesses();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();

            Assert.Throws<ArgumentException>(() => new CleanService(store, store, store, store, projection.Object, ingestion.Object)
                .CleanDatabase(new CleanScope(All: true, ReingestWeeks: 105)));
            Assert.Throws<ArgumentException>(() => new CleanService(store, store, store, store, projection.Object, ingestion.Object)
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

        [Theory]
        [InlineData("aider")]
        [InlineData("windsurf")]
        [InlineData(" AIDER ")]
        [InlineData("Cascade")]
        public void CleanScopeRejectsThePrunedAiderAndWindsurfIdentities(string prunedHarness)
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => CleanService.ValidateScope(new CleanScope(Harnesses: [prunedHarness])));

            Assert.Contains("selects no known harness", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void CleanScopeDropsPrunedIdentitiesMixedWithValidOnes()
        {
            IReadOnlyList<string>? scope = CleanService.ValidateScope(
                new CleanScope(Harnesses: ["aider", "codex", "windsurf"]));

            Assert.Equal(["codex"], scope);
        }

        private static InMemoryRelationalStore SeedTwoHarnesses()
        {
            InMemoryRelationalStore store = new();

            SeedHarness(store, "claude-code", "sess-claude", recordCount: 2);
            SeedHarness(store, "codex", "sess-codex", recordCount: 1);

            store.ReplaceSessionProjections(
            [
                Projection("sess-claude", "claude-code"),
                Projection("sess-codex", "codex"),
            ]);
            store.ReplaceSessionTurns("sess-claude",
            [
                new StoredSessionTurn("sess-claude", 0, "claude-code-span-1"),
                new StoredSessionTurn("sess-claude", 1, "claude-code-span-2"),
            ]);
            store.ReplaceSessionTurns("sess-codex", []);

            store.ReplaceRuns(
            [
                new StoredRun("run-claude", "claude-code", "explicit", "explicit_run_id", 1),
                new StoredRun("run-codex", "codex", "explicit", "explicit_run_id", 1),
            ]);
            store.ReplaceExecutions(
            [
                new StoredExecution("exec-claude", "run-claude", "claude-code", IsRoot: true, SessionId: "sess-claude"),
                new StoredExecution("exec-codex", "run-codex", "codex", IsRoot: true, SessionId: "sess-codex"),
            ]);
            store.ReplaceHarnessRollups(
            [
                new StoredHarnessRollup("claude-code", 1, 1, 1, 0, 1, []),
                new StoredHarnessRollup("codex", 1, 1, 1, 0, 1, []),
            ]);
            store.ReplaceFindings(
            [
                Finding("finding-claude", "sess-claude"),
                Finding("finding-codex", "sess-codex"),
            ]);
            store.WriteProblem(new CollectedProblem(
                ProblemKey: "problem-claude",
                Severity: "warn",
                Code: "INGEST_VALIDATION",
                Message: "span validation failed",
                HarnessId: "claude-code"));
            store.PutQuarantinedSpan(new StoredQuarantinedSpan(
                "claude-code-span-9",
                "unsupported-version",
                ["otlp"],
                HarnessId: "claude-code"));
            store.SetProjectionDirtyState(new DirtyProjectionState(1, 2, ["sess-claude"], IsWildcard: false));

            return store;
        }

        private static void SeedHarness(InMemoryRelationalStore store, string harness, string sessionKey, int recordCount)
        {
            List<CollectedRecord> records = [];
            for (int index = 1; index <= recordCount; index++)
            {
                records.Add(MakeRecord($"{harness}-span-{index}", sessionKey, harness));
            }

            store.WriteBatch(new CollectedBatch(
                Source: new CollectedSource(harness, SourceKind.Otlp, $"otlp:{harness}"),
                Sessions: [new CollectedSession(sessionKey, harness, sessionKey, Workspace: null, Title: null, ReportedUsage: [])],
                Records: records,
                Problems: []));
        }

        private static CollectedRecord MakeRecord(string spanId, string sessionKey, string harness) => new(
            RecordId: spanId,
            HarnessId: harness,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "llm.invoke",
            StartedAt: new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: 100,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 10),
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
            ReportedOutputTokens: 5);

        private static StoredSessionProjection Projection(string sessionId, string harness) => new(
            SessionId: sessionId,
            Label: $"{harness} session",
            IsSubagent: false,
            SpanCount: 1,
            StartedAtUtc: "2026-11-14T10:00:00.000Z",
            EndedAtUtc: "2026-11-14T10:05:00.000Z",
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: []);

        private static StoredFinding Finding(string findingId, string sessionId) => new(
            FindingId: findingId,
            DetectorId: "oversized-tool-result",
            Title: "Tool result exceeded the bounded size",
            Mechanism: "mcp",
            Confidence: "measured",
            Recommendation: "Trim oversized tool results at the harness boundary.",
            OutcomeRiskCaveat: "No comparable runs.",
            RankScore: 1.0,
            EvidenceLinks: [],
            SessionId: sessionId);

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

        /// <summary>Writer whose every operation fails, so the wipe itself throws.</summary>
        private sealed class ThrowingWriter : IRelationalStoreWriter
        {
            public void WriteBatch(CollectedBatch batch) => throw new IOException("disk full");

            public void DeleteSource(string sourceId) => throw new IOException("disk full");

            public void ClearEstimatedRecordTokens(string harnessId, string sourceLocator, EstimatedRecordTokens estimated) =>
                throw new IOException("disk full");

            public void WriteProblem(CollectedProblem problem) => throw new IOException("disk full");
        }
    }

    /// <summary>Doctor diagnostics report the relational store figures and the breaker state verbatim.</summary>
    public class DoctorServiceTests
    {
        [Theory]
        [InlineData(DashCircuitState.Closed, "closed")]
        [InlineData(DashCircuitState.Open, "open")]
        [InlineData(DashCircuitState.HalfOpen, "half-open")]
        [InlineData(DashCircuitState.Isolated, "isolated")]
        public void DiagnosticsSurfaceTheCircuitBreakerStateAccurately(DashCircuitState state, string expected)
        {
            InMemoryRelationalStore store = new();
            store.WriteBatch(new CollectedBatch(
                Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
                Sessions: [new CollectedSession("sess-doctor", "claude-code", "sess-doctor", Workspace: null, Title: null, ReportedUsage: [])],
                Records:
                [
                    Record("doctor-span-1", "sess-doctor"),
                    Record("doctor-span-2", "sess-doctor"),
                ],
                Problems: []));
            Mock<ICircuitBreaker> breaker = new();
            breaker.SetupGet(candidate => candidate.State).Returns(state);

            DoctorDiagnosticsReport report = new DoctorService(store, store, breaker.Object).RunDiagnostics();

            Assert.Equal(string.Empty, report.DatabasePath);
            Assert.Equal(2, report.Records);
            Assert.Equal(1, report.Sessions);
            Assert.Equal(["claude-code"], report.ObservedHarnesses);
            Assert.Equal(expected, report.CircuitBreakerState);
        }

        [Fact]
        public void DiagnosticsReportTheRealDatabasePathOfASqliteStore()
        {
            using TempDirectory directory = new();
            string databasePath = directory.Combine("doctor.db");
            SqliteRelationalStore store = new(databasePath, new SqliteCircuitBreaker());
            Mock<ICircuitBreaker> breaker = new();

            DoctorDiagnosticsReport report = new DoctorService(store, store, breaker.Object).RunDiagnostics();

            Assert.Equal(Path.GetFullPath(databasePath), report.DatabasePath);
            Assert.Equal(0, report.Records);
            Assert.Equal(0, report.Sessions);
            Assert.Empty(report.ObservedHarnesses);
            Assert.Null(report.LastRebuild);
        }

        [Fact]
        public void DiagnosticsSurfaceTheLastRebuildOfARebuiltStore()
        {
            using TempDirectory directory = new();
            string databasePath = directory.Combine("rebuilt-doctor.db");
            FakeRelationalStoreFileSystem fakeFs = new();
            fakeFs.AddExistingFile(databasePath);
            ScriptedRelationalStore store = new(
                databasePath,
                new SqliteCircuitBreaker(),
                initialize: false,
                fakeFs,
                log: _ => { });
            store.EnqueueResponse("store_state");
            store.EnqueueResponse("24\t5");
            store.EnqueueResponse(string.Empty);
            store.EnqueueResponse(string.Empty);
            store.EnsureInitialized();
            store.EnqueueResponse("0");
            store.EnqueueResponse(string.Empty);
            store.EnqueueResponse(string.Empty);
            Mock<ICircuitBreaker> breaker = new();
            breaker.SetupGet(candidate => candidate.State).Returns(DashCircuitState.Closed);

            DoctorDiagnosticsReport report = new DoctorService(store, store, breaker.Object).RunDiagnostics();

            Assert.NotNull(report.LastRebuild);
            Assert.Equal(Path.GetFullPath(databasePath), report.LastRebuild.DatabasePath);
            Assert.Equal(24, report.LastRebuild.OldSchemaVersion);
            Assert.Equal(SqliteRelationalStore.SchemaVersion, report.LastRebuild.NewSchemaVersion);
            Assert.True(report.LastRebuild.SettingsResetToDefaults);
            Assert.Equal(["OTLP-only", "unevidenced (zcode)"], report.LastRebuild.UnrecoverableHistoryClasses);
            Assert.False(report.LastRebuild.ReingestTriggered);
            Assert.True(store.HasPendingRebuildReingest);
        }

        [Fact]
        public void RunVacuumLeavesTheRelationalStoreAnswering()
        {
            using TempDirectory directory = new();
            string databasePath = directory.Combine("vacuum.db");
            FreelistSqliteRelationalStore store = new(databasePath, new SqliteCircuitBreaker());
            Mock<ICircuitBreaker> breaker = new();
            string filler = new('x', 2000);

            for (int batchIndex = 0; batchIndex < 80; batchIndex++)
            {
                CollectedRecord[] records = new CollectedRecord[10];
                for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
                {
                    records[recordIndex] = Record($"vacuum-span-{batchIndex}-{recordIndex}", "sess-vacuum") with
                    {
                        Parts =
                        [
                            new CollectedContentPart(0, ContentBucket.UserPrompt, filler, null, null, null, "user", null, null),
                            new CollectedContentPart(1, ContentBucket.Reasoning, filler, null, null, null, "assistant", null, null),
                        ],
                    };
                }

                store.WriteBatch(new CollectedBatch(
                    Source: new CollectedSource("claude-code", SourceKind.File, $"/tmp/vacuum-batch-{batchIndex}.jsonl"),
                    Sessions: [],
                    Records: records,
                    Problems: []));
            }

            foreach (StoredSource source in store.Sources("claude-code"))
            {
                store.DeleteSource(source.SourceId);
            }

            Assert.True(store.FreelistPageCount() > 0, "deleting every source should leave free pages for vacuum to reclaim");

            Assert.True(new DoctorService(store, store, breaker.Object).RunVacuum());

            Assert.Equal(0, store.FreelistPageCount());
            Assert.Equal(SqliteRelationalStore.SchemaVersion, store.GetStoreState()?.SchemaVersion);
        }

        /// <summary>A SQLite store that can read <c>PRAGMA freelist_count</c>, the pages only a vacuum reclaims.</summary>
        private sealed class FreelistSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : SqliteRelationalStore(databasePath, circuitBreaker)
        {
            public long FreelistPageCount() =>
                long.Parse(ExecuteSqliteCore("PRAGMA freelist_count;\n").Trim(), CultureInfo.InvariantCulture);
        }

        [Fact]
        public void RunVacuumOnANonSqliteStoreReportsThatVacuumDoesNotApply()
        {
            InMemoryRelationalStore store = new();
            Mock<ICircuitBreaker> breaker = new();

            Assert.False(new DoctorService(store, store, breaker.Object).RunVacuum());
        }

        [Fact]
        public void ConstructorRejectsNullDependencies()
        {
            InMemoryRelationalStore store = new();
            Mock<ICircuitBreaker> breaker = new();

            Assert.Throws<ArgumentNullException>(() => new DoctorService(null!, store, breaker.Object));
            Assert.Throws<ArgumentNullException>(() => new DoctorService(store, null!, breaker.Object));
            Assert.Throws<ArgumentNullException>(() => new DoctorService(store, store, null!));
        }

        private static CollectedRecord Record(string spanId, string sessionKey) => new(
            RecordId: spanId,
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "llm.invoke",
            StartedAt: new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: 100,
            Tokens: [new CollectedTokenCount(TokenClass.Input, 10)],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null));

        /// <summary>Filesystem fake answering the D15 read-only store seam without touching disk.</summary>
        private class FakeRelationalStoreFileSystem : IRelationalStoreFileSystem
        {
            private readonly HashSet<string> existingFiles = new(StringComparer.Ordinal);

            public void AddExistingFile(string path) => existingFiles.Add(Path.GetFullPath(path));

            public bool FileExists(string path) => existingFiles.Contains(Path.GetFullPath(path));

            public void CreateDirectory(string path)
            {
            }

            public void DeleteFile(string path) => existingFiles.Remove(Path.GetFullPath(path));
        }

        /// <summary>Scripted store answering queued SQLite responses so a rebuild runs without a real database.</summary>
        private class ScriptedRelationalStore : SqliteRelationalStore
        {
            private readonly Queue<string> queuedResponses = new();

            public ScriptedRelationalStore(
                string databasePath,
                ICircuitBreaker circuitBreaker,
                bool initialize,
                IRelationalStoreFileSystem fileSystem,
                Action<string>? log = null)
                : base(databasePath, circuitBreaker, initialize, fileSystem, onRebuiltFromOlderSchema: null, log)
            {
            }

            public void EnqueueResponse(string response) => queuedResponses.Enqueue(response);

            protected override string ExecuteSqliteCore(string sql)
            {
                if (!FileSystem.FileExists(DatabasePath))
                {
                    EnsureDatabaseDirectory();
                }

                return queuedResponses.Count > 0 ? queuedResponses.Dequeue() : string.Empty;
            }
        }
    }

    /// <summary>Report rendering over a mocked query service and the relational projection store.</summary>
    public class ReportServiceTests
    {
        [Fact]
        public void JsonFormatPrintsTheQueryPayload()
        {
            Harness harness = new(populated: true);

            (int exitCode, string output) = harness.Reports.GenerateReport("json", harness: "claude-code", limit: 7);

            Assert.Equal(0, exitCode);
            using JsonDocument document = JsonDocument.Parse(output);
            Assert.Equal(3, document.RootElement.GetProperty("sessionCount").GetInt32());
            harness.Query.Verify(service => service.GetReport("claude-code", null, null, 7), Times.Once);
        }

        [Fact]
        public void MarkdownRendersHarnessesAndFindings()
        {
            Harness harness = new(populated: true);

            (int exitCode, string output) = harness.Reports.GenerateReport("markdown", limit: 1);

            Assert.Equal(0, exitCode);
            Assert.Contains("# KyberDash report", output, StringComparison.Ordinal);
            Assert.Contains("| Harness | Sessions | Context p50 | Context p95 | Cache hit |", output, StringComparison.Ordinal);
            Assert.Contains("| claude-code | 4 | absent | absent | absent |", output, StringComparison.Ordinal);
            Assert.Contains("absent", output, StringComparison.Ordinal);
            Assert.Contains("**dormant-tool-schema**", output, StringComparison.Ordinal);
        }

        [Fact]
        public void MarkdownRendersMeasuredRollupMetrics()
        {
            Harness harness = new(populated: true);
            harness.Store.ReplaceHarnessRollups(
            [
                new StoredHarnessRollup(
                    "claude-code",
                    SessionCount: 6,
                    RunCount: 2,
                    ExecutionCount: 3,
                    UnknownWindowSessions: 1,
                    WindowSessionsTotal: 6,
                    Metrics:
                    [
                        new StoredHarnessRollupMetric("context_pressure_median", 0.425, "measured"),
                        new StoredHarnessRollupMetric("cache_hit_rate", 0.9, "derived"),
                    ]),
            ]);

            (int exitCode, string output) = harness.Reports.GenerateReport("markdown");

            Assert.Equal(0, exitCode);
            Assert.Contains("| claude-code | 6 | 42.5% | absent | 90% |", output, StringComparison.Ordinal);
        }

        [Fact]
        public void TextRendersEmptySectionsHonestly()
        {
            Harness harness = new(populated: false);

            (int exitCode, string output) = harness.Reports.GenerateReport("text");

            Assert.Equal(0, exitCode);
            Assert.Contains("KyberDash report", output, StringComparison.Ordinal);
            Assert.Contains("(none recorded)", output, StringComparison.Ordinal);
        }

        [Fact]
        public void UnknownFormatExitsTwoWithGuidance()
        {
            Harness harness = new(populated: true);

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
            Harness harness = new(populated: true);

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
            public Harness(bool populated)
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
                if (populated)
                {
                    Store.WriteBatch(new CollectedBatch(
                        Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
                        Sessions: [new CollectedSession("sess-report", "claude-code", "sess-report", Workspace: null, Title: null, ReportedUsage: [])],
                        Records: [],
                        Problems: []));
                    Store.ReplaceHarnessRollups([Rollup]);
                    Store.ReplaceFindings([Finding]);
                }

                Reports = new ReportService(Query.Object, Store);
            }

            public Mock<IDashboardQueryService> Query { get; } = new();

            public InMemoryRelationalStore Store { get; } = new();

            public StoredHarnessRollup Rollup { get; } = new(
                "claude-code",
                SessionCount: 4,
                RunCount: 1,
                ExecutionCount: 1,
                UnknownWindowSessions: 0,
                WindowSessionsTotal: 4,
                Metrics: []);

            public StoredFinding Finding { get; } = new(
                FindingId: "finding-1",
                DetectorId: "dormant-tool-schema",
                Title: "Tool schema never invoked",
                Mechanism: "mcp",
                Confidence: "measured",
                Recommendation: "Relocate dormant tool schemas to on-demand deferred loading.",
                OutcomeRiskCaveat: "No comparable runs.",
                RankScore: 1.0,
                EvidenceLinks: []);

            public ReportService Reports { get; }
        }
    }

    /// <summary>Job-scheduler lifecycle: the rebuild re-ingest handoff and the lease arbitration.</summary>
    public class JobSchedulerServiceTests
    {
        [Fact]
        public void StartRunsImportHistoryOnceWhenTheStoreArmsARebuildReingest()
        {
            using TempDirectory directory = new();
            SqliteRelationalStore store = new(directory.Combine("rebuild.db"), new SqliteCircuitBreaker());
            ArmPendingRebuildReingest(store);
            Assert.True(store.HasPendingRebuildReingest);

            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            ingestion.Setup(service => service.RefreshHarnessSources(It.IsAny<int>())).Returns(new RefreshReport(string.Empty, 0, string.Empty, [], 0, 0, 0, 0, 0, 0));
            FakeSettingsRepository settings = new();
            FakeServerStateRepository serverState = new();

            using JobSchedulerService jobs = new(store, store, projection.Object, ingestion.Object, settings, serverState, Options(serverState));
            jobs.Start();

            ingestion.Verify(service => service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks), Times.Once);
            Assert.False(store.HasPendingRebuildReingest);
        }

        [Fact]
        public void StartLogsTheRebuildReingestItTriggers()
        {
            using TempDirectory directory = new();
            SqliteRelationalStore store = new(directory.Combine("rebuild-logged.db"), new SqliteCircuitBreaker());
            ArmPendingRebuildReingest(store);

            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            ingestion.Setup(service => service.RefreshHarnessSources(It.IsAny<int>())).Returns(new RefreshReport(string.Empty, 0, string.Empty, [], 0, 0, 0, 0, 0, 0));
            FakeSettingsRepository settings = new();
            FakeServerStateRepository serverState = new();
            List<string> logs = [];

            using JobSchedulerService jobs = new(
                store,
                store,
                projection.Object,
                ingestion.Object,
                settings,
                serverState,
                Options(serverState),
                logs.Add);
            jobs.Start();

            Assert.Single(logs);
            Assert.Contains("rebuilt its relational store", logs[0], StringComparison.Ordinal);
            Assert.Contains("import-history", logs[0], StringComparison.Ordinal);
            ingestion.Verify(service => service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks), Times.Once);
            Assert.False(store.HasPendingRebuildReingest);
        }

        [Fact]
        public void StartLeavesImportHistoryAloneWhenNoRebuildIsPending()
        {
            using TempDirectory directory = new();
            SqliteRelationalStore store = new(directory.Combine("quiet.db"), new SqliteCircuitBreaker());

            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            ingestion.Setup(service => service.RefreshHarnessSources(It.IsAny<int>())).Returns(new RefreshReport(string.Empty, 0, string.Empty, [], 0, 0, 0, 0, 0, 0));
            FakeSettingsRepository settings = new();
            FakeServerStateRepository serverState = new();

            using JobSchedulerService jobs = new(store, store, projection.Object, ingestion.Object, settings, serverState, Options(serverState));
            jobs.Start();

            ingestion.Verify(service => service.RefreshHarnessSources(It.IsAny<int>()), Times.Never);
            Assert.False(store.HasPendingRebuildReingest);
        }

        [Fact]
        public async Task RefreshDeclinesWhilePausedButImportHistoryStillRuns()
        {
            InMemoryRelationalStore store = new();
            FakeSettingsRepository settings = new();
            FakeServerStateRepository serverState = new();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            ingestion.Setup(service => service.RefreshHarnessSources(It.IsAny<int>())).Returns(new RefreshReport(string.Empty, 0, string.Empty, [], 0, 0, 0, 0, 0, 0));

            using JobSchedulerService jobs = new(store, store, projection.Object, ingestion.Object, settings, serverState, Options(serverState));

            JobOutcome idle = await jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal("succeeded", idle.Outcome);

            settings.WriteBooleanSetting(SettingsService.JobsPausedKey, true);
            JobOutcome paused = await jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal("declined", paused.Outcome);
            Assert.True(jobs.GetStatus().Paused);

            JobOutcome imported = await jobs.TriggerImportHistoryAsync(TestContext.Current.CancellationToken);
            Assert.Equal("succeeded", imported.Outcome);
            ingestion.Verify(service => service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks), Times.Once);
        }

        [Fact]
        public async Task StartAcquiresTheLeaseAndStopReleasesIt()
        {
            InMemoryRelationalStore store = new();
            FakeSettingsRepository settings = new();
            FakeServerStateRepository serverState = new();
            Mock<ICanonProjectionService> projection = new();
            Mock<ITranscriptIngestionService> ingestion = new();
            ingestion.Setup(service => service.RefreshHarnessSources(It.IsAny<int>())).Returns(new RefreshReport(string.Empty, 0, string.Empty, [], 0, 0, 0, 0, 0, 0));

            JobSchedulerService jobs = new(store, store, projection.Object, ingestion.Object, settings, serverState, Options(serverState));
            try
            {
                jobs.Start();
                Assert.Equal(1, serverState.JobsLeaseRequests);

                await jobs.StopAsync();
                Assert.False(jobs.GetStatus().HostedElsewhere);
            }
            finally
            {
                await jobs.DisposeAsync();
            }
        }

        private static DashOptions Options(FakeServerStateRepository serverState) =>
            new() { StateDirectory = serverState.DefaultStateDirectory };

        /// <summary>
        /// Arms the store's pending rebuild re-ingest flag directly. The rebuild path that arms it in
        /// production lands with the older-schema rebuild work; until then this is the only way a
        /// test can observe the scheduler consuming the flag.
        /// </summary>
        private static void ArmPendingRebuildReingest(SqliteRelationalStore store)
        {
            FieldInfo? field = typeof(SqliteRelationalStore).GetField(
                "pendingRebuildReingest",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(store, 1);
        }
    }

    /// <summary>Composition root wiring: every migrated service resolves over the relational store.</summary>
    public class ServiceCompositionTests
    {
        [Fact]
        public void AddDashApplication_ResolvesEveryMigratedServiceOverTheRelationalStore()
        {
            using TempDirectory directory = new();
            DashOptions options = new() { DatabasePath = directory.Combine("composition.db") };

            using ServiceProvider provider = new ServiceCollection().AddDashApplication(options).BuildServiceProvider();

            Assert.IsType<CleanService>(provider.GetRequiredService<ICleanService>());
            Assert.IsType<CaptureService>(provider.GetRequiredService<ICaptureService>());
            Assert.IsType<JobSchedulerService>(provider.GetRequiredService<IJobSchedulerService>());
            Assert.IsType<DoctorService>(provider.GetRequiredService<IDoctorService>());
            Assert.IsType<ReportService>(provider.GetRequiredService<IReportService>());
            Assert.IsType<E2eSeederService>(provider.GetRequiredService<IE2eSeederService>());
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
                    new CanonSessionProjectorEngine(Store, Store, Store, Pricing, new FakeTextTokenizer()),
                    new CanonRollupsEngine(Store, Store, Pricing));
                Analytics = new AnalyticsService(bridge);
            }

            public InMemoryRelationalStore Store { get; } = new();

            public FakePricingRepository Pricing { get; } = new();

            public AnalyticsService Analytics { get; }
        }
    }

    /// <summary>
    /// Report projection alignment: injected store paths, 4-arg vs 5-arg windowing, bounded
    /// scorecard derivation without N+1 scans, unmeasurable figure reasons, and non-blended cost.
    /// </summary>
    public class BridgeReportTests
    {
        private static readonly DateTimeOffset FixedNow = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void CoverageStorePathUsesDefaultResolutionOrInjectedDatabasePath()
        {
            InMemoryRelationalStore store = new();
            FakePricingRepository pricing = new();
            KyberBridgeEngine defaultBridge = new(
                store,
                store,
                store,
                pricing,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing),
                clock: () => FixedNow);

            JsonElement defaultReport = defaultBridge.GetReport();
            Assert.Equal(
                CanonDbPath.Resolve(),
                defaultReport.GetProperty("coverage").GetProperty("storePath").GetString());

            KyberBridgeEngine customBridge = new(
                store,
                store,
                store,
                pricing,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing),
                version: "1.9.0",
                clock: () => FixedNow,
                storePath: "/tmp/custom-report.db");

            JsonElement customReport = customBridge.GetReport();
            Assert.Equal(
                "/tmp/custom-report.db",
                customReport.GetProperty("coverage").GetProperty("storePath").GetString());
            Assert.Equal("1.9.0", customReport.GetProperty("kyberdashVersion").GetString());

            ServiceCollection services = new();
            services.AddSingleton<IRelationalStoreWriter>(store);
            services.AddSingleton<IRelationalStoreReader>(store);
            services.AddSingleton<IProjectionStore>(store);
            services.AddSingleton<IOperationalStore>(store);
            services.AddSingleton<IPricingRepository>(pricing);
            services.AddDashApplication(new DashOptions
            {
                DatabasePath = "/tmp/di-canon.db",
                StateDirectory = Path.GetTempPath(),
                Version = "2.4.0",
            });

            using ServiceProvider provider = services.BuildServiceProvider();
            IDashboardQueryService queryService = provider.GetRequiredService<IDashboardQueryService>();
            JsonElement diReport = queryService.GetReport();
            Assert.Equal("/tmp/di-canon.db", diReport.GetProperty("coverage").GetProperty("storePath").GetString());
            Assert.Equal("2.4.0", diReport.GetProperty("kyberdashVersion").GetString());
        }

        [Fact]
        public void EmptyStoreEmitsFullContextReportShapeWithCompatibilityScalars()
        {
            InMemoryRelationalStore store = new();
            KyberBridgeEngine bridge = CreateBridge(store, version: "1.2.3", storePath: "canon.db");

            JsonElement report = bridge.GetReport();

            Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("2026-11-20T12:00:00.000Z", report.GetProperty("generatedAt").GetString());
            Assert.Equal("1.2.3", report.GetProperty("kyberdashVersion").GetString());
            Assert.True(report.GetProperty("storeGeneration").GetInt64() >= 0);
            Assert.Equal(0, report.GetProperty("sessionCount").GetInt32());
            Assert.Equal(0, report.GetProperty("findingCount").GetInt32());
            Assert.Equal(0, report.GetProperty("harnessCount").GetInt32());

            JsonElement scope = report.GetProperty("scope");
            Assert.Equal(7, scope.GetProperty("days").GetInt32());
            Assert.False(scope.TryGetProperty("harness", out _));
            Assert.False(scope.TryGetProperty("sessionId", out _));
            Assert.False(scope.TryGetProperty("runId", out _));

            JsonElement coverage = report.GetProperty("coverage");
            Assert.Equal("canon.db", coverage.GetProperty("storePath").GetString());
            Assert.Empty(coverage.GetProperty("harnesses").EnumerateArray());
            Assert.Equal(0, coverage.GetProperty("quarantineCount").GetInt32());
            Assert.Equal(0, coverage.GetProperty("problemCount").GetInt32());
            Assert.NotEmpty(coverage.GetProperty("hints").EnumerateArray());

            Assert.Empty(report.GetProperty("findings").EnumerateArray());
            Assert.Empty(report.GetProperty("harnesses").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, report.GetProperty("latestSession").ValueKind);

            JsonElement unknownCost = Assert.Single(report.GetProperty("cost").EnumerateArray());
            Assert.Equal("unknown", unknownCost.GetProperty("basis").GetString());
            Assert.Equal(JsonValueKind.Null, unknownCost.GetProperty("amountUsd").GetProperty("value").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(unknownCost.GetProperty("amountUsd").GetProperty("reason").GetString()));
        }

        [Fact]
        public void FourArgOverloadDefaultsToSevenDaysWhileFiveArgFiltersWindow()
        {
            InMemoryRelationalStore store = new();
            FakePricingRepository pricing = new();

            WriteRichSession(
                store,
                sessionId: "sess-recent",
                harnessId: "claude-code",
                startedAt: FixedNow.AddDays(-2),
                inputTokens: 1_000,
                cacheReadTokens: 800,
                reportedCostUsd: 0.25,
                toolResultText: new string('a', 14_000));
            WriteRichSession(
                store,
                sessionId: "sess-old",
                harnessId: "codex",
                startedAt: FixedNow.AddDays(-20),
                inputTokens: 1_000,
                cacheReadTokens: 0,
                reportedCostUsd: 0.75,
                toolResultText: new string('b', 14_000));

            _ = new CanonProjectionService(
                store,
                store,
                store,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing)).ProjectAll();

            KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
            DashboardQueryService queryService = new(bridge);

            JsonElement defaultReport = queryService.GetReport();
            Assert.Equal(7, defaultReport.GetProperty("scope").GetProperty("days").GetInt32());
            Assert.Equal(1, defaultReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal(1, defaultReport.GetProperty("harnessCount").GetInt32());
            JsonElement recentHarness = Assert.Single(defaultReport.GetProperty("harnesses").EnumerateArray());
            Assert.Equal("claude-code", recentHarness.GetProperty("harness").GetString());
            Assert.NotEmpty(defaultReport.GetProperty("findings").EnumerateArray());
            foreach (JsonElement finding in defaultReport.GetProperty("findings").EnumerateArray())
            {
                Assert.Equal("sess-recent", finding.GetProperty("sessionId").GetString());
                Assert.StartsWith("finding/", finding.GetProperty("view").GetString(), StringComparison.Ordinal);
                Assert.True(finding.TryGetProperty("recoverableTokens", out _));
            }

            List<JsonElement> coverageHarnesses = [.. defaultReport.GetProperty("coverage").GetProperty("harnesses").EnumerateArray()];
            Assert.Equal(2, coverageHarnesses.Count);
            Assert.Equal(1, coverageHarnesses.Single(static h => h.GetProperty("harness").GetString() == "claude-code").GetProperty("sessionsInWindow").GetInt32());
            Assert.Equal(0, coverageHarnesses.Single(static h => h.GetProperty("harness").GetString() == "codex").GetProperty("sessionsInWindow").GetInt32());

            JsonElement thirtyDayReport = queryService.GetReport(null, null, null, 50, 30);
            Assert.Equal(30, thirtyDayReport.GetProperty("scope").GetProperty("days").GetInt32());
            Assert.Equal(2, thirtyDayReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal(2, thirtyDayReport.GetProperty("harnessCount").GetInt32());

            JsonElement codexScoped = queryService.GetReport("codex", null, null, 1, 30);
            Assert.Equal("codex", codexScoped.GetProperty("scope").GetProperty("harness").GetString());
            Assert.Equal(1, codexScoped.GetProperty("sessionCount").GetInt32());
            Assert.Equal("sess-old", codexScoped.GetProperty("latestSession").GetProperty("sessionId").GetString());
            Assert.True(codexScoped.GetProperty("findings").GetArrayLength() <= 1);
            Assert.Single(codexScoped.GetProperty("harnesses").EnumerateArray());

            JsonElement sessionScoped = queryService.GetReport(null, "sess-recent", null, 0, 7);
            Assert.Equal("sess-recent", sessionScoped.GetProperty("scope").GetProperty("sessionId").GetString());
            Assert.Equal(1, sessionScoped.GetProperty("sessionCount").GetInt32());
            Assert.True(sessionScoped.GetProperty("findings").GetArrayLength() <= 1);

            StoredRun runForOld = Assert.Single(((IProjectionStore)store).ListRuns("codex"));
            JsonElement runScoped = queryService.GetReport(null, null, runForOld.RunId, 500, 30);
            Assert.Equal(runForOld.RunId, runScoped.GetProperty("scope").GetProperty("runId").GetString());
            Assert.Equal(1, runScoped.GetProperty("sessionCount").GetInt32());
            Assert.Equal("sess-old", runScoped.GetProperty("latestSession").GetProperty("sessionId").GetString());
        }

        [Fact]
        public void UnexportedDimensionsAndUnmeasurableContextEmitReasons()
        {
            InMemoryRelationalStore store = new();
            FakePricingRepository pricing = new();

            store.WriteBatch(new CollectedBatch(
                Source: new CollectedSource("cursor", SourceKind.Otlp, "otlp:cursor"),
                Sessions:
                [
                    new CollectedSession("sess-cursor", "cursor", "sess-cursor", new CollectedWorkspace("/work/proj", "proj", "main"), null, []),
                ],
                Records:
                [
                    new CollectedRecord(
                        RecordId: "span-cur-1",
                        HarnessId: "cursor",
                        Op: RecordOp.LlmInvoke,
                        Kind: RecordKind.Client,
                        Name: "chat.turn",
                        StartedAt: FixedNow.AddHours(-2),
                        Status: RecordStatus.Ok,
                        SessionKey: "sess-cursor",
                        ParentRecordId: null,
                        TraceId: null,
                        Model: "unlisted-model",
                        EndedAt: FixedNow.AddHours(-1),
                        ReportedDurationMs: 500,
                        Tokens:
                        [
                            new CollectedTokenCount(TokenClass.Input, 120),
                            new CollectedTokenCount(TokenClass.Output, 30),
                        ],
                        ReportedCost: null,
                        Parts: [],
                        Tool: null,
                        Agent: null,
                        SkillsInvoked: [],
                        AgentInstanceId: null,
                        ToolDefinitions: [],
                        Signals: new CollectedRecordSignals(null, null, null, null),
                        ReportedInputTokens: 120,
                        ReportedOutputTokens: 30),
                ],
                Problems: []));

            _ = new CanonProjectionService(
                store,
                store,
                store,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing)).ProjectAll();

            KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);
            JsonElement report = bridge.GetReport(null, null, null, 50, 7);

            JsonElement cursorHarness = Assert.Single(report.GetProperty("harnesses").EnumerateArray());
            JsonElement dimensions = cursorHarness.GetProperty("dimensions");
            foreach (string key in new[]
            {
                "contextHygiene",
                "cacheEfficiency",
                "toolYield",
                "skillUtilisation",
                "delegationOverhead",
                "continuity",
            })
            {
                JsonElement dim = dimensions.GetProperty(key);
                Assert.Equal(JsonValueKind.Null, dim.GetProperty("value").ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(dim.GetProperty("reason").GetString()));
            }

            JsonElement latestTurn = report.GetProperty("latestSession").GetProperty("latestTurn");
            Assert.Equal(JsonValueKind.Null, latestTurn.GetProperty("pressure").GetProperty("value").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(latestTurn.GetProperty("pressure").GetProperty("reason").GetString()));
            Assert.Equal(JsonValueKind.Null, latestTurn.GetProperty("contextWindow").GetProperty("value").ValueKind);
            Assert.Equal(JsonValueKind.Null, latestTurn.GetProperty("residual").GetProperty("value").ValueKind);
            foreach (string bucket in new[]
            {
                "system_prompt",
                "tool_definitions",
                "instruction_context",
                "conversation_history",
                "tool_result_content",
            })
            {
                JsonElement bucketVal = latestTurn.GetProperty("buckets").GetProperty(bucket);
                Assert.Equal(JsonValueKind.Null, bucketVal.GetProperty("value").ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(bucketVal.GetProperty("reason").GetString()));
            }
        }

        [Fact]
        public void HydratesSingleLatestSessionOnceWithoutPerSessionLoopsAndKeepsCostBasesSeparate()
        {
            InMemoryRelationalStore store = new();
            FakePricingRepository pricing = new(new Dictionary<string, ModelCosts>(StringComparer.Ordinal)
            {
                ["claude-sonnet-4-6"] = new ModelCosts(3e-6, 15e-6, 3.75e-6, 0.3e-6, 0, 1.0, false),
            });

            for (int index = 0; index < 5; index++)
            {
                WriteRichSession(
                    store,
                    sessionId: $"sess-multi-{index}",
                    harnessId: index % 2 == 0 ? "claude-code" : "codex",
                    startedAt: FixedNow.AddHours(-10 + index),
                    inputTokens: 800,
                    cacheReadTokens: index % 2 == 0 ? 400 : 0,
                    reportedCostUsd: 0.10,
                    toolResultText: index == 4 ? "ok" : null,
                    includeMcpTool: true);
            }

            _ = new CanonProjectionService(
                store,
                store,
                store,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing)).ProjectAll();

            KyberBridgeEngine bridge = new(
                store,
                store,
                store,
                pricing,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing),
                clock: () => FixedNow,
                storePath: "/tmp/multi.db");
            store.ClearReadLog();

            JsonElement report = bridge.GetReport(null, null, null, 50, 7);

            // The report prices its whole scoped window with one batched report-figures read and
            // reads the latest session's turns once through the turn read — no per-session loop.
            Assert.Equal(1, store.ReadLog.Count(static name => name == nameof(IRelationalStoreReader.SessionReportFigures)));
            Assert.Equal(1, store.ReadLog.Count(static name => name == nameof(IRelationalStoreReader.SessionTurnFigures)));
            Assert.DoesNotContain(nameof(IRelationalStoreReader.SessionSummaryFigures), store.ReadLog);
            Assert.DoesNotContain(nameof(IRelationalStoreReader.TurnFigures), store.ReadLog);
            Assert.Equal(5, report.GetProperty("sessionCount").GetInt32());

            JsonElement latestSession = report.GetProperty("latestSession");
            Assert.Equal("sess-multi-4", latestSession.GetProperty("sessionId").GetString());
            Assert.Equal("session/sess-multi-4", latestSession.GetProperty("view").GetString());
            JsonElement latestTurn = latestSession.GetProperty("latestTurn");
            Assert.Equal(1, latestTurn.GetProperty("index").GetInt32());
            Assert.True(latestTurn.GetProperty("pressure").GetProperty("value").GetDouble() > 0);
            Assert.Equal(200_000, latestTurn.GetProperty("contextWindow").GetProperty("value").GetInt64());
            JsonElement buckets = latestTurn.GetProperty("buckets");
            Assert.Equal("tokens", buckets.GetProperty("conversation_history").GetProperty("unit").GetString());
            Assert.Equal("tokens", buckets.GetProperty("system_prompt").GetProperty("unit").GetString());
            Assert.Equal("tokens", latestTurn.GetProperty("residual").GetProperty("unit").GetString());
            Assert.NotEmpty(latestSession.GetProperty("toolDefinitionSources").EnumerateArray());

            List<JsonElement> costRows = [.. report.GetProperty("cost").EnumerateArray()];
            Assert.Contains(costRows, static row => row.GetProperty("basis").GetString() == "harness" && Math.Abs(row.GetProperty("amountUsd").GetProperty("value").GetDouble() - 0.50) < 0.001);
            Assert.Contains(costRows, static row => row.GetProperty("basis").GetString() == "published" && row.GetProperty("amountUsd").GetProperty("value").GetDouble() > 0);
            Assert.Equal(2, costRows.Count);
        }

        [Fact]
        public void ExcludesGeminiAndCasingWhitespaceVariantsFromReportAndDayNarrowing()
        {
            InMemoryRelationalStore store = new();
            FakePricingRepository pricing = new();

            WriteRichSession(
                store,
                sessionId: "sess-agy-out-of-day",
                harnessId: "antigravity",
                startedAt: FixedNow.AddDays(-3),
                inputTokens: 900,
                cacheReadTokens: 300,
                reportedCostUsd: 0.20,
                toolResultText: new string('a', 14_000));
            WriteRichSession(
                store,
                sessionId: "sess-claude-out-of-day",
                harnessId: "claude-code",
                startedAt: FixedNow.AddDays(-3),
                inputTokens: 900,
                cacheReadTokens: 300,
                reportedCostUsd: 0.20);
            WriteRichSession(
                store,
                sessionId: "sess-gemini-lower",
                harnessId: "gemini",
                startedAt: FixedNow.AddHours(-2),
                inputTokens: 500,
                cacheReadTokens: 0,
                reportedCostUsd: 0.10);
            WriteRichSession(
                store,
                sessionId: "sess-gemini-mixed",
                harnessId: " Gemini ",
                startedAt: FixedNow.AddHours(-1),
                inputTokens: 500,
                cacheReadTokens: 0,
                reportedCostUsd: 0.10);
            WriteRichSession(
                store,
                sessionId: "sess-gemini-upper",
                harnessId: "GEMINI",
                startedAt: FixedNow.AddMinutes(-30),
                inputTokens: 500,
                cacheReadTokens: 0,
                reportedCostUsd: 0.10);

            _ = new CanonProjectionService(
                store,
                store,
                store,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing)).ProjectAll();

            // Inject a stale excluded rollup and run to verify GetReport rejects excluded projections directly too.
            store.ReplaceHarnessRollups(
            [
                .. ((IProjectionStore)store).ListHarnessRollups(),
                new StoredHarnessRollup("gemini", 1, 1, 1, 1, 1, []),
            ]);
            store.ReplaceRuns(
            [
                .. ((IProjectionStore)store).ListRuns(),
                new StoredRun("run-gemini", "GEMINI", "explicit", "explicit_run_id", 1, StartedAtUtc: FixedNow.AddMinutes(-30).UtcDateTime.ToString("O", CultureInfo.InvariantCulture), EndedAtUtc: FixedNow.AddMinutes(-29).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ]);

            KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

            JsonElement sevenDayReport = bridge.GetReport(null, null, null, 50, 7);
            Assert.Equal(2, sevenDayReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal(2, sevenDayReport.GetProperty("harnessCount").GetInt32());
            List<string> sevenDayCoverageHarnesses =
            [
                .. sevenDayReport.GetProperty("coverage").GetProperty("harnesses").EnumerateArray()
                    .Select(static entry => entry.GetProperty("harness").GetString() ?? string.Empty),
            ];
            Assert.Equal(["antigravity", "claude-code"], sevenDayCoverageHarnesses);
            Assert.DoesNotContain(
                sevenDayReport.GetProperty("harnesses").EnumerateArray(),
                static entry => string.Equals(entry.GetProperty("harness").GetString(), "gemini", StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual("gemini", sevenDayReport.GetProperty("latestSession").GetProperty("harness").GetString());

            JsonElement oneDayReport = bridge.GetReport(null, null, null, 50, 1);
            Assert.Equal(0, oneDayReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal(0, oneDayReport.GetProperty("findingCount").GetInt32());
            Assert.Equal(0, oneDayReport.GetProperty("harnessCount").GetInt32());
            Assert.Empty(oneDayReport.GetProperty("findings").EnumerateArray());
            Assert.Empty(oneDayReport.GetProperty("harnesses").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, oneDayReport.GetProperty("latestSession").ValueKind);
            List<JsonElement> oneDayCoverage = [.. oneDayReport.GetProperty("coverage").GetProperty("harnesses").EnumerateArray()];
            Assert.Equal(2, oneDayCoverage.Count);
            Assert.All(oneDayCoverage, static entry =>
            {
                Assert.NotEqual("gemini", entry.GetProperty("harness").GetString());
                Assert.Equal(0, entry.GetProperty("sessionsInWindow").GetInt32());
            });

            JsonElement geminiFilterReport = bridge.GetReport(" GEMINI ", null, null, 50, 7);
            Assert.Equal(0, geminiFilterReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal(0, geminiFilterReport.GetProperty("harnessCount").GetInt32());
            Assert.Empty(geminiFilterReport.GetProperty("harnesses").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, geminiFilterReport.GetProperty("latestSession").ValueKind);
        }

        [Fact]
        public void PostCleanSingleHarnessAndFullCleanSessionsDoNotLeakIntoReport()
        {
            InMemoryRelationalStore store = new();
            FakePricingRepository pricing = new();

            WriteRichSession(
                store,
                sessionId: "sess-agy",
                harnessId: "antigravity",
                startedAt: FixedNow.AddHours(-3),
                inputTokens: 1_000,
                cacheReadTokens: 200,
                reportedCostUsd: 0.25,
                toolResultText: new string('a', 14_000));
            WriteRichSession(
                store,
                sessionId: "sess-claude",
                harnessId: "claude-code",
                startedAt: FixedNow.AddHours(-2),
                inputTokens: 1_000,
                cacheReadTokens: 400,
                reportedCostUsd: 0.30,
                toolResultText: new string('b', 14_000));

            CanonProjectionService projection = new(
                store,
                store,
                store,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing));
            _ = projection.ProjectAll();

            CleanService cleanService = new(
                store,
                store,
                store,
                store,
                projection,
                new TranscriptIngestionService(store, store, new Data.Collector.Sources.HarnessSourceRegistry()));
            _ = cleanService.CleanDatabase(new CleanScope(Harnesses: ["antigravity"]));

            Assert.Equal(2, store.Sessions().Count);
            Assert.Empty(((IProjectionStore)store).ListSessionProjections("antigravity"));

            KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

            JsonElement antigravityReport = bridge.GetReport("antigravity", null, null, 50, 7);
            Assert.Equal(0, antigravityReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal(0, antigravityReport.GetProperty("findingCount").GetInt32());
            Assert.Equal(0, antigravityReport.GetProperty("harnessCount").GetInt32());
            Assert.Empty(antigravityReport.GetProperty("findings").EnumerateArray());
            Assert.Empty(antigravityReport.GetProperty("harnesses").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, antigravityReport.GetProperty("latestSession").ValueKind);
            List<string> remainingCoverage =
            [
                .. antigravityReport.GetProperty("coverage").GetProperty("harnesses").EnumerateArray()
                    .Select(static entry => entry.GetProperty("harness").GetString() ?? string.Empty),
            ];
            Assert.Equal(["claude-code"], remainingCoverage);

            JsonElement allReportAfterSingleClean = bridge.GetReport(null, null, null, 50, 7);
            Assert.Equal(1, allReportAfterSingleClean.GetProperty("sessionCount").GetInt32());
            Assert.Equal(1, allReportAfterSingleClean.GetProperty("harnessCount").GetInt32());
            Assert.Equal("sess-claude", allReportAfterSingleClean.GetProperty("latestSession").GetProperty("sessionId").GetString());
            Assert.Equal("claude-code", Assert.Single(allReportAfterSingleClean.GetProperty("harnesses").EnumerateArray()).GetProperty("harness").GetString());
            Assert.All(
                allReportAfterSingleClean.GetProperty("findings").EnumerateArray(),
                static finding => Assert.Equal("sess-claude", finding.GetProperty("sessionId").GetString()));

            _ = cleanService.CleanDatabase(new CleanScope(All: true));
            Assert.Equal(2, store.Sessions().Count);

            JsonElement reportAfterFullClean = bridge.GetReport(null, null, null, 50, 7);
            Assert.Equal(0, reportAfterFullClean.GetProperty("sessionCount").GetInt32());
            Assert.Equal(0, reportAfterFullClean.GetProperty("findingCount").GetInt32());
            Assert.Equal(0, reportAfterFullClean.GetProperty("harnessCount").GetInt32());
            Assert.Empty(reportAfterFullClean.GetProperty("coverage").GetProperty("harnesses").EnumerateArray());
            Assert.Empty(reportAfterFullClean.GetProperty("findings").EnumerateArray());
            Assert.Empty(reportAfterFullClean.GetProperty("harnesses").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, reportAfterFullClean.GetProperty("latestSession").ValueKind);
        }

        [Fact]
        public void UnprojectedAndMissingTimestampSessionsAreExcludedAcrossWindows()
        {
            InMemoryRelationalStore store = new();
            FakePricingRepository pricing = new();

            WriteRichSession(
                store,
                sessionId: "sess-claude-old",
                harnessId: "claude-code",
                startedAt: FixedNow.AddDays(-3),
                inputTokens: 900,
                cacheReadTokens: 300,
                reportedCostUsd: 0.25);

            store.WriteBatch(new CollectedBatch(
                Source: new CollectedSource("codex", SourceKind.Otlp, "otlp:codex"),
                Sessions:
                [
                    new CollectedSession("sess-codex-unprojected", "codex", "sess-codex-unprojected", Workspace: null, Title: "unprojected", ReportedUsage: []),
                ],
                Records: [],
                Problems: []));

            store.WriteBatch(new CollectedBatch(
                Source: new CollectedSource("pi", SourceKind.Otlp, "otlp:pi"),
                Sessions:
                [
                    new CollectedSession("sess-pi-no-ts", "pi", "sess-pi-no-ts", Workspace: null, Title: "no-timestamp", ReportedUsage: []),
                ],
                Records: [],
                Problems: []));

            _ = new CanonProjectionService(
                store,
                store,
                store,
                new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, pricing)).ProjectAll();

            // Force a projected session row with unparseable timestamps to verify window checks fail closed.
            ((IProjectionStore)store).ReplaceSessionProjection(new StoredSessionProjection(
                SessionId: "sess-pi-no-ts",
                Label: "no-timestamp",
                IsSubagent: false,
                SpanCount: 0,
                StartedAtUtc: "not-a-timestamp",
                EndedAtUtc: "not-a-timestamp",
                ParentSessionId: null,
                AgentName: null,
                IncludedInParent: false,
                RequestSpanIds: [],
                Turns: []));

            KyberBridgeEngine bridge = CreateBridge(store, pricing: pricing);

            JsonElement oneDayReport = bridge.GetReport(null, null, null, 50, 1);
            Assert.Equal(0, oneDayReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal(0, oneDayReport.GetProperty("harnessCount").GetInt32());
            Assert.Empty(oneDayReport.GetProperty("harnesses").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, oneDayReport.GetProperty("latestSession").ValueKind);
            Assert.DoesNotContain(
                oneDayReport.GetProperty("coverage").GetProperty("harnesses").EnumerateArray(),
                static entry => entry.GetProperty("harness").GetString() == "codex");

            JsonElement sevenDayReport = bridge.GetReport(null, null, null, 50, 7);
            Assert.Equal(1, sevenDayReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal("sess-claude-old", sevenDayReport.GetProperty("latestSession").GetProperty("sessionId").GetString());
            Assert.DoesNotContain(
                sevenDayReport.GetProperty("coverage").GetProperty("harnesses").EnumerateArray(),
                static entry => entry.GetProperty("harness").GetString() == "codex");

            JsonElement wideReport = bridge.GetReport(null, null, null, 50, 365_000);
            Assert.Equal(1, wideReport.GetProperty("sessionCount").GetInt32());
            Assert.Equal("sess-claude-old", wideReport.GetProperty("latestSession").GetProperty("sessionId").GetString());
        }

        private static KyberBridgeEngine CreateBridge(
            InMemoryRelationalStore store,
            FakePricingRepository? pricing = null,
            string version = KyberBridgeEngine.DefaultVersion,
            string? storePath = null)
        {
            FakePricingRepository resolvedPricing = pricing ?? new FakePricingRepository();
            return new KyberBridgeEngine(
                store,
                store,
                store,
                resolvedPricing,
                new CanonSessionProjectorEngine(store, store, store, resolvedPricing, new FakeTextTokenizer()),
                new CanonRollupsEngine(store, store, resolvedPricing),
                version: version,
                clock: () => FixedNow,
                storePath: storePath);
        }

        private static void WriteRichSession(
            InMemoryRelationalStore store,
            string sessionId,
            string harnessId,
            DateTimeOffset startedAt,
            long inputTokens,
            long cacheReadTokens,
            double reportedCostUsd,
            string? toolResultText = null,
            bool includeMcpTool = false)
        {
            List<CollectedRecord> records =
            [
                new CollectedRecord(
                    RecordId: $"span-{sessionId}-0",
                    HarnessId: harnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "chat.turn",
                    StartedAt: startedAt,
                    Status: RecordStatus.Ok,
                    SessionKey: sessionId,
                    ParentRecordId: null,
                    TraceId: null,
                    Model: "claude-sonnet-4-6",
                    EndedAt: startedAt.AddMinutes(1),
                    ReportedDurationMs: 1_000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, inputTokens),
                        new CollectedTokenCount(TokenClass.CacheRead, cacheReadTokens),
                        new CollectedTokenCount(TokenClass.CacheWrite, 0),
                        new CollectedTokenCount(TokenClass.Output, 80),
                    ],
                    ReportedCost: new CollectedCost(reportedCostUsd, "USD"),
                    Parts:
                    [
                        new CollectedContentPart(0, ContentBucket.UserPrompt, "Inspect the context report.", null, 120, null, "user", null, null),
                    ],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: includeMcpTool
                        ? [new CollectedToolDefinition(0, "mcp__github__search", "github", 900)]
                        : [],
                    Signals: new CollectedRecordSignals(null, null, null, 200_000),
                    ReportedInputTokens: inputTokens + cacheReadTokens,
                    ReportedOutputTokens: 80),
            ];

            if (!string.IsNullOrEmpty(toolResultText))
            {
                records.Add(new CollectedRecord(
                    RecordId: $"span-{sessionId}-tool",
                    HarnessId: harnessId,
                    Op: RecordOp.ToolInvoke,
                    Kind: RecordKind.Client,
                    Name: "search_docs",
                    StartedAt: startedAt.AddSeconds(15),
                    Status: RecordStatus.Ok,
                    SessionKey: sessionId,
                    ParentRecordId: $"span-{sessionId}-0",
                    TraceId: null,
                    Model: "claude-sonnet-4-6",
                    EndedAt: startedAt.AddSeconds(16),
                    ReportedDurationMs: 250,
                    Tokens: [],
                    ReportedCost: null,
                    Parts:
                    [
                        new CollectedContentPart(0, ContentBucket.ToolResultContent, toolResultText, null, Math.Max(10, toolResultText.Length / 4), null, null, null, null),
                    ],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, 200_000),
                    ReportedInputTokens: null,
                    ReportedOutputTokens: null));
            }

            store.WriteBatch(new CollectedBatch(
                Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
                Sessions:
                [
                    new CollectedSession(
                        SessionKey: sessionId,
                        HarnessId: harnessId,
                        NativeSessionId: sessionId,
                        Workspace: new CollectedWorkspace("/tmp/kyber-weave", "kyber-weave", "main"),
                        Title: sessionId,
                        ReportedUsage: []),
                ],
                Records: records,
                Problems: []));
        }
    }
}
