using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies relational ingestion routing across <see cref="TranscriptIngestionService"/>,
/// <see cref="CanonIngestPipeline"/>, <c>IngestSpanNormalizer</c>, and DI registration.
/// </summary>
public class RelationalIngestTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "relational-ingest-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies <see cref="TranscriptIngestionService.IngestSpans"/> writes records and problems
    /// exclusively through <see cref="IRelationalStoreWriter"/> and quarantined spans through
    /// <see cref="IOperationalStore"/> with zero raw payload persisted.
    /// </summary>
    [Fact]
    public void IngestSpans_WritesOnlyViaRelationalWriterAndOperationalStore_WithZeroRawPayload()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        TranscriptIngestionService service = new(spyWriter, store, new HarnessSourceRegistry());

        OtlpSpan copilotTurn = MakeOtlpSpan(
            spanId: "1111111111111111",
            traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            serviceName: "copilot-host",
            name: "copilot.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element("sess-copilot"),
                ["gen_ai.request.model"] = Element("gpt-5"),
                ["gen_ai.system_instructions"] = Element("Be concise."),
                ["gen_ai.input.messages"] = Element("[{\"role\":\"user\",\"content\":\"hello\"}]"),
                ["gen_ai.usage.input_tokens"] = Element(1000),
                ["gen_ai.usage.cache_read_input_tokens"] = Element(600),
                ["gen_ai.usage.cache_creation_input_tokens"] = Element(100),
                ["gen_ai.usage.output_tokens"] = Element(200),
            });

        OtlpSpan piTurn = MakeOtlpSpan(
            spanId: "2222222222222222",
            traceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            serviceName: "pi-host",
            name: "pi.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.skills"] = Element("debugging"),
                ["gen_ai.usage.input_tokens"] = Element(50),
                ["gen_ai.usage.cache_read_input_tokens"] = Element(200),
                ["gen_ai.usage.output_tokens"] = Element(30),
            });

        OtlpSpan badClaudeOutput = MakeOtlpSpan(
            spanId: "3333333333333333",
            traceId: "cccccccccccccccccccccccccccccccc",
            serviceName: "claude-host",
            name: "claude.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.session_id"] = Element("sess-bad"),
                ["gen_ai.usage.input_tokens"] = Element(10),
                ["gen_ai.usage.output_tokens"] = Element(-5),
            });

        OtlpSpan unknownSpan = MakeOtlpSpan(
            spanId: "4444444444444444",
            traceId: "dddddddddddddddddddddddddddddddd",
            serviceName: "unknown-host",
            name: "mystery.event",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["mystery.event_type"] = Element("unrecognized"),
                ["mystery.secret_payload"] = Element("do-not-persist"),
            });

        IngestOutcome outcome = service.IngestSpans([copilotTurn, piTurn, badClaudeOutput, unknownSpan]);

        Assert.Equal(2, outcome.Accepted);
        Assert.Equal(1, outcome.Quarantined);
        Assert.Equal(1, outcome.Rejected);
        Assert.NotEmpty(spyWriter.WriteBatchCalls);
        Assert.Equal(2, store.CountRecords());

        CollectedRecord? copilotRecord = store.FindRecord("1111111111111111");
        Assert.NotNull(copilotRecord);
        Assert.Equal("copilot", copilotRecord.HarnessId);
        Assert.Equal("sess-copilot", copilotRecord.SessionKey);
        Assert.Equal("gpt-5", copilotRecord.Model);
        Assert.Contains(copilotRecord.Parts, static p => p.Bucket == ContentBucket.SystemPrompt && p.PlainText == "Be concise.");
        Assert.Contains(copilotRecord.Parts, static p => p.Bucket == ContentBucket.ConversationHistory && p.PlainText is null && p.Withheld is not null);

        CollectedRecord? piRecord = store.FindRecord("2222222222222222");
        Assert.NotNull(piRecord);
        Assert.Equal("pi", piRecord.HarnessId);

        Assert.Null(store.FindRecord("3333333333333333"));
        Assert.Equal(1, store.CountIngestProblems());
        StoredIngestProblem problem = Assert.Single(store.ListIngestProblems());
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Problem.Code);
        Assert.Equal("claude-code", problem.Problem.HarnessId);
        Assert.DoesNotContain("do-not-persist", problem.Problem.Message, StringComparison.Ordinal);

        Assert.Null(store.FindRecord("4444444444444444"));
        Assert.Empty(store.FindRecordParts("4444444444444444"));
        Assert.Equal(1, store.CountQuarantinedSpans());
        StoredQuarantinedSpan? quarantined = store.GetQuarantinedSpan("4444444444444444");
        Assert.NotNull(quarantined);
        Assert.Equal("unclaimed", quarantined.Reason);
        Assert.Equal(["mystery"], quarantined.ObservedNamespaces);
    }

    /// <summary>
    /// An OpenCode replay keeps the session label stamped on the request, each tool invocation,
    /// and a harness-reported cost of zero through projection. Those are the figures the
    /// harness-collection page renders for the consent-on session.
    /// </summary>
    [Fact]
    public void IngestBatch_OpenCodeReplay_PreservesLabelToolCallsAndZeroHarnessCost()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionId = "ses-replay";

        OtlpSpan readTool = OpenCodeSpan(
            spanId: "aaaaaaaaaaaaaaaa",
            name: "opencode.tool",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("tool"),
                ["opencode.session.id"] = Element(sessionId),
                ["opencode.tool.name"] = Element("read"),
                ["opencode.tool.call_id"] = Element("call-read"),
            });
        OtlpSpan skillTool = OpenCodeSpan(
            spanId: "bbbbbbbbbbbbbbbb",
            name: "opencode.tool",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("tool"),
                ["opencode.session.id"] = Element(sessionId),
                ["opencode.tool.name"] = Element("skill"),
                ["opencode.tool.call_id"] = Element("call-skill"),
            });
        OtlpSpan step = OpenCodeSpan(
            spanId: "cccccccccccccccc",
            name: "opencode.step",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("step"),
                ["opencode.session.id"] = Element(sessionId),
                ["opencode.usage.input"] = Element(10),
                ["opencode.usage.output"] = Element(2),
                ["opencode.cost"] = Element(0.0),
            });
        OtlpSpan request = OpenCodeSpan(
            spanId: "dddddddddddddddd",
            name: "opencode.request",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("3"),
                ["opencode.span.kind"] = Element("request"),
                ["opencode.session.id"] = Element(sessionId),
                ["kyber.session.label"] = Element("kw-probe runA shared-service"),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([readTool, skillTool, step, request], store, store);
        Assert.Equal(4, outcome.Accepted);
        Assert.Equal(0, outcome.Quarantined);
        Assert.Equal(0, outcome.Rejected);

        ProjectionPassReport report = new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()).ProjectIncremental();
        Assert.Equal(1, report.Built);

        StoredSessionProjection? projection = store.GetSessionProjection(sessionId);
        Assert.NotNull(projection);
        Assert.Equal("kw-probe runA shared-service", projection.Label);

        SessionSummaryFigure? summary = store.SessionSummaryFigures(sessionId, pricing.LoadPricingTables());
        Assert.NotNull(summary);
        Assert.Equal(2, summary.ToolCalls);
        Assert.NotNull(summary.ReportedCost);
        Assert.Equal(CostBasis.Harness, summary.ReportedCost.Basis);
        Assert.Equal(0d, summary.ReportedCost.Value);
        SessionToolFigureSet toolFigures = store.SessionToolFigures(sessionId);
        Assert.Contains(toolFigures.Tools, static tool => tool.Name == "read" && tool.Invocations == 1);
        Assert.Contains(toolFigures.Tools, static tool => tool.Name == "skill" && tool.Invocations == 1);

        KyberBridgeEngine bridge = new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));
        JsonElement tools = bridge.GetSessionTools(sessionId)!.Value;
        Assert.Contains(
            tools.GetProperty("tools").EnumerateArray(),
            static tool => tool.GetProperty("name").GetString() == "read" && tool.GetProperty("invocations").GetInt32() == 1);
        JsonElement summaryPayload = bridge.GetSessionSummary(sessionId)!.Value;
        Assert.Equal(0d, summaryPayload.GetProperty("summary").GetProperty("cost_reported").GetProperty("value").GetDouble());
    }

    /// <summary>
    /// A Factory turn that declares token usage not measurable keeps the session input absent,
    /// so the overview renders an em dash rather than a measured zero.
    /// </summary>
    [Fact]
    public void IngestBatch_FactoryTurnWithoutTokenCounters_LeavesTotalInputUnmeasured()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionId = "factory-replay-content-off";

        OtlpSpan turn = MakeOtlpSpan(
            spanId: "aaaaaaaaaaaaaaaa",
            traceId: "ffffffffffffffffffffffffffffffff",
            serviceName: "kyberdash-factory-plugin",
            name: "factory.turn",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["factory.contract_version"] = Element(CanonAdapters.DroidContractVersion),
                ["factory.session.id"] = Element(sessionId),
                ["factory.model"] = Element("gpt-5-codex"),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([turn], store, store);
        Assert.Equal(new IngestOutcome(1, 0, 0), outcome);

        CollectedRecord? stored = store.FindRecord("aaaaaaaaaaaaaaaa");
        Assert.NotNull(stored);
        Assert.DoesNotContain(stored.Tokens, static token => token.TokenClass == TokenClass.Input);
        Assert.Null(stored.ReportedInputTokens);

        _ = new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()).ProjectIncremental();
        SessionSummaryFigure? turnTotals = store.SessionSummaryFigures(sessionId, pricing.LoadPricingTables());
        Assert.NotNull(turnTotals);
        Assert.Null(turnTotals.TotalInput);

        KyberBridgeEngine bridge = new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));
        JsonElement summary = bridge.GetSessionSummary(sessionId)!.Value.GetProperty("summary");
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("total_input").ValueKind);

        JsonElement listed = Assert.Single(
            bridge.GetSessionPage().GetProperty("sessions").EnumerateArray(),
            row => row.GetProperty("session_id").GetString() == sessionId);
        JsonElement declared = listed.GetProperty("total_input");
        Assert.Equal("not_measurable", declared.GetProperty("availability").GetString());
        string? reason = declared.GetProperty("reason").GetString();
        Assert.Contains("droid", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token", reason, StringComparison.OrdinalIgnoreCase);

        JsonElement timeline = bridge.GetSessionTimeline(sessionId)!.Value.GetProperty("nodes");
        Assert.Contains(timeline.EnumerateArray(), static node => node.GetProperty("name").GetString() == "factory.turn");
    }

    /// <summary>
    /// A Factory turn captured with content consent keeps the prompt in the store as a content
    /// part, but the content-free sections publish none: the timeline section's span attributes
    /// and read carry no prompt text, and the prompt surfaces only through the requests section's
    /// page-scoped preview read and the scoped content read.
    /// </summary>
    [Fact]
    public void IngestBatch_FactoryTurnWithConsent_KeepsPromptOutOfTheContentFreeSections()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionId = "factory-replay-content-on";
        const string prompt = "the recorded factory prompt that must not travel without consent";

        OtlpSpan turn = MakeOtlpSpan(
            spanId: "aaaaaaaaaaaaaaaa",
            traceId: "ffffffffffffffffffffffffffffffff",
            serviceName: "kyberdash-factory-plugin",
            name: "factory.turn",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["factory.contract_version"] = Element(CanonAdapters.DroidContractVersion),
                ["factory.session.id"] = Element(sessionId),
                ["factory.model"] = Element("gpt-5-codex"),
                ["factory.turn.prompt.text"] = Element(prompt),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([turn], store, store);
        Assert.Equal(new IngestOutcome(1, 0, 0), outcome);
        _ = new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()).ProjectIncremental();

        KyberBridgeEngine bridge = new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing, new FakeTextTokenizer()),
            new CanonRollupsEngine(store, store, pricing));
        store.ClearReadLog();
        JsonElement timeline = bridge.GetSessionTimeline(sessionId)!.Value.GetProperty("nodes");
        JsonElement node = Assert.Single(
            timeline.EnumerateArray(),
            static row => row.GetProperty("name").GetString() == "factory.turn");
        Assert.False(node.GetProperty("attributes").TryGetProperty(FactoryKeys.PromptText, out _));
        Assert.Equal(0, store.MaterializedContentBytes);

        // The prompt reaches a payload only through the requests section's page-scoped preview
        // read — never a whole-conversation one.
        JsonElement requests = bridge.GetSessionRequests(sessionId)!.Value;
        Assert.Contains(
            requests.GetProperty("requests").EnumerateArray(),
            static row => row.GetProperty("prompt").ValueKind == JsonValueKind.String);
        Assert.Contains(nameof(InMemoryRelationalStore.RequestPromptPreviews), store.ReadLog);

        // The capture is still stored: a scoped content read, not the content-free sections, is
        // what returns the prompt.
        Assert.Contains(
            store.FindRecordParts("aaaaaaaaaaaaaaaa"),
            part => part.PlainText == prompt);
    }

    /// <summary>
    /// A turn that stored no token class and no reported total is not priced. A stored
    /// zero is priced at zero. A reported input total is not billed at the fresh rate,
    /// a missing side is not billed as zero, and a class remainder stays partial.
    /// A turn that stored only input, or only output, stays partial.
    /// </summary>
    [Fact]
    public void SessionSummaryFigures_AbsentTokens_IsNotPricedAsZero()
    {
        const double inputRate = 0.000003;
        const double outputRate = 0.000015;
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new(snapshot: new Dictionary<string, ModelCosts>(StringComparer.Ordinal)
        {
            ["claude-sonnet-4-6"] = new ModelCosts(
                InputCostPerToken: inputRate,
                OutputCostPerToken: outputRate,
                CacheWriteCostPerToken: 0.00000375,
                CacheReadCostPerToken: 0.0000003,
                WebSearchCostPerRequest: 0,
                FastMultiplier: 1,
                CacheWriteCostIsExplicit: true),
        });

        store.WriteBatch(PricedTurn("sess-absent", tokens: []));
        store.WriteBatch(PricedTurn(
            "sess-zero",
            tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 0),
                new CollectedTokenCount(TokenClass.Output, 0),
            ]));
        store.WriteBatch(PricedTurn("sess-reported-both", tokens: [], reportedInput: 1_000, reportedOutput: 100));
        store.WriteBatch(PricedTurn("sess-reported-input", tokens: [], reportedInput: 1_000));
        store.WriteBatch(PricedTurn("sess-reported-output", tokens: [], reportedOutput: 100));
        store.WriteBatch(PricedTurn(
            "sess-remainder",
            tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 100),
                new CollectedTokenCount(TokenClass.Output, 10),
            ],
            reportedInput: 300));
        store.WriteBatch(PricedTurn(
            "sess-input-only",
            tokens: [new CollectedTokenCount(TokenClass.Input, 100)]));
        store.WriteBatch(PricedTurn(
            "sess-output-only",
            tokens: [new CollectedTokenCount(TokenClass.Output, 10)]));

        IModelPricingLookup tables = pricing.LoadPricingTables();
        AssertAggregateCost(store, tables, "sess-absent", CostStatus.NoRate, null);
        AssertAggregateCost(store, tables, "sess-zero", CostStatus.Priced, 0d);
        AssertAggregateCost(store, tables, "sess-reported-both", CostStatus.Partial, 100 * outputRate);
        AssertAggregateCost(store, tables, "sess-reported-input", CostStatus.NoRate, null);
        AssertAggregateCost(store, tables, "sess-reported-output", CostStatus.Partial, 100 * outputRate);
        AssertAggregateCost(store, tables, "sess-remainder", CostStatus.Partial, (100 * inputRate) + (10 * outputRate));
        AssertAggregateCost(store, tables, "sess-input-only", CostStatus.Partial, 100 * inputRate);
        AssertAggregateCost(store, tables, "sess-output-only", CostStatus.Partial, 10 * outputRate);
    }

    private static void AssertAggregateCost(
        InMemoryRelationalStore store,
        IModelPricingLookup tables,
        string sessionKey,
        CostStatus status,
        double? value)
    {
        SessionSummaryFigure view = store.SessionSummaryFigures(sessionKey, tables)!;
        Assert.Equal(status, view.PublishedCost.Status);
        if (value is null)
        {
            Assert.Null(view.PublishedCost.Value);
            return;
        }

        Assert.NotNull(view.PublishedCost.Value);
        Assert.Equal(value.Value, view.PublishedCost.Value.Value, precision: 8);
    }

    private static CollectedBatch PricedTurn(
        string sessionKey,
        IReadOnlyList<CollectedTokenCount> tokens,
        long? reportedInput = null,
        long? reportedOutput = null)
    {
        CollectedRecord record = new(
            RecordId: sessionKey + "-turn",
            HarnessId: "codex-cli",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "codex.turn",
            StartedAt: BaselineUtc,
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-6",
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: tokens,
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: reportedInput,
            ReportedOutputTokens: reportedOutput);

        return new CollectedBatch(
            Source: new CollectedSource("codex-cli", SourceKind.File, sessionKey),
            Sessions: [new CollectedSession(sessionKey, "codex-cli", sessionKey, null, null, [])],
            Records: [record],
            Problems: []);
    }

    /// <summary>
    /// Verifies <see cref="CanonIngestPipeline.IngestBatch(IReadOnlyList{OtlpSpan}, IRelationalStoreWriter, IOperationalStore, IReadOnlyList{IHarnessAdapter}?)"/>
    /// persists accepted records, problems, and quarantined spans through the relational contracts.
    /// </summary>
    [Fact]
    public void CanonIngestPipeline_IngestBatch_AcceptsRelationalWriterAndOperationalStore()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);

        OtlpSpan validSpan = MakeOtlpSpan(
            spanId: "5555555555555555",
            traceId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            serviceName: "pi-svc",
            name: "pi.turn",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Element("sess-pi-direct"),
                ["gen_ai.session.id"] = Element("sess-pi-direct"),
                ["gen_ai.usage.input_tokens"] = Element(25),
                ["gen_ai.usage.output_tokens"] = Element(10),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([validSpan], spyWriter, store);

        Assert.Equal(new IngestOutcome(1, 0, 0), outcome);
        Assert.Single(spyWriter.WriteBatchCalls);
        Assert.NotNull(store.FindRecord("5555555555555555"));
        Assert.NotNull(store.GetSession("sess-pi-direct"));
    }

    /// <summary>
    /// A record whose two <c>tool_definitions</c> parts differ in fate: the first parses into
    /// stored definitions, the second is truncated JSON the parser must refuse. The part that
    /// produced definitions is the only one skipped from stored content — its unparsed sibling
    /// keeps its text and reported tokens, so skipping is per part, never all-or-nothing.
    /// </summary>
    [Fact]
    public void IngestBatch_SkipsOnlyTheToolDefinitionPartsThatProducedDefinitions()
    {
        InMemoryRelationalStore store = new();
        ContentPart parsedPart = new("tool_definitions", """[{"name":"read","description":"Read a file"}]""", Order: 0);
        ContentPart truncatedPart = new("tool_definitions", "[{\"name\":\"wri\"", Tokens: 77, Order: 1);
        SeededPartsAdapter adapter = new("two-part-harness", [parsedPart, truncatedPart]);
        OtlpSpan span = MakeOtlpSpan(
            spanId: "4444444444444444",
            traceId: "dddddddddddddddddddddddddddddddd",
            serviceName: "two-part-host",
            name: "two-part.turn",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([span], store, store, [adapter]);

        Assert.Equal(new IngestOutcome(1, 0, 0), outcome);
        Assert.NotNull(store.FindRecord("4444444444444444"));
        Assert.Equal(
            ["read"],
            store.ToolDefinitions("4444444444444444").Select(static row => row.Definition.ToolName).ToList());

        List<CollectedContentPart> definitionParts = [.. store
            .FindRecordParts("4444444444444444")
            .Where(static part => part.Bucket == ContentBucket.ToolDefinitions)];
        Assert.Single(definitionParts);
        Assert.Equal("[{\"name\":\"wri\"", definitionParts[0].PlainText);
        Assert.Equal(77, definitionParts[0].ReportedTokens);
    }

    /// <summary>
    /// Verifies local source discovery and mapping persist exclusively via <see cref="IRelationalStoreWriter"/>,
    /// skip unchanged revisions via checkpoints, and replace changed sources cleanly.
    /// </summary>
    [Fact]
    public void RefreshHarnessSources_WritesMappedBatchesOnlyViaRelationalWriter_AndReusesCheckpoints()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string unitOnePath = Combine(HomeRoot, ".commandcode/projects/repo/u1.jsonl");
        string unitTwoPath = Combine(HomeRoot, ".commandcode/projects/repo/u2.jsonl");
        fs.AddFile(unitOnePath, "{\"type\":\"turn\",\"id\":\"rec-1\"}\n", BaselineUtc.AddMinutes(-10));
        fs.AddFile(unitTwoPath, "{\"type\":\"turn\",\"id\":\"rec-2\"}\n", BaselineUtc.AddMinutes(-5));

        CountingCommandCodeMapper mapper = new();
        TranscriptIngestionService service = new(
            spyWriter,
            store,
            new HarnessSourceRegistry(),
            [mapper],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport first = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(2, mapper.InvocationCount);
        Assert.Equal(2, spyWriter.WriteBatchCalls.Count);
        Assert.Equal(2, store.CountRecords("commandcode"));
        Assert.Equal(2, store.Sources("commandcode").Count);
        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", unitOnePath)?.LastStatus);
        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", unitTwoPath)?.LastStatus);
        Assert.Single(store.ListRefreshRuns());

        RefreshReport second = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, mapper.InvocationCount);
        Assert.Equal(2, spyWriter.WriteBatchCalls.Count);
        Assert.All(second.Rows, static row => Assert.Equal("skipped", row.Status));

        fs.AddFile(unitOnePath, "{\"type\":\"turn\",\"id\":\"rec-1\",\"updated\":true}\n", BaselineUtc.AddMinutes(1));
        RefreshReport third = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, third.ExitCode);
        Assert.Equal(3, mapper.InvocationCount);
        Assert.Single(spyWriter.DeleteSourceCalls);
        Assert.Equal(3, spyWriter.WriteBatchCalls.Count);
        Assert.Contains(third.Rows, r => r.SourceKey == unitOnePath && r.Status == "committed");
        Assert.Contains(third.Rows, r => r.SourceKey == unitTwoPath && r.Status == "skipped");
    }

    /// <summary>
    /// Verifies unreadable or malformed source units emit <c>SOURCE_UNREADABLE</c> without halting
    /// remaining units in the same refresh pass.
    /// </summary>
    [Fact]
    public void RefreshHarnessSources_UnreadableAndMalformedUnitsEmitSourceUnreadableAndContinue()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string ioFailPath = Combine(HomeRoot, ".commandcode/projects/repo/01-io-fail.jsonl");
        string parseFailPath = Combine(HomeRoot, ".commandcode/projects/repo/02-malformed.jsonl");
        string goodPath = Combine(HomeRoot, ".commandcode/projects/repo/03-good.jsonl");

        fs.AddFile(ioFailPath, "{\"type\":\"turn\"}\n", BaselineUtc.AddMinutes(-15));
        fs.ThrowOnRead(ioFailPath, new IOException("permission denied on file"));
        fs.AddFile(parseFailPath, "{not-valid-json\n", BaselineUtc.AddMinutes(-10));
        fs.AddFile(goodPath, "{\"type\":\"turn\",\"id\":\"rec-good\"}\n", BaselineUtc.AddMinutes(-5));

        CountingCommandCodeMapper mapper = new();
        TranscriptIngestionService service = new(
            spyWriter,
            store,
            new HarnessSourceRegistry(),
            [mapper],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport report = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(1, report.ExitCode);
        Assert.Equal(3, report.Rows.Count);
        Assert.Contains(report.Rows, r => r.SourceKey == ioFailPath && r.Status == "failed");
        Assert.Contains(report.Rows, r => r.SourceKey == parseFailPath && r.Status == "failed");
        Assert.Contains(report.Rows, r => r.SourceKey == goodPath && r.Status == "committed" && r.RecordsWritten == 1);

        Assert.Single(spyWriter.WriteBatchCalls);
        Assert.Equal(1, store.CountRecords("commandcode"));
        Assert.NotNull(store.FindRecord("rec-good"));

        Assert.Equal(2, spyWriter.WriteProblemCalls.Count);
        Assert.All(spyWriter.WriteProblemCalls, static p => Assert.Equal("SOURCE_UNREADABLE", p.Code));
        Assert.Equal(2, store.CountIngestProblems("commandcode"));
        Assert.Equal("SOURCE_UNREADABLE", store.GetSourceCheckpoint("commandcode", ioFailPath)?.LastErrorCode);
        Assert.Equal("failed", store.GetSourceCheckpoint("commandcode", ioFailPath)?.LastStatus);
        Assert.Equal("SOURCE_UNREADABLE", store.GetSourceCheckpoint("commandcode", parseFailPath)?.LastErrorCode);
        Assert.Equal("failed", store.GetSourceCheckpoint("commandcode", parseFailPath)?.LastStatus);
        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", goodPath)?.LastStatus);
    }

    /// <summary>
    /// Verifies unknown event types are counted in <c>ingest_problem</c> with zero payload rows
    /// or raw payloads persisted.
    /// </summary>
    [Fact]
    public void RefreshHarnessSources_UnknownEventTypesCountedWithZeroPayloadPersisted()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string unknownOnlyPath = Combine(HomeRoot, ".commandcode/projects/repo/unknown-events.jsonl");
        fs.AddFile(
            unknownOnlyPath,
            "{\"type\":\"future_unknown_event\",\"secretBody\":\"never-persist-this\"}\n{\"type\":\"another_unknown_event\",\"secretBody\":\"never-persist-either\"}\n",
            BaselineUtc.AddMinutes(-5));

        CountingCommandCodeMapper mapper = new();
        TranscriptIngestionService service = new(
            spyWriter,
            store,
            new HarnessSourceRegistry(),
            [mapper],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport report = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(0, store.CountRecords());
        Assert.Equal(2, store.CountIngestProblems("commandcode"));

        IReadOnlyList<StoredIngestProblem> problems = store.ListIngestProblems("commandcode");
        Assert.Equal(2, problems.Count);
        foreach (StoredIngestProblem entry in problems)
        {
            Assert.Equal("UNKNOWN_EVENT_TYPE", entry.Problem.Code);
            Assert.DoesNotContain("never-persist", entry.Problem.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("{", entry.Problem.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Verifies <c>IngestSpanNormalizer</c> produces relational DTO batch output directly.
    /// </summary>
    [Fact]
    public void IngestSpanNormalizer_DropsPipelineSinkAndProducesRelationalDtoBatchOutput()
    {
        Type? normalizerType = typeof(TranscriptIngestionService).Assembly.GetType("KyberDash.Application.IngestSpanNormalizer");
        Assert.NotNull(normalizerType);

        Type[] nestedTypes = normalizerType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(nestedTypes, static t => string.Equals(t.Name, "PipelineSink", StringComparison.Ordinal));

        OtlpSpan validSpan = MakeOtlpSpan(
            spanId: "6666666666666666",
            traceId: "ffffffffffffffffffffffffffffffff",
            serviceName: "copilot-host",
            name: "copilot.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element("sess-norm"),
                ["gen_ai.usage.input_tokens"] = Element(100),
                ["gen_ai.usage.output_tokens"] = Element(25),
            });

        OtlpSpan unclaimedSpan = MakeOtlpSpan(
            spanId: "7777777777777777",
            traceId: "ffffffffffffffffffffffffffffffff",
            serviceName: "other",
            name: "other.span",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["custom.attr"] = Element("x"),
            });

        MethodInfo? normalizeMethod = normalizerType.GetMethod("Normalize", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(normalizeMethod);

        object? batch = normalizeMethod.Invoke(null, [new[] { validSpan, unclaimedSpan }]);
        Assert.NotNull(batch);

        Type batchType = batch.GetType();
        PropertyInfo? relationalBatchesProp = batchType.GetProperty("RelationalBatches");
        PropertyInfo? quarantinedSpansProp = batchType.GetProperty("QuarantinedSpans");
        PropertyInfo? relationalProblemsProp = batchType.GetProperty("RelationalProblems");

        Assert.NotNull(relationalBatchesProp);
        Assert.NotNull(quarantinedSpansProp);
        Assert.NotNull(relationalProblemsProp);

        IReadOnlyList<CollectedBatch> relationalBatches = Assert.IsAssignableFrom<IReadOnlyList<CollectedBatch>>(relationalBatchesProp.GetValue(batch));
        IReadOnlyList<StoredQuarantinedSpan> quarantinedSpans = Assert.IsAssignableFrom<IReadOnlyList<StoredQuarantinedSpan>>(quarantinedSpansProp.GetValue(batch));
        IReadOnlyList<CollectedProblem> relationalProblems = Assert.IsAssignableFrom<IReadOnlyList<CollectedProblem>>(relationalProblemsProp.GetValue(batch));

        CollectedBatch collected = Assert.Single(relationalBatches);
        Assert.Single(collected.Records);
        Assert.Equal("6666666666666666", collected.Records[0].RecordId);
        StoredQuarantinedSpan quarantined = Assert.Single(quarantinedSpans);
        Assert.Equal("7777777777777777", quarantined.SpanId);
        Assert.Empty(relationalProblems);
    }

    /// <summary>
    /// Verifies <see cref="ServiceCollectionExtensions.AddDashApplication"/> wires
    /// <see cref="TranscriptIngestionService"/> with <see cref="IRelationalStoreWriter"/>,
    /// <see cref="IOperationalStore"/>, and <see cref="HarnessSourceRegistry"/>.
    /// </summary>
    [Fact]
    public void ServiceCollectionExtensions_WiresTranscriptIngestionServiceThroughRelationalWriterAndOperationalStore()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string path = Combine(HomeRoot, ".commandcode/projects/repo/di.jsonl");
        fs.AddFile(path, "{\"type\":\"turn\",\"id\":\"rec-di\"}\n", BaselineUtc);

        ServiceCollection services = new();
        services.AddSingleton<IRelationalStoreWriter>(spyWriter);
        services.AddSingleton<IOperationalStore>(store);
        services.AddSingleton<IHarnessFileSystem>(fs);
        services.AddSingleton<IHarnessMapper, CountingCommandCodeMapper>();

        _ = services.AddDashApplication(new DashOptions { StateDirectory = StateRoot });

        using ServiceProvider provider = services.BuildServiceProvider();
        ITranscriptIngestionService ingestion = provider.GetRequiredService<ITranscriptIngestionService>();

        IngestOutcome spanOutcome = ingestion.IngestSpans(
        [
            MakeOtlpSpan(
                spanId: "8888888888888888",
                traceId: "12121212121212121212121212121212",
                serviceName: "pi-svc",
                name: "pi.turn",
                kind: "client",
                attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["pi.session.id"] = Element("sess-di"),
                    ["gen_ai.session.id"] = Element("sess-di"),
                    ["gen_ai.usage.input_tokens"] = Element(12),
                    ["gen_ai.usage.output_tokens"] = Element(4),
                }),
        ]);

        Assert.Equal(1, spanOutcome.Accepted);
        Assert.NotNull(store.FindRecord("8888888888888888"));
    }

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static OtlpSpan OpenCodeSpan(
        string spanId,
        string name,
        IReadOnlyDictionary<string, JsonElement> attributes) =>
        MakeOtlpSpan(
            spanId,
            traceId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            serviceName: "opencode",
            name,
            kind: "internal",
            attributes);

    private static OtlpSpan MakeOtlpSpan(
        string spanId,
        string traceId,
        string serviceName,
        string name,
        string kind,
        IReadOnlyDictionary<string, JsonElement> attributes) =>
        new(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: null,
            Name: name,
            Kind: kind,
            StartTimeUnixNano: "1760000000000000000",
            EndTimeUnixNano: "1760000000100000000",
            Timestamp: "2026-11-19T10:00:00.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("ok"),
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element(serviceName),
            },
            Scope: new OtlpScope("test-scope", "1.0.0"));

    /// <summary>
    /// A harness adapter whose normalized record carries exactly the seeded content parts, standing
    /// in for a harness that reports tool definitions in parts a test controls.
    /// </summary>
    private sealed class SeededPartsAdapter(string harnessName, IReadOnlyList<ContentPart> parts) : IHarnessAdapter
    {
        public string Name => harnessName;

        public IReadOnlyList<string> Namespaces => ["two-part"];

        public double Detect(RawSpan span) => 1.0;

        public CanonicalRecord Normalize(RawSpan span) => new()
        {
            SpanId = span.SpanId,
            Source = span.Source,
            Harness = harnessName,
            Name = span.Name,
            Op = "llm.invoke",
            Kind = span.Kind,
            Timestamp = span.Timestamp ?? string.Empty,
            Status = "ok",
            Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
            Cost = new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
            Parts = parts,
            Content = CanonValidation.ContentFromParts(parts),
            SessionId = "sess-two-part-defs",
        };

        public Problem? Validate(CanonicalRecord record) => null;
    }

    private sealed class SpyRelationalStoreWriter(IRelationalStoreWriter inner) : IRelationalStoreWriter
    {
        public List<CollectedBatch> WriteBatchCalls { get; } = [];

        public List<string> DeleteSourceCalls { get; } = [];

        public List<CollectedProblem> WriteProblemCalls { get; } = [];

        public void WriteBatch(CollectedBatch batch)
        {
            WriteBatchCalls.Add(batch);
            inner.WriteBatch(batch);
        }

        public void DeleteSource(string sourceId)
        {
            DeleteSourceCalls.Add(sourceId);
            inner.DeleteSource(sourceId);
        }

        public void WriteProblem(CollectedProblem problem)
        {
            WriteProblemCalls.Add(problem);
            inner.WriteProblem(problem);
        }
    }

    private sealed class CountingCommandCodeMapper : IHarnessMapper
    {
        public string HarnessId => "commandcode";

        public int InvocationCount { get; private set; }

        public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
        {
            InvocationCount++;
            string rawText = unit.RawText ?? string.Empty;
            List<CollectedRecord> records = [];
            List<CollectedProblem> problems = [];

            string[] lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                using JsonDocument document = JsonDocument.Parse(lines[i]);
                JsonElement root = document.RootElement;
                string eventType = root.TryGetProperty("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString() ?? "unknown"
                    : "unknown";

                if (string.Equals(eventType, "turn", StringComparison.Ordinal))
                {
                    string id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? $"rec-{i}"
                        : $"rec-{i}";

                    records.Add(new CollectedRecord(
                        RecordId: id,
                        HarnessId: HarnessId,
                        Op: RecordOp.LlmInvoke,
                        Kind: RecordKind.Client,
                        Name: "commandcode.turn",
                        StartedAt: importedAtUtc,
                        Status: RecordStatus.Ok,
                        SessionKey: "sess-cc",
                        ParentRecordId: null,
                        TraceId: null,
                        Model: "claude-sonnet-4-5",
                        EndedAt: null,
                        ReportedDurationMs: 10,
                        Tokens: [new CollectedTokenCount(TokenClass.Input, 10), new CollectedTokenCount(TokenClass.Output, 5)],
                        ReportedCost: null,
                        Parts: [],
                        Tool: null,
                        Agent: null,
                        SkillsInvoked: [],
                        AgentInstanceId: null,
                        ToolDefinitions: [],
                        Signals: new CollectedRecordSignals(null, null, null, null)));
                }
                else
                {
                    problems.Add(new CollectedProblem(
                        ProblemKey: $"unknown-event:{unit.Unit.SourceKey}:{i}",
                        Severity: "warning",
                        Code: "UNKNOWN_EVENT_TYPE",
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipped unknown event type '{eventType}'."),
                        Location: unit.Unit.SourceKey,
                        HarnessId: HarnessId,
                        TimestampUtc: importedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
                }
            }

            List<CollectedSession> sessions = records.Count > 0
                ? [new CollectedSession("sess-cc", HarnessId, "sess-cc", null, null, [])]
                : [];

            return new CollectedBatch(
                Source: new CollectedSource(HarnessId, SourceKind.File, unit.Unit.SourceKey),
                Sessions: sessions,
                Records: records,
                Problems: problems);
        }
    }

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, (string Content, long LengthBytes, DateTimeOffset LastWriteUtc)> files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> readFailures = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

        public void AddFile(string path, string content, DateTimeOffset lastWriteUtc)
        {
            string normalized = Path.GetFullPath(path);
            files[normalized] = (content, Encoding.UTF8.GetByteCount(content), lastWriteUtc);

            string? dir = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(dir))
            {
                _ = directories.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        public void ThrowOnRead(string path, Exception exception) =>
            readFailures[Path.GetFullPath(path)] = exception;

        public bool DirectoryExists(string path) =>
            directories.Contains(Path.GetFullPath(path));

        public bool FileExists(string path) =>
            files.ContainsKey(Path.GetFullPath(path));

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
        {
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            List<string> matches = [];
            foreach (string filePath in files.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                string? parent = Path.GetDirectoryName(filePath);
                if (parent is null)
                {
                    continue;
                }

                bool inScope = recursive
                    ? string.Equals(parent, normalizedRoot, StringComparison.Ordinal)
                      || parent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    : string.Equals(parent, normalizedRoot, StringComparison.Ordinal);

                if (inScope && MatchesPattern(Path.GetFileName(filePath), searchPattern))
                {
                    matches.Add(filePath);
                }
            }

            return matches;
        }

        public IEnumerable<string> EnumerateDirectories(string directoryPath)
        {
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            return directories
                .Where(d => string.Equals(Path.GetDirectoryName(d), normalizedRoot, StringComparison.Ordinal))
                .OrderBy(static d => d, StringComparer.Ordinal)
                .ToList();
        }

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            if (files.TryGetValue(Path.GetFullPath(filePath), out (string Content, long LengthBytes, DateTimeOffset LastWriteUtc) entry))
            {
                lengthBytes = entry.LengthBytes;
                lastWriteUtc = entry.LastWriteUtc;
                return true;
            }

            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath)
        {
            string normalized = Path.GetFullPath(filePath);
            if (readFailures.TryGetValue(normalized, out Exception? exception))
            {
                throw exception;
            }

            return files[normalized].Content;
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);

        private static bool MatchesPattern(string fileName, string pattern)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
