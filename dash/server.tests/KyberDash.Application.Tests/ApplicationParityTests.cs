using System.Globalization;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// Engine-level parity: the bridge, projector, rollups, refresh, and job scheduler driven through the
/// real implementations over an in-memory store, plus process-free service contracts.
/// </summary>
public class ApplicationParityTests
{
    [Fact]
    public void ProjectorBuildsSessionSummariesAndFindingsFromStoredRecords()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonProjectionService projection = new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));

        WriteSession(
            store,
            "sess-a",
            "claude-code",
            [
                Turn(
                    "span-a-1",
                    "sess-a",
                    0,
                    harnessId: "claude-code",
                    inputTokens: 400,
                    outputTokens: 60,
                    parts: [TextPart(0, ContentBucket.UserPrompt, "Fix the bug")],
                    model: "claude-sonnet-4-6",
                    declaredContextWindow: 200_000),
            ]);
        WriteSession(
            store,
            "sess-b",
            "codex",
            [
                Turn(
                    "span-b-1",
                    "sess-b",
                    1,
                    harnessId: "codex",
                    inputTokens: 1_500,
                    outputTokens: 120,
                    parts: [TextPart(0, ContentBucket.UserPrompt, "Build feature B")],
                    model: "gpt-5.3-codex",
                    declaredContextWindow: 200_000),
                Turn(
                    "span-b-tool",
                    "sess-b",
                    2,
                    harnessId: "codex",
                    parentSpanId: "span-b-1",
                    name: "search_docs",
                    op: RecordOp.ToolInvoke,
                    parts: [new CollectedContentPart(0, ContentBucket.ToolResultContent, new string('z', 15_000), null, 3_750, null, null, null, null)],
                    model: "gpt-5.3-codex",
                    declaredContextWindow: 200_000),
            ]);

        (ProjectionPassReport pass, IReadOnlyList<HarnessRollupRow> rollups) = projection.ProjectAll();

        Assert.Equal(2, pass.Built);
        Assert.Equal(2, rollups.Count);
        Assert.Contains(rollups, static r => r.Harness == "claude-code" && r.SampleCount == 1);
        Assert.Contains(
            ((IProjectionStore)store).ListFindings(sessionId: "sess-b"),
            static finding => finding.DetectorId == "oversized-tool-result");

        StoredSessionProjection? sessionA = ((IProjectionStore)store).GetSessionProjection("sess-a");
        Assert.NotNull(sessionA);
        Assert.Equal(1, sessionA.SpanCount);
        Assert.NotNull(sessionA.Context);
        Assert.Equal(400, Assert.Single(sessionA.Context.Turns, static turn => turn.Position == 0).InputTokens);
    }

    [Fact]
    public void ProjectionShortCircuitsWhenTheStoreIsAlreadyCurrent()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonProjectionService projection = new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));

        WriteSession(store, "sess-a", "droid", [Turn("span-a-1", "sess-a", 0)]);
        ProjectionPassReport first = projection.ProjectIncremental();
        Assert.Equal(1, first.Built);

        ProjectionPassReport second = projection.ProjectIncremental();
        Assert.Equal(0, second.Built);
        Assert.Equal(1, second.Skipped);
        Assert.Empty(second.RebuiltKeys);
    }

    [Fact]
    public void UsageIngestionProjectionQueryPipelineAnswersEveryDashboardRead()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());
        CanonProjectionService projection = new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));
        KyberBridgeEngine bridge = new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));
        DashboardQueryService query = new(bridge);

        OtlpSpan span = new(
            TraceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            SpanId: "1111111111111111",
            ParentSpanId: null,
            Name: "chat",
            Kind: "client",
            StartTimeUnixNano: "1760000000000000000",
            EndTimeUnixNano: "1760000000100000000",
            Timestamp: "2026-11-14T10:00:00.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("ok"),
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Json(("sess-copilot")),
                ["gen_ai.usage.input_tokens"] = Json(1000),
                ["gen_ai.usage.output_tokens"] = Json(250),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["service.name"] = Json("copilot-host") },
            Scope: new OtlpScope("test", "1"));

        IngestOutcome outcome = ingestion.IngestSpans([span]);
        Assert.Equal(1, outcome.Accepted);

        (ProjectionPassReport pass, IReadOnlyList<HarnessRollupRow> rollups) = projection.ProjectAll();
        Assert.Equal(1, pass.Built);
        Assert.Single(rollups);
        Assert.Contains(rollups, static r => r.Harness == "copilot" && r.SampleCount == 1);

        Assert.Contains("sess-copilot", query.GetSessionPage().GetRawText(), StringComparison.Ordinal);
        Assert.NotNull(query.GetSessionSummary("sess-copilot"));
        Assert.NotNull(query.GetSessionCharts("sess-copilot"));
        Assert.Null(query.GetSessionCharts("missing"));
        Assert.NotNull(store.SessionSummaryFigures("sess-copilot"));
        Assert.NotNull(store.SessionTurnFigures("sess-copilot"));
        Assert.NotNull(store.TurnFigures("sess-copilot", 0));
        Assert.Null(store.TurnFigures("sess-copilot", 1));
        Assert.Null(store.SessionSummaryFigures("missing"));
        Assert.Null(store.SessionTurnFigures("missing"));
        string harnessPayload = query.GetHarnesses().GetRawText();
        Assert.Contains("\"copilot\"", harnessPayload, StringComparison.Ordinal);
        Assert.Contains("\"sampleCount\"", harnessPayload, StringComparison.Ordinal);
        string runsPayload = query.GetRuns().GetRawText();
        Assert.Contains("derived:copilot:", runsPayload, StringComparison.Ordinal);
        Assert.Equal(1, query.GetMeta().GetProperty("apiVersion").GetInt32());
        Assert.Equal(KyberBridgeEngine.DefaultVersion, query.GetVersion().GetProperty("version").GetString());
        Assert.True(query.GetCoverage().TryGetProperty("harnesses", out _));
        Assert.True(query.GetQuarantinePage(null, 10, 0, 1).TryGetProperty("total", out _));
        Assert.Equal(0, query.GetProblemsPage(null, 10, 0, 1).GetProperty("total").GetInt32());
        Assert.True(query.GetFindings(sessionId: "sess-copilot").TryGetProperty("findings", out _));
        Assert.True(query.GetReport().TryGetProperty("sessionCount", out _));
        Assert.True(query.GetTimeline("sess-copilot").TryGetProperty("sessions", out _));
        Assert.Equal(JsonValueKind.Array, query.GetPredictions().ValueKind);
        Assert.Equal(0, query.GetCalibration().GetProperty("totalPredictions").GetInt32());
        Assert.True(query.GetCompare().TryGetProperty("harnesses", out _));
        Assert.True(query.GetModelCatalog().TryGetProperty("source", out _));
    }

    [Fact]
    public void SessionContentForASpanAnswersFlatPartsAndNothingForASpanThatCapturedNone()
    {
        InMemoryRelationalStore store = new();
        WriteSession(store, "sess-content", "droid",
            [
                Turn("span-empty", "sess-content", 0),
                Turn(
                    "span-parts",
                    "sess-content",
                    1,
                    parts: [new CollectedContentPart(0, ContentBucket.UserPrompt, "hello", Withheld: null, ReportedTokens: 3, Server: null, Role: null, Resident: null, OriginalUtf8BytesWhenTruncated: null)]),
            ]);
        KyberBridgeEngine bridge = Bridge(store);

        JsonElement none = bridge.GetSessionContent("sess-content", spanId: "span-empty", full: true)!.Value;
        Assert.Equal(0, none.GetProperty("parts").GetArrayLength());

        JsonElement flat = bridge.GetSessionContent("sess-content", spanId: "span-parts", full: true)!.Value;
        JsonElement entry = Assert.Single(flat.GetProperty("parts").EnumerateArray().ToList());
        Assert.Equal("span-parts", entry.GetProperty("spanId").GetString());
        Assert.Equal("user_prompt", entry.GetProperty("part").GetString());
        Assert.Equal("hello", entry.GetProperty("text").GetString());
        Assert.Equal(3, entry.GetProperty("tokens").GetInt64());

        JsonElement partFiltered = bridge.GetSessionContent("sess-content", spanId: "span-parts", part: "user_prompt", full: true)!.Value;
        JsonElement filteredEntry = Assert.Single(partFiltered.GetProperty("parts").EnumerateArray().ToList());
        Assert.Equal("hello", filteredEntry.GetProperty("text").GetString());

        // The content view reads only the span it answers for: five bytes of text, through the
        // scoped content read, with no broad record or part read on the way.
        store.ClearReadLog();
        JsonElement scoped = bridge.GetSessionContent("sess-content", spanId: "span-parts", full: true)!.Value;
        Assert.Single(scoped.GetProperty("parts").EnumerateArray());
        Assert.Equal(5, store.MaterializedContentBytes);
        Assert.Contains(nameof(InMemoryRelationalStore.TurnContentParts), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.SessionRecordsForRebuild), store.ReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance), store.ReadLog);
    }

    [Fact]
    public void PredictionRecordingValidatesTheBodyAndStoresTheRow()
    {
        InMemoryRelationalStore store = new();
        KyberBridgeEngine bridge = Bridge(store);

        Assert.False(bridge.TryRecordPrediction(Json("not-an-object"), out PredictionRow? rejected, out string? error));
        Assert.Null(rejected);
        Assert.False(string.IsNullOrEmpty(error));

        Assert.True(bridge.TryRecordPrediction(
            Json(new { findingId = "finding-1", runId = "run-1", predictedWasteTokens = 500, confidence = 0.6 }),
            out PredictionRow? created,
            out string? createdError));
        Assert.Null(createdError);
        Assert.NotNull(created);
        Assert.Equal("finding-1", created.FindingId);
        Assert.Equal(500, created.PredictedWasteTokens);
        Assert.Single(((IProjectionStore)store).ListPredictions("run-1"));
        Assert.Equal(1, bridge.GetCalibration().GetProperty("totalPredictions").GetInt32());
    }

    [Fact]
    public void PurgeExpiredContentStampsRetentionMarksWithoutClearingContent()
    {
        InMemoryRelationalStore store = new();
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());

        PurgeExpiredContentResult result = ingestion.PurgeExpiredContent(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(0, result.Purged);
        Assert.Equal("2026-11-01T00:00:00.000Z", result.CutoffUtc);
        Assert.Equal(CanonRefreshEngine.ContentRetentionDays, store.GetContentRetentionDays());
        Assert.Equal("2026-11-01T00:00:00.000Z", store.GetContentPurgeMarkUtc());
    }

    [Fact]
    public async Task JobSchedulerDeclinesRefreshWhilePausedButAlwaysImportsHistory()
    {
        InMemoryRelationalStore store = new();
        FakeSettingsRepository settings = new();
        FakeServerStateRepository serverState = new();
        FakePricingRepository pricing = new();
        CanonProjectionService projection = new(store, store, store, new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()), new CanonRollupsEngine(store, store, pricing));
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());
        DashOptions options = new() { StateDirectory = serverState.DefaultStateDirectory };

        using JobSchedulerService jobs = new(store, store, projection, ingestion, settings, serverState, options, clock: () => new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));

        JobOutcome idle = await jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", idle.Outcome);
        Assert.Equal("idle", jobs.GetStatus().State);
        Assert.NotNull(jobs.GetStatus().LastSuccessAt);
        Assert.Equal("2026-11-15T00:05:00.000Z", jobs.GetStatus().NextDueAt);

        settings.WriteBooleanSetting(SettingsService.JobsPausedKey, true);
        JobOutcome paused = await jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal("declined", paused.Outcome);
        Assert.True(jobs.GetStatus().Paused);

        JobOutcome imported = await jobs.TriggerImportHistoryAsync(TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", imported.Outcome);
    }

    [Fact]
    public async Task JobSchedulerReportsHostedElsewhereWhenAnotherProcessHoldsTheLease()
    {
        InMemoryRelationalStore store = new();
        FakeSettingsRepository settings = new();
        FakeServerStateRepository serverState = new() { JobsLeaseAvailable = false };
        FakePricingRepository pricing = new();
        CanonProjectionService projection = new(store, store, store, new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()), new CanonRollupsEngine(store, store, pricing));
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());

        using JobSchedulerService jobs = new(store, store, projection, ingestion, settings, serverState, new DashOptions { StateDirectory = serverState.DefaultStateDirectory });

        JobOutcome outcome = await jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hosted-elsewhere", outcome.Outcome);
        Assert.True(jobs.GetStatus().HostedElsewhere);
        Assert.Equal("running-elsewhere", jobs.GetStatus().State);
    }

    [Fact]
    public async Task JobSchedulerSurfacesFailuresAsAFailedOutcome()
    {
        FakeSettingsRepository settings = new();
        FakeServerStateRepository serverState = new();
        FailingProjectionService projection = new();
        InMemoryRelationalStore store = new();
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());

        using JobSchedulerService jobs = new(store, store, projection, ingestion, settings, serverState, new DashOptions { StateDirectory = serverState.DefaultStateDirectory });

        JobOutcome outcome = await jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("failed", outcome.Outcome);
        Assert.Equal("projection exploded", outcome.Reason);
        Assert.Equal("failed", jobs.GetStatus().State);
        Assert.Equal("projection exploded", jobs.GetStatus().LastFailure);
    }

    [Fact]
    public async Task StartAcquiresTheLeaseAndStopReleasesIt()
    {
        InMemoryRelationalStore store = new();
        FakeServerStateRepository serverState = new();
        FakePricingRepository pricing = new();
        CanonProjectionService projection = new(store, store, store, new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()), new CanonRollupsEngine(store, store, pricing));
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());

        JobSchedulerService jobs = new(store, store, projection, ingestion, new FakeSettingsRepository(), serverState, new DashOptions { StateDirectory = serverState.DefaultStateDirectory });
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

    [Fact]
    public void CaptureStatusReportsReceiverStateConsentAndPluginLocations()
    {
        FakeSettingsRepository settings = new();
        FakeServerStateRepository serverState = new();
        CaptureService capture = new(settings, new InMemoryRelationalStore(), serverState);
        string stateDir = serverState.DefaultStateDirectory;

        JsonElement initial = capture.GetCaptureStatus(stateDir);
        Assert.False(initial.GetProperty("receiverHosted").GetBoolean());
        Assert.False(initial.GetProperty("contentCapture").GetBoolean());
        Assert.Equal("the configured collector port", initial.GetProperty("otlpEndpoint").GetString());

        JsonElement updated = capture.ConfigureCapture(
            Json(new { receiverHosted = true, contentCapture = true }),
            stateDir);

        Assert.True(updated.GetProperty("receiverHosted").GetBoolean());
        Assert.True(updated.GetProperty("contentCapture").GetBoolean());
        Assert.True(settings.ReadFactoryContentConsent(stateDir));
    }

    [Fact]
    public void CaptureRejectsNonBooleanConsentValues()
    {
        CaptureService capture = new(new FakeSettingsRepository(), new InMemoryRelationalStore(), new FakeServerStateRepository());

        DashValidationException exception = Assert.Throws<DashValidationException>(
            () => capture.ConfigureCapture(Json(new { contentCapture = "yes" }), Path.GetTempPath()));

        Assert.Contains("boolean", exception.Message, StringComparison.Ordinal);
        Assert.Throws<DashValidationException>(() => capture.ConfigureCapture(Json("[]"), Path.GetTempPath()));
    }

    [Fact]
    public void CapturePluginInstallRefusesUnknownHarnessesAndWrongRoots()
    {
        CaptureService capture = new(new FakeSettingsRepository(), new InMemoryRelationalStore(), new FakeServerStateRepository());

        (int unknownExitCode, string unknownOutput) = capture.InstallPlugin("nope", ifInstalled: false, configDir: null);
        Assert.Equal(2, unknownExitCode);
        Assert.Contains("Expected one of: opencode, factory", unknownOutput, StringComparison.Ordinal);

        (int factoryExitCode, string factoryOutput) = capture.InstallPlugin(CaptureService.FactoryHarness, ifInstalled: false, configDir: "/tmp/x");
        Assert.Equal(2, factoryExitCode);
        Assert.Contains("--config-dir selects the OpenCode config root", factoryOutput, StringComparison.Ordinal);

        using TempDirectory directory = new();
        (int opencodeExitCode, string opencodeOutput) = capture.InstallPlugin(
            CaptureService.OpencodeHarness,
            ifInstalled: true,
            configDir: directory.Path);
        Assert.Equal(0, opencodeExitCode);
        Assert.Contains("skipped-not-installed", opencodeOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryConsentIsWrittenWhereTheOperatorCanFindIt()
    {
        FakeSettingsRepository settings = new();
        FakeServerStateRepository serverState = new();
        CaptureService capture = new(settings, new InMemoryRelationalStore(), serverState);

        (int exitCode, string output) = capture.SetFactoryConsent(true);

        Assert.Equal(0, exitCode);
        Assert.Contains("content on", output, StringComparison.Ordinal);
        Assert.True(settings.ReadFactoryContentConsent(serverState.DefaultStateDirectory));
    }

    [Fact]
    public async Task AntigravityStatusLineRecordsOnlyValidObjectPayloads()
    {
        FakeSettingsRepository settings = new();
        CaptureService capture = new(settings, new InMemoryRelationalStore(), new FakeServerStateRepository());

        await capture.RecordAntigravityStatusLineAsync("   ", cancellationToken: TestContext.Current.CancellationToken);
        await capture.RecordAntigravityStatusLineAsync("not json", cancellationToken: TestContext.Current.CancellationToken);
        await capture.RecordAntigravityStatusLineAsync("[1,2,3]", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(settings.StatusLines);

        await capture.RecordAntigravityStatusLineAsync("{\"model\":\"claude\"}", cancellationToken: TestContext.Current.CancellationToken);
        JsonElement recorded = Assert.Single(settings.StatusLines);
        Assert.Equal("claude", recorded.GetProperty("model").GetString());
    }

    [Fact]
    public async Task EngineEnsureReportsNotRunningWithoutTouchingTheEngine()
    {
        FakeServerStateRepository serverState = new();
        EngineLifecycleService lifecycle = new(serverState, new DashOptions());

        EngineEnsureReport report = await lifecycle
            .EnsureRunningEngineMatchesVersionAsync("0.21.0", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Equal("engine: not-running", report.Message);
        Assert.Equal(0, serverState.ShutdownRequests);
    }

    [Fact]
    public async Task EngineEnsureAttachesWhenTheLiveVersionMatches()
    {
        FakeServerStateRepository serverState = new()
        {
            ProbeResult = (EngineProbeState.Running, new EngineDescriptor(42, "kyberdash-api", "0.21.0", "2026-11-15T00:00:00.000Z")),
        };
        EngineLifecycleService lifecycle = new(serverState, new DashOptions());

        EngineEnsureReport report = await lifecycle
            .EnsureRunningEngineMatchesVersionAsync("0.21.0", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Equal("engine: attached 0.21.0", report.Message);
        Assert.Equal(0, serverState.ShutdownRequests);
    }

    [Fact]
    public async Task EngineEnsureRefusesAnUnresponsiveHolder()
    {
        FakeServerStateRepository serverState = new()
        {
            ProbeResult = (EngineProbeState.Unresponsive, null),
        };
        EngineLifecycleService lifecycle = new(serverState, new DashOptions());

        EngineEnsureReport report = await lifecycle
            .EnsureRunningEngineMatchesVersionAsync("0.21.0", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Contains("unresponsive", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineEnsureReportsARefusedShutdownAsAFailure()
    {
        FakeServerStateRepository serverState = new()
        {
            ProbeResult = (EngineProbeState.Running, new EngineDescriptor(42, "kyberdash-api", "0.20.0", "2026-11-15T00:00:00.000Z")),
            ShutdownAccepted = false,
        };
        EngineLifecycleService lifecycle = new(serverState, new DashOptions());

        EngineEnsureReport report = await lifecycle
            .EnsureRunningEngineMatchesVersionAsync("0.21.0", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Equal("engine: failed the live engine refused the shutdown request", report.Message);
        Assert.Equal(1, serverState.ShutdownRequests);
    }

    [Fact]
    public async Task ServerInfoPublicationAndResolutionUseTheStateRepository()
    {
        FakeServerStateRepository serverState = new();
        EngineLifecycleService lifecycle = new(serverState, new DashOptions());
        const string StateDir = "/tmp/kyberdash-engine-test";

        Assert.Null(await lifecycle.ResolveLiveServerUriAsync(StateDir, TestContext.Current.CancellationToken));
        Assert.Equal(serverState.DefaultStateDirectory, lifecycle.DefaultStateDirectory);

        lifecycle.PublishServerInfo(StateDir, new ServerInfo(4747, Environment.ProcessId, "2026-11-15T00:00:00.000Z"));

        Uri? resolved = await lifecycle.ResolveLiveServerUriAsync(StateDir, TestContext.Current.CancellationToken);
        Assert.NotNull(resolved);
        Assert.Equal(4747, resolved.Port);
        Assert.Equal("127.0.0.1", resolved.Host);

        lifecycle.ClearServerInfo(StateDir);
        Assert.Null(serverState.ReadServerInfo(StateDir));
    }

    [Fact]
    public async Task LiveServerResolutionFallsBackToTheEngineDescriptorUrl()
    {
        FakeServerStateRepository serverState = new()
        {
            ProbeResult = (EngineProbeState.Running, new EngineDescriptor(
                42,
                "kyberdash-api",
                "0.21.0",
                "2026-11-15T00:00:00.000Z",
                new Uri("http://127.0.0.1:5005"))),
        };
        EngineLifecycleService lifecycle = new(serverState, new DashOptions());

        Uri? resolved = await lifecycle.ResolveLiveServerUriAsync("/tmp/kyberdash-engine-test", TestContext.Current.CancellationToken);

        Assert.Equal(5005, resolved?.Port);
    }

    [Fact]
    public async Task E2eSeederRefusesTheOperatorsOwnStoreAndUnknownWorlds()
    {
        string canonPath = CanonDbPath.Resolve(null, null, null);
        using TempDirectory directory = new();
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        CanonProjectionService projection = new(store, store, store, new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()), new CanonRollupsEngine(store, store, pricing));
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());
        E2eSeederService seeder = new(store, store, store, ingestion, projection);

        Assert.Equal(E2eSeederService.RefusedExitCode, seeder.SeedWorld(E2eSeederService.HarnessCollectionWorld, canonPath, directory.Path));
        Assert.Equal(E2eSeederService.RefusedExitCode, seeder.SeedWorld("no-such-world", directory.Combine("world.db"), directory.Path));
        await Task.CompletedTask;
    }

    [Fact]
    public void WebAssetServicePrefersTheRequestOverrideThenTheConfiguredDirectory()
    {
        MockWebAssetRepository assets = new();
        WebAssetService service = new(assets, new DashOptions { DashboardDir = "/tmp/configured" });

        _ = service.TryResolveAsset("/index.html");
        _ = service.TryResolveAsset("/main.js", "/tmp/override");
        _ = new WebAssetService(assets, new DashOptions()).TryResolveAsset("/index.html");

        Assert.Equal(["/tmp/configured", "/tmp/override", null], assets.Overrides);
    }

    [Fact]
    public void WebAssetServiceRequiresAPath()
    {
        WebAssetService service = new(new MockWebAssetRepository(), new DashOptions());

        Assert.Throws<ArgumentException>(() => service.TryResolveAsset("  "));
    }

    [Fact]
    public void TranscriptIngestionDiscoversSourcesWithinTheHistoryWindowAndPurges()
    {
        InMemoryRelationalStore store = new();
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());

        RefreshReport report = ingestion.RefreshHarnessSources(historyWeeks: 3);

        Assert.Equal(3, report.HistoryWeeks);
        Assert.Equal(0, report.ExitCode);

        PurgeExpiredContentResult purge = ingestion.PurgeExpiredContent(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, purge.Purged);
        Assert.Equal("2026-11-01T00:00:00.000Z", purge.CutoffUtc);
        Assert.Equal(CanonRefreshEngine.ContentRetentionDays, store.GetContentRetentionDays());
        Assert.Equal("2026-11-01T00:00:00.000Z", store.GetContentPurgeMarkUtc());
        Assert.Equal(
            CanonRefreshEngine.ContentRetentionFloor(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            TranscriptIngestionService.RetentionFloor(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IngestSpansWithNoSpansIsANoOp()
    {
        InMemoryRelationalStore store = new();
        TranscriptIngestionService ingestion = new(store, store, new HarnessSourceRegistry());

        IngestOutcome outcome = ingestion.IngestSpans([]);

        Assert.Equal(new IngestOutcome(0, 0, 0), outcome);
        Assert.Equal(0, store.CountRecords());
    }

    private static KyberBridgeEngine Bridge(InMemoryRelationalStore store)
    {
        FakePricingRepository pricing = new();
        return new KyberBridgeEngine(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));
    }

    private static void WriteSession(InMemoryRelationalStore store, string sessionKey, string harnessId, IReadOnlyList<CollectedRecord> records)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        IReadOnlyList<CollectedContentPart>? parts = null,
        string harnessId = "droid",
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        string? parentSpanId = null,
        string name = "chat.turn",
        RecordOp op = RecordOp.LlmInvoke,
        string? model = null,
        long? declaredContextWindow = null)
    {
        DateTimeOffset startedAt = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero).AddMinutes(minuteOffset);
        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: op,
            Kind: RecordKind.Client,
            Name: name,
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: parentSpanId,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
                new CollectedTokenCount(TokenClass.CacheWrite, 0),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ],
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: declaredContextWindow),
            ReportedInputTokens: inputTokens + cacheRead,
            ReportedOutputTokens: outputTokens);
    }

    private static CollectedContentPart TextPart(int position, ContentBucket bucket, string text) =>
        new(
            Position: position,
            Bucket: bucket,
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Projection service that always fails, for job-host failure reporting.</summary>
    private class FailingProjectionService : ICanonProjectionService
    {
        public ProjectionPassReport ProjectIncremental() => throw new StoredDataException("projection exploded");

        public IReadOnlyList<HarnessRollupRow> ProjectRunsAndHarnesses() => throw new StoredDataException("projection exploded");

        public (ProjectionPassReport Projection, IReadOnlyList<HarnessRollupRow> Rollups) ProjectAll() =>
            throw new StoredDataException("projection exploded");
    }

    /// <summary>Web asset repository recording the override directory each call carried.</summary>
    private class MockWebAssetRepository : IWebAssetRepository
    {
        public List<string?> Overrides { get; } = [];

        public WebAssetDocument? TryResolveAsset(string requestPath, string? overrideDir = null)
        {
            Overrides.Add(overrideDir);
            return null;
        }
    }
}
