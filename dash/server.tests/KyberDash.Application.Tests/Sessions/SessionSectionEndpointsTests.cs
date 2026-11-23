using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Sessions;

/// <summary>
/// The section endpoints answer their slice of the session view from purpose-named content-free
/// reads: <see cref="KyberBridgeEngine.GetSessionSummary"/> carries every field the summary
/// section renders, the charts section carries the turn figures read's turn list and the stored
/// context projection's context object without paying for any other section's reads,
/// the turn section carries that turn's stored context buckets, the requests, reconciliation and
/// problems sections page today's rows at 25 with the true total, the tools section pages the
/// ranked rows with the aggregate priced over every row, the servers and skills sections page
/// today's rows at 25, the timeline section pages one tree level at a time with the union of all
/// levels equal to today's <see cref="CanonContextAnalysis.BuildTimeline"/> tree, and the run
/// page's turn list comes from the same per-session turn figures.
/// </summary>
public sealed class SessionSectionEndpointsTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Reader methods that materialize conversation text, so a section may not issue them.</summary>
    private static readonly string[] ContentBearingReads =
    [
        nameof(InMemoryRelationalStore.SessionRecordsForRebuild),
        nameof(InMemoryRelationalStore.AllSessionRecordsForMaintenance),
        nameof(InMemoryRelationalStore.ToolCalls),
        nameof(InMemoryRelationalStore.ToolDefinitions),
        nameof(InMemoryRelationalStore.AgentInvocations),
        nameof(InMemoryRelationalStore.SkillsInvoked),
        nameof(InMemoryRelationalStore.TurnContentParts),
    ];

    /// <summary>
    /// The widened summary section carries the summary, notes, cost-basis mismatch, span count,
    /// reconciliation totals, and unused-tool count for the seeded session, from the content-free
    /// summary figures alone — parity checked against the store's figures and the section routes
    /// that serve the rows the totals count.
    /// </summary>
    [Fact]
    public void TheSummarySectionCarriesTheContractFields()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement summary = bridge.GetSessionSummary(sessionKey)!.Value;

        // The summary block carries the seeded session's figures: 128 turns whose measured input
        // is fresh + cache-read (1,200 + 2i each), so 169,856 input tokens — the header the
        // detail route used to serve.
        Assert.Equal(128, summary.GetProperty("summary").GetProperty("turn_count").GetInt32());
        Assert.Equal(169_856, summary.GetProperty("summary").GetProperty("total_input").GetInt64());
        Assert.True(summary.GetProperty("notes").GetArrayLength() > 0);
        StoredSessionProjection? storedHeader = store.GetSessionProjection(sessionKey);
        Assert.NotNull(storedHeader);
        Assert.Equal(storedHeader.SpanCount, summary.GetProperty("span_count").GetInt32());

        // The reconciliation totals count the same rows the reconciliation section pages.
        int total = 0;
        int mismatched = 0;
        for (int page = 1; ; page++)
        {
            JsonElement reconciliationPage = bridge.GetSessionReconciliation(sessionKey, page)!.Value;
            foreach (JsonElement row in reconciliationPage.GetProperty("reconciliation").EnumerateArray())
            {
                total++;
                if (!row.GetProperty("input_match").GetBoolean() || !row.GetProperty("output_match").GetBoolean())
                {
                    mismatched++;
                }
            }

            if (reconciliationPage.GetProperty("reconciliation").EnumerateArray().Count() < 25)
            {
                break;
            }
        }

        Assert.Equal(128, total);
        Assert.Equal(1, mismatched);
        Assert.Equal(total, summary.GetProperty("reconciliation").GetProperty("total").GetInt32());
        Assert.Equal(mismatched, summary.GetProperty("reconciliation").GetProperty("mismatched").GetInt32());

        // The unused-tool count is offered minus invoked by name, computed on the server, and
        // equals the tools section's aggregate over the same rows.
        JsonElement toolsPage = bridge.GetSessionTools(sessionKey)!.Value;
        Assert.Equal(2, summary.GetProperty("tools_offered_count").GetInt32());
        Assert.Equal(1, summary.GetProperty("tools_invoked_count").GetInt32());
        Assert.Equal(1, toolsPage.GetProperty("aggregate").GetProperty("unusedCount").GetInt32());
        Assert.Equal(
            toolsPage.GetProperty("aggregate").GetProperty("unusedCount").GetInt32(),
            summary.GetProperty("unused_tools_count").GetInt32());
        Assert.Equal(1, summary.GetProperty("skills_count").GetInt32());
        Assert.Equal(0, summary.GetProperty("problems_count").GetInt32());

        // The section answers from the content-free summary read alone.
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
        Assert.Contains(nameof(InMemoryRelationalStore.SessionSummaryFigures), store.ReadLog);
        Assert.Contains(nameof(InMemoryRelationalStore.SessionHeadersByKeys), store.SessionProjectionReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.GetSessionTurns), store.ReadLog);
    }

    /// <summary>
    /// The charts section carries the turn figures read's turn list and the stored context
    /// projection's context object on a 128-turn session — including the first and last context
    /// edges, which price the records' reported input rather than the stored projection's
    /// measured input — and issues no tool, skill, request, timeline, or content read.
    /// </summary>
    [Fact]
    public void TheChartsSectionCarriesTheTurnFiguresTurnsAndContext()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        SessionTurnFigureSet turnSet = store.SessionTurnFigures(sessionKey, pricing.LoadPricingTables())!;
        store.ClearReadLog();
        JsonElement charts = bridge.GetSessionCharts(sessionKey)!.Value;

        // The charts read is the turn figures pass alone; the projection reads are the 404 header
        // check and the stored context projection. The filtered names are EnsureProjected's
        // session-agnostic steady-state guards, which every route pays before its section reads.
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionTurnFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Equal(
            (IReadOnlyList<string>)
            [
                nameof(InMemoryRelationalStore.SessionHeadersByKeys),
                nameof(InMemoryRelationalStore.GetSessionProjection),
            ],
            store.SessionProjectionReadLog
                .Where(static read => read is not ("ListSessionProjections" or "ListHarnessRollups"))
                .ToList());

        Assert.Equal(sessionKey, charts.GetProperty("sessionId").GetString());

        // The charts turn list is the turn figures read's own list, in turn order, with the
        // dual-key row shape the turn page renders.
        List<JsonElement> turnRows = [.. charts.GetProperty("turns").EnumerateArray()];
        Assert.Equal(turnSet.Turns.Count, turnRows.Count);
        for (int index = 0; index < turnRows.Count; index++)
        {
            Assert.Equal(turnSet.Turns[index].SpanId, turnRows[index].GetProperty("spanId").GetString());
            Assert.Equal(turnSet.Turns[index].Position, turnRows[index].GetProperty("turnIndex").GetInt32());
            Assert.Equal(turnSet.Turns[index].InputTokens, turnRows[index].GetProperty("inputTokens").GetInt64());
            Assert.Equal(turnSet.Turns[index].OutputTokens, turnRows[index].GetProperty("outputTokens").GetInt64());
        }

        // The context object's per-turn entries are the same entries the turn section serves.
        List<JsonElement> contextTurns = [.. charts.GetProperty("context").GetProperty("turns").EnumerateArray()];
        Assert.Equal(turnSet.Turns.Count, contextTurns.Count);
        JsonElement turnSection = bridge.GetTurnFigures(sessionKey, 1)!.Value;
        Assert.Equal(contextTurns[1].GetRawText(), turnSection.GetProperty("context").GetRawText());
        Assert.True(charts.GetProperty("context").TryGetProperty("first", out JsonElement firstEdge));
        Assert.True(firstEdge.TryGetProperty("buckets", out _));
        Assert.True(firstEdge.TryGetProperty("reported_input", out _));
        Assert.True(charts.GetProperty("context").TryGetProperty("last", out JsonElement lastEdge));
        Assert.True(lastEdge.TryGetProperty("buckets", out _));
        Assert.True(lastEdge.TryGetProperty("reported_input", out _));
    }

    /// <summary>
    /// The widened turn section carries that turn's stored context buckets — the same per-turn
    /// entry the charts context object lists — beside today's turn figures.
    /// </summary>
    [Fact]
    public void TheTurnSectionCarriesThatTurnsContextBuckets()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        JsonElement charts = bridge.GetSessionCharts(sessionKey)!.Value;
        store.ClearReadLog();
        JsonElement figures = bridge.GetTurnFigures(sessionKey, 1)!.Value;

        Assert.Equal("turn-sec-001", figures.GetProperty("turn").GetProperty("spanId").GetString());
        Assert.Equal(
            charts.GetProperty("context").GetProperty("turns")[1].GetRawText(),
            figures.GetProperty("context").GetRawText());
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.TurnFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Null(bridge.GetTurnFigures(sessionKey, 128));
    }

    /// <summary>
    /// The requests section pages today's request rows at 25 with the true total, and carries the
    /// prompt previews the page's requests own — read in one scoped pass over exactly the page's
    /// request spans, never the session's whole conversation.
    /// </summary>
    [Fact]
    public void TheRequestsSectionPagesAt25AndCarriesTodaysRows()
    {
        PreviewCapturingStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        SessionRequestFigurePageResult firstFigures = store.SessionRequestFigures(sessionKey, 1, 25);
        SessionRequestFigurePageResult secondFigures = store.SessionRequestFigures(sessionKey, 2, 25);
        SessionRequestFigurePageResult lastFigures = store.SessionRequestFigures(sessionKey, 6, 25);

        store.ClearReadLog();
        JsonElement page = bridge.GetSessionRequests(sessionKey)!.Value;

        Assert.Equal(firstFigures.Total, page.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. page.GetProperty("requests").EnumerateArray()];
        Assert.Equal(25, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            Assert.Equal(firstFigures.Items[index].RequestSpanId, rows[index].GetProperty("request").GetString());
            Assert.Equal(firstFigures.Items[index].TimestampUtc, rows[index].GetProperty("timestamp").GetString());
            Assert.Equal(firstFigures.Items[index].Turns, rows[index].GetProperty("turns").GetInt32());
            Assert.Equal(firstFigures.Items[index].Model, rows[index].GetProperty("model").GetString());
        }

        // Every preview is the scoped part read's first 200 characters of the page's requests'
        // own prompt parts, which the detail payload cannot carry on its content-free pass.
        Assert.All(rows, static row => Assert.Equal(new string('x', 200), row.GetProperty("prompt").GetString()));

        // One prompt-preview read answered exactly the page's request spans.
        IReadOnlyList<string> previewRead = Assert.Single(store.PromptPreviewReads);
        Assert.True(previewRead.Count <= 25, $"the prompt read took {previewRead.Count} ids");
        Assert.Equal(
            rows.Select(static row => row.GetProperty("request").GetString() ?? string.Empty).ToList(),
            previewRead.ToList());

        // The requests read is the section's own content-free pass plus the one scoped prompt
        // read; the projection reads are the 404 header check and EnsureProjected's guards.
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionRequestFigures), nameof(InMemoryRelationalStore.RequestPromptPreviews)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionHeadersByKeys)],
            store.SessionProjectionReadLog
                .Where(static read => read is not ("ListSessionProjections" or "ListHarnessRollups"))
                .ToList());
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));

        // Page 2 continues the rows and pays its own prompt read; a page past the end answers
        // empty with the true total.
        store.ClearReadLog();
        store.PromptPreviewReads.Clear();
        JsonElement second = bridge.GetSessionRequests(sessionKey, page: 2)!.Value;
        Assert.Equal(secondFigures.Items[0].RequestSpanId, second.GetProperty("requests").EnumerateArray().First().GetProperty("request").GetString());
        Assert.Equal(firstFigures.Total, second.GetProperty("total").GetInt32());
        _ = Assert.Single(store.PromptPreviewReads);

        store.ClearReadLog();
        store.PromptPreviewReads.Clear();
        JsonElement lastPage = bridge.GetSessionRequests(sessionKey, page: 6)!.Value;
        Assert.Equal(3, lastPage.GetProperty("requests").EnumerateArray().Count());
        Assert.Equal(lastFigures.Items[^1].RequestSpanId, lastPage.GetProperty("requests").EnumerateArray().Last().GetProperty("request").GetString());
        Assert.Equal(firstFigures.Total, lastPage.GetProperty("total").GetInt32());

        store.ClearReadLog();
        store.PromptPreviewReads.Clear();
        JsonElement past = bridge.GetSessionRequests(sessionKey, page: 7)!.Value;
        Assert.Empty(past.GetProperty("requests").EnumerateArray().ToList());
        Assert.Equal(firstFigures.Total, past.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The reconciliation section pages today's measurable reconciliation rows at 25 with the true
    /// total, in turn order, from the content-free reconciliation figures alone.
    /// </summary>
    [Fact]
    public void TheReconciliationSectionPagesAt25AndCarriesTodaysRows()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-sections";
        WriteSectionSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        SessionReconciliationFigurePageResult firstFigures = store.SessionReconciliationFigures(sessionKey, 1, 25);
        SessionReconciliationFigurePageResult secondFigures = store.SessionReconciliationFigures(sessionKey, 2, 25);
        SessionReconciliationFigurePageResult lastFigures = store.SessionReconciliationFigures(sessionKey, 6, 25);
        Assert.Equal(128, firstFigures.Total);

        store.ClearReadLog();
        JsonElement page = bridge.GetSessionReconciliation(sessionKey)!.Value;

        Assert.Equal(firstFigures.Total, page.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. page.GetProperty("reconciliation").EnumerateArray()];
        Assert.Equal(25, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            Assert.Equal(firstFigures.Items[index].RequestSpanId, rows[index].GetProperty("request").GetString());
            Assert.Equal(firstFigures.Items[index].RootInput, rows[index].GetProperty("root_input").GetInt64());
            Assert.Equal(firstFigures.Items[index].SumChatInput, rows[index].GetProperty("sum_chat_input").GetInt64());
            Assert.Equal(firstFigures.Items[index].InputMatch, rows[index].GetProperty("input_match").GetBoolean());
        }

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionReconciliationFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionHeadersByKeys)],
            store.SessionProjectionReadLog
                .Where(static read => read is not ("ListSessionProjections" or "ListHarnessRollups"))
                .ToList());
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));

        JsonElement second = bridge.GetSessionReconciliation(sessionKey, page: 2)!.Value;
        Assert.Equal(secondFigures.Items[0].RequestSpanId, second.GetProperty("reconciliation").EnumerateArray().First().GetProperty("request").GetString());

        JsonElement lastPage = bridge.GetSessionReconciliation(sessionKey, page: 6)!.Value;
        Assert.Equal(3, lastPage.GetProperty("reconciliation").EnumerateArray().Count());
        Assert.Equal(lastFigures.Items[^1].RequestSpanId, lastPage.GetProperty("reconciliation").EnumerateArray().Last().GetProperty("request").GetString());

        JsonElement past = bridge.GetSessionReconciliation(sessionKey, page: 7)!.Value;
        Assert.Empty(past.GetProperty("reconciliation").EnumerateArray().ToList());
        Assert.Equal(firstFigures.Total, past.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The problems section pages the session's stored ingest problems at 25 with the true total,
    /// carrying the same rows the detail payload's problems array lists.
    /// </summary>
    [Fact]
    public void TheProblemsSectionPagesStoredIngestProblems()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-problems";
        WriteSession(
            store,
            sessionKey,
            [
                Turn("span-problems-1", sessionKey, 0, inputTokens: 100),
                Turn("span-problems-2", sessionKey, 1, inputTokens: 200),
            ]);
        WriteProblems(store, sessionKey, count: 30);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        SessionProblemPageResult firstFigures = store.SessionProblems(sessionKey, 1, 25);
        SessionProblemPageResult secondFigures = store.SessionProblems(sessionKey, 2, 25);
        Assert.Equal(30, firstFigures.Total);

        store.ClearReadLog();
        JsonElement page = bridge.GetSessionProblems(sessionKey)!.Value;

        Assert.Equal(firstFigures.Total, page.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. page.GetProperty("problems").EnumerateArray()];
        Assert.Equal(25, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            Assert.Equal(firstFigures.Items[index].Code, rows[index].GetProperty("code").GetString());
            Assert.Equal(firstFigures.Items[index].Message, rows[index].GetProperty("message").GetString());
            Assert.Equal(firstFigures.Items[index].Location, rows[index].GetProperty("location").GetString());
        }

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionProblems)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionHeadersByKeys)],
            store.SessionProjectionReadLog
                .Where(static read => read is not ("ListSessionProjections" or "ListHarnessRollups"))
                .ToList());
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));

        JsonElement second = bridge.GetSessionProblems(sessionKey, page: 2)!.Value;
        Assert.Equal(5, second.GetProperty("problems").EnumerateArray().Count());
        Assert.Equal(secondFigures.Items[0].Message, second.GetProperty("problems").EnumerateArray().First().GetProperty("message").GetString());
    }

    /// <summary>
    /// The run page's turn list answers the same turns the per-session turn figures read carries,
    /// with the execution and session attribution the run view adds.
    /// </summary>
    [Fact]
    public void RunTurnsAnswerFromSessionTurnFigures()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        List<CollectedRecord> runTurns =
        [
            Turn("span-run-a1", "sess-run-a", 0, inputTokens: 400, cacheRead: 100, outputTokens: 60),
        ];
        runTurns.Add(Turn("span-run-a2", "sess-run-a", 1, inputTokens: 1_200, outputTokens: 80));
        WriteSession(store, "sess-run-a", runTurns, repo: "kyber-weave", branch: "kw-task/T4");
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();
        IModelPricingLookup lookup = pricing.LoadPricingTables();

        store.ClearReadLog();
        JsonElement run = bridge.GetRunById("derived:claude-code:sess-run-a")!.Value;

        SessionTurnFigureSet figures = store.SessionTurnFigures("sess-run-a", lookup)!;
        Assert.Equal(
            figures.Turns.Select(static turn => turn.SpanId).ToList(),
            run.GetProperty("turns").EnumerateArray()
                .Select(static turn => turn.GetProperty("spanId").GetString() ?? string.Empty)
                .ToList());
        JsonElement first = run.GetProperty("turns").EnumerateArray().First();
        Assert.Equal("sess-run-a", first.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.String, first.GetProperty("executionId").ValueKind);

        // The run page pays the turn figures read, not the tool figure set.
        Assert.Contains(nameof(InMemoryRelationalStore.SessionTurnFigures), store.ReadLog);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
    }

    /// <summary>
    /// The tools section pages the ranked tool rows — never-invoked rows included — at 25 with
    /// the true total, and its aggregate equals the tool figures' own page-independent aggregates
    /// on page 1 and on page 2: the largest resident cost, the resident cost total, the invocation
    /// count, the unused count, the unused range, the per-server rollup, and the span that carries
    /// the session's tool definitions.
    /// </summary>
    [Fact]
    public void TheToolsSectionPagesRankedRowsAndCarriesTodaysAggregate()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-tools";
        WriteManyToolSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        SessionToolFigureSet toolFigures = store.SessionToolFigures(sessionKey, pricing.LoadPricingTables());
        Assert.Equal(30, toolFigures.Tools.Count);

        store.ClearReadLog();
        JsonElement page = bridge.GetSessionTools(sessionKey)!.Value;

        Assert.Equal(30, page.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. page.GetProperty("tools").EnumerateArray()];
        Assert.Equal(25, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            // The ranked rows carry today's payload row shape in the figures' ranking order: the
            // name the schema ranking shows, and the invocations and residency the tools array
            // carries.
            Assert.Equal(toolFigures.Tools[index].Name, rows[index].GetProperty("name").GetString());
            Assert.Equal(toolFigures.Tools[index].Invocations, rows[index].GetProperty("invocations").GetInt32());
            Assert.Equal(toolFigures.Tools[index].Invoked, rows[index].GetProperty("invoked").GetBoolean());
            Assert.Equal(toolFigures.Tools[index].TotalSchemaCost, rows[index].GetProperty("total_schema_cost").GetInt64());
        }

        // The ranking includes the never-invoked rows: tool-29 sits at the top of page 1 and
        // tool-00 at the end of the ranking, on page 2 — two invoked rows across both pages.
        Assert.Equal(1, rows.Count(static row => row.GetProperty("invoked").GetBoolean()));

        // The aggregate equals the tool figures' page-independent aggregates, not just the page:
        // the largest resident cost, the resident cost total, the invocations the rows carry, the
        // rows never invoked, the unused range, the per-server rollup, and the span that carries
        // the session's tool definitions.
        JsonElement aggregate = page.GetProperty("aggregate");
        Assert.Equal(toolFigures.MaxCost, aggregate.GetProperty("maxCost").GetInt64());
        Assert.Equal(toolFigures.ResidentCostTotal, aggregate.GetProperty("residentCostTotal").GetInt64());
        Assert.Equal(toolFigures.InvocationCount, aggregate.GetProperty("invocationCount").GetInt32());
        Assert.Equal(toolFigures.UnusedCount, aggregate.GetProperty("unusedCount").GetInt32());
        Assert.NotNull(toolFigures.UnusedRange);
        Assert.Equal(toolFigures.UnusedRange.TokenResidencies, aggregate.GetProperty("unusedRange").GetProperty("tokenResidencies").GetInt64());
        foreach (StoredSchemaServerTokens server in toolFigures.ByServer)
        {
            Assert.Equal(server.Tokens, aggregate.GetProperty("byServer").GetProperty(server.ServerName).GetInt64());
        }

        Assert.Equal("turn-tools-000", aggregate.GetProperty("definitionsSpanId").GetString());

        // The tools read prices the tool tables and the stored schema only: no content, no
        // full projection read.
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionToolFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.GetSessionProjection), store.SessionProjectionReadLog);

        // Page 2 carries the remaining ranked rows — including the second invoked one — and the
        // same aggregate, and pays its own read.
        store.ClearReadLog();
        JsonElement second = bridge.GetSessionTools(sessionKey, page: 2)!.Value;
        JsonElement[] secondRows = [.. second.GetProperty("tools").EnumerateArray()];
        Assert.Equal(5, secondRows.Length);
        Assert.Equal(toolFigures.Tools[25].Name, secondRows[0].GetProperty("name").GetString());
        Assert.Equal(1, secondRows.Count(static row => row.GetProperty("invoked").GetBoolean()));
        Assert.Equal("tool-00", secondRows.Last(static row => row.GetProperty("invoked").GetBoolean()).GetProperty("name").GetString());
        Assert.Equal(aggregate.GetRawText(), second.GetProperty("aggregate").GetRawText());
        Assert.Equal(30, second.GetProperty("total").GetInt32());
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionToolFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
    }

    /// <summary>
    /// The servers section pages the tool figures' per-server rollup rows at 25 with the true
    /// total, from the same tool figures read the tools section pays.
    /// </summary>
    [Fact]
    public void TheServersSectionPagesTodaysRowsAt25()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-tools";
        WriteManyToolSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        SessionToolFigureSet toolFigures = store.SessionToolFigures(sessionKey, pricing.LoadPricingTables());
        Assert.Equal(2, toolFigures.Servers.Count);

        store.ClearReadLog();
        JsonElement page = bridge.GetSessionServers(sessionKey)!.Value;

        Assert.Equal(toolFigures.Servers.Count, page.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. page.GetProperty("servers").EnumerateArray()];
        Assert.Equal(toolFigures.Servers.Count, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            Assert.Equal(toolFigures.Servers[index].Server, rows[index].GetProperty("server").GetString());
            Assert.Equal(toolFigures.Servers[index].IsMcp, rows[index].GetProperty("is_mcp").GetBoolean());
            Assert.Equal(toolFigures.Servers[index].Tools, rows[index].GetProperty("tools").GetInt32());
            Assert.Equal(toolFigures.Servers[index].SchemaTokens, rows[index].GetProperty("schema_tokens").GetInt64());
            Assert.Equal(toolFigures.Servers[index].Invocations, rows[index].GetProperty("invocations").GetInt32());
            Assert.Equal(toolFigures.Servers[index].UnusedTools, rows[index].GetProperty("unused_tools").GetInt32());
        }

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionToolFigures)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
    }

    /// <summary>
    /// The skills section pages the session's distinct skill rows at 25 with the true total,
    /// carrying one row per invoked skill in invocation order, and answers a session that
    /// invoked no skills with the not-measurable block the summary section carries.
    /// </summary>
    [Fact]
    public void TheSkillsSectionPagesTodaysRowsAt25()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-skills";
        List<CollectedRecord> turns =
        [
            .. Enumerable.Range(0, 30).Select(i => Turn($"turn-skills-{i:D3}", sessionKey, i, inputTokens: 1_000)),
        ];
        for (int index = 0; index < turns.Count; index++)
        {
            string skillName = $"skill-{index:D2}";
            turns[index] = turns[index] with { SkillsInvoked = new[] { skillName } };
        }

        WriteSession(store, sessionKey, turns, repo: "kyber-weave", branch: "kw-task/T6", title: "skills session");
        WriteSession(
            store,
            "sess-noskills",
            [
                Turn("span-noskills-1", "sess-noskills", 0, inputTokens: 100),
                Turn("span-noskills-2", "sess-noskills", 1, inputTokens: 200),
            ]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement page = bridge.GetSessionSkills(sessionKey)!.Value;

        Assert.True(page.GetProperty("measurable").GetBoolean());
        Assert.Equal(30, page.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. page.GetProperty("rows").EnumerateArray()];
        Assert.Equal(25, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            Assert.Equal($"skill-{index:D2}", rows[index].GetProperty("name").GetString());
            Assert.Equal(1, rows[index].GetProperty("invocations").GetInt32());
        }

        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
        Assert.Contains(nameof(InMemoryRelationalStore.SessionSkillFigures), store.ReadLog);

        JsonElement second = bridge.GetSessionSkills(sessionKey, page: 2)!.Value;
        JsonElement[] secondRows = [.. second.GetProperty("rows").EnumerateArray()];
        Assert.Equal(5, secondRows.Length);
        Assert.Equal("skill-25", secondRows[0].GetProperty("name").GetString());
        Assert.Equal(30, second.GetProperty("total").GetInt32());

        // A session that invoked no skills answers the not-measurable block the summary section
        // carries, with no rows.
        JsonElement none = bridge.GetSessionSkills("sess-noskills")!.Value;
        Assert.Equal("not_measurable", none.GetProperty("availability").GetString());
        Assert.Empty(none.GetProperty("rows").EnumerateArray().ToList());
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// The timeline section pages the top level at 25 with today's node fields minus
    /// <c>children</c> plus each node's <c>childCount</c>, and the bounds of the whole metadata
    /// tree — from the timeline records read alone.
    /// </summary>
    [Fact]
    public void TheTimelineSectionPagesTheTopLevelAt25WithChildCountAndBounds()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-timeline";
        WriteTimelineSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        JsonElement[] today = TodayTimelineNodes(store, sessionKey);
        Assert.Equal(32, today.Length);

        store.ClearReadLog();
        JsonElement page = bridge.GetSessionTimeline(sessionKey)!.Value;

        Assert.Equal(sessionKey, page.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("parent").ValueKind);
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(25, page.GetProperty("limit").GetInt32());
        Assert.Equal(0, page.GetProperty("offset").GetInt32());
        Assert.Equal(32, page.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. page.GetProperty("nodes").EnumerateArray()];
        Assert.Equal(25, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            AssertTimelineRowCarriesTodaysFields(rows[index], today[index]);
        }

        // The bounds are the whole tree's extent, not the page's: the last root ends 29 minutes
        // after the session start, a page that carries none of it still carries its end.
        (long startMs, long endMs) = TimelineTreeExtent(today);
        Assert.Equal((0, 1_741_000), (startMs, endMs));
        Assert.Equal(startMs, page.GetProperty("bounds").GetProperty("startMs").GetInt64());
        Assert.Equal(endMs, page.GetProperty("bounds").GetProperty("endMs").GetInt64());

        // The section answers from the timeline records read alone.
        Assert.Equal(0, store.MaterializedContentBytes);
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionTimelineRecords)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());
        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionHeadersByKeys)],
            store.SessionProjectionReadLog
                .Where(static read => read is not ("ListSessionProjections" or "ListHarnessRollups"))
                .ToList());
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));

        // Page 2 carries the remaining seven top-level nodes; a page past the end answers empty
        // with the true total.
        store.ClearReadLog();
        JsonElement second = bridge.GetSessionTimeline(sessionKey, page: 2)!.Value;
        JsonElement[] secondRows = [.. second.GetProperty("nodes").EnumerateArray()];
        Assert.Equal(7, secondRows.Length);
        Assert.Equal(32, second.GetProperty("total").GetInt32());
        for (int index = 0; index < secondRows.Length; index++)
        {
            AssertTimelineRowCarriesTodaysFields(secondRows[index], today[25 + index]);
        }

        JsonElement past = bridge.GetSessionTimeline(sessionKey, page: 3)!.Value;
        Assert.Empty(past.GetProperty("nodes").EnumerateArray().ToList());
        Assert.Equal(32, past.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// A <c>?parent=</c> request — including one naming a synthetic container — answers that
    /// node's children paged at 25 in <c>BuildTimeline</c>'s sibling order, from the same
    /// timeline records read; a parent no node names answers nothing rather than an empty level.
    /// </summary>
    [Fact]
    public void TheTimelineSectionPagesASyntheticParentsChildrenAt25()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-timeline";
        WriteTimelineSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        JsonElement[] today = TodayTimelineNodes(store, sessionKey);
        JsonElement todayAuxiliary = Assert.Single(
            today,
            static node => node.GetProperty("spanId").GetString() == "(auxiliary)");
        JsonElement[] todayAuxiliaryChildren = [.. todayAuxiliary.GetProperty("children").EnumerateArray()];
        Assert.Equal(30, todayAuxiliaryChildren.Length);

        store.ClearReadLog();
        JsonElement auxiliary = bridge.GetSessionTimeline(sessionKey, parentNodeId: "(auxiliary)")!.Value;

        Assert.Equal("(auxiliary)", auxiliary.GetProperty("parent").GetString());
        Assert.Equal(30, auxiliary.GetProperty("total").GetInt32());
        JsonElement[] rows = [.. auxiliary.GetProperty("nodes").EnumerateArray()];
        Assert.Equal(25, rows.Length);
        for (int index = 0; index < rows.Length; index++)
        {
            AssertTimelineRowCarriesTodaysFields(rows[index], todayAuxiliaryChildren[index]);
            Assert.True(rows[index].GetProperty("isAuxiliary").GetBoolean());
        }

        Assert.Equal(
            (IReadOnlyList<string>)[nameof(InMemoryRelationalStore.SessionTimelineRecords)],
            store.ReadLog.Where(static read => read != "CountRecords").ToList());

        JsonElement second = bridge.GetSessionTimeline(sessionKey, parentNodeId: "(auxiliary)", page: 2)!.Value;
        JsonElement[] secondRows = [.. second.GetProperty("nodes").EnumerateArray()];
        Assert.Equal(5, secondRows.Length);
        Assert.Equal(30, second.GetProperty("total").GetInt32());
        AssertTimelineRowCarriesTodaysFields(secondRows[0], todayAuxiliaryChildren[25]);

        // The orphan container's id names a level too: its children's declared parent is
        // missing, so the group is the only place they hang.
        JsonElement todayOrphan = Assert.Single(
            today,
            static node => node.GetProperty("spanId").GetString()?.StartsWith("(orphan:", StringComparison.Ordinal) == true);
        JsonElement orphan = bridge.GetSessionTimeline(sessionKey, parentNodeId: todayOrphan.GetProperty("spanId").GetString())!.Value;
        Assert.Equal(2, orphan.GetProperty("total").GetInt32());
        Assert.Equal(2, orphan.GetProperty("nodes").EnumerateArray().Count());

        // A real span parent and the synthetic session root both name levels.
        JsonElement firstRoot = bridge.GetSessionTimeline(sessionKey, parentNodeId: "turn-tl-000")!.Value;
        Assert.Equal(3, firstRoot.GetProperty("total").GetInt32());
        JsonElement sessionRoot = bridge.GetSessionTimeline(sessionKey, parentNodeId: "(session)")!.Value;
        Assert.Equal(32, sessionRoot.GetProperty("total").GetInt32());

        // A parent no node names answers nothing: an empty level would read as a real node with
        // no children, and the caller could not tell the difference.
        Assert.Null(bridge.GetSessionTimeline(sessionKey, parentNodeId: "(no-such-node)"));
    }

    /// <summary>
    /// The union of every level the timeline section pages — every page of every parent,
    /// synthetic containers included — equals today's <c>BuildTimeline</c> tree over the
    /// session's content-free records: no node lost, none duplicated, and each level keeps the
    /// tree's sibling order.
    /// </summary>
    [Fact]
    public void TheUnionOfTimelineLevelsEqualsTodaysBuildTimelineTree()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-timeline";
        WriteTimelineSession(store, sessionKey);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        // Today's tree: BuildTimeline over the records the timeline read model prices.
        List<CanonicalRecord> todayRecords =
            [.. store.SessionRecordsContentFree(sessionKey).Select(CanonRecordHydration.Hydrate)];
        todayRecords.Sort(static (left, right) =>
        {
            int byTimestamp = string.CompareOrdinal(left.Timestamp, right.Timestamp);
            return byTimestamp != 0 ? byTimestamp : string.CompareOrdinal(left.SpanId, right.SpanId);
        });

        TimelineNode todayRoot = CanonContextAnalysis.BuildTimeline(todayRecords);
        // The top level groups under the empty key: no span id is empty, so it cannot collide.
        Dictionary<string, List<(string SpanId, int ChildCount)>> expectedLevels = [];
        void Walk(string parent, TimelineNode node)
        {
            if (!expectedLevels.TryGetValue(parent, out List<(string SpanId, int ChildCount)>? level))
            {
                level = [];
                expectedLevels[parent] = level;
            }

            level.Add((node.SpanId, node.Children.Count));
            foreach (TimelineNode child in node.Children)
            {
                Walk(node.SpanId, child);
            }
        }

        foreach (TimelineNode child in todayRoot.Children)
        {
            Walk(string.Empty, child);
        }

        // The pages: every level through the section route, descending only into parents the
        // pages themselves name, so the walk cannot invent a node the route never answered.
        List<(string? Parent, JsonElement Row)> paged = [];
        Queue<string?> pending = new();
        pending.Enqueue(null);
        while (pending.Count > 0)
        {
            string? parent = pending.Dequeue();
            for (int page = 1; ; page++)
            {
                JsonElement payload = bridge.GetSessionTimeline(sessionKey, parent, page: page)!.Value;
                JsonElement[] pageRows = [.. payload.GetProperty("nodes").EnumerateArray()];
                foreach (JsonElement row in pageRows)
                {
                    paged.Add((parent, row));
                    if (row.GetProperty("childCount").GetInt32() > 0)
                    {
                        pending.Enqueue(row.GetProperty("spanId").GetString());
                    }
                }

                if (pageRows.Length < 25)
                {
                    break;
                }
            }
        }

        Dictionary<string, List<(string SpanId, int ChildCount)>> pagedLevels = [];
        foreach ((string? parent, JsonElement row) in paged)
        {
            string key = parent ?? string.Empty;
            if (!pagedLevels.TryGetValue(key, out List<(string SpanId, int ChildCount)>? level))
            {
                level = [];
                pagedLevels[key] = level;
            }

            level.Add((row.GetProperty("spanId").GetString() ?? string.Empty, row.GetProperty("childCount").GetInt32()));
        }

        Assert.Equal(expectedLevels.Count, pagedLevels.Count);
        foreach (KeyValuePair<string, List<(string SpanId, int ChildCount)>> expected in expectedLevels)
        {
            Assert.True(
                pagedLevels.TryGetValue(expected.Key, out List<(string SpanId, int ChildCount)>? level),
                $"no page answered the level under '{(expected.Key.Length == 0 ? "(top level)" : expected.Key)}'");
            Assert.Equal(expected.Value.Count, level!.Count);
            for (int index = 0; index < expected.Value.Count; index++)
            {
                Assert.Equal(expected.Value[index].SpanId, level[index].SpanId);
                Assert.Equal(expected.Value[index].ChildCount, level[index].ChildCount);
            }
        }

        // No node appears twice: every span id the pages answered is distinct.
        Assert.Equal(
            paged.Count,
            paged
                .DistinctBy(static entry => entry.Row.GetProperty("spanId").GetString(), StringComparer.Ordinal)
                .Count());
    }

    /// <summary>
    /// A timeline records read that fails is isolated to its session: the section answers the
    /// projection-failed marker with no nodes, the coverage payload names the session, and the
    /// remaining sessions answer normally.
    /// </summary>
    [Fact]
    public void AFailingTimelineReadIsIsolatedAsProjectionFailed()
    {
        TimelineFaultingStore store = new();
        FakePricingRepository pricing = new();
        const string sessionKey = "sess-timeline-broken";
        WriteTimelineSession(store, sessionKey);
        WriteSession(store, "sess-timeline-healthy", [Turn("turn-healthy", "sess-timeline-healthy", 0, inputTokens: 100)]);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        JsonElement broken = bridge.GetSessionTimeline(sessionKey)!.Value;
        Assert.Equal("PROJECTION_FAILED", broken.GetProperty("projection_status").GetString());
        Assert.Empty(broken.GetProperty("nodes").EnumerateArray().ToList());
        Assert.Equal(0, broken.GetProperty("total").GetInt32());

        JsonElement healthy = bridge.GetSessionTimeline("sess-timeline-healthy")!.Value;
        Assert.False(healthy.TryGetProperty("projection_status", out _));
        Assert.Equal(1, healthy.GetProperty("total").GetInt32());

        JsonElement projectionHealth = bridge.GetCoverage().GetProperty("projection");
        Assert.Equal("PROJECTION_FAILED", projectionHealth.GetProperty("status").GetString());
        Assert.Contains(
            sessionKey,
            [.. projectionHealth.GetProperty("failedSessions").EnumerateArray().Select(static session => session.GetString())],
            StringComparer.Ordinal);
    }

    /// <summary>
    /// An unknown session's section routes answer nothing through the header read: the 404 check
    /// is <see cref="InMemoryRelationalStore.SessionHeadersByKeys"/>, never the full projection
    /// read, and no content-free read runs either.
    /// </summary>
    [Fact]
    public void SectionRoutesAnswer404ThroughTheHeaderRead()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        bridge.EnsureProjected();

        store.ClearReadLog();
        Assert.Null(bridge.GetSessionSummary("sess-absent"));
        Assert.Null(bridge.GetSessionCharts("sess-absent"));
        Assert.Null(bridge.GetTurnFigures("sess-absent", 0));
        Assert.Null(bridge.GetSessionRequests("sess-absent"));
        Assert.Null(bridge.GetSessionReconciliation("sess-absent"));
        Assert.Null(bridge.GetSessionProblems("sess-absent"));
        Assert.Null(bridge.GetSessionTools("sess-absent"));
        Assert.Null(bridge.GetSessionServers("sess-absent"));
        Assert.Null(bridge.GetSessionSkills("sess-absent"));
        Assert.Null(bridge.GetSessionTimeline("sess-absent"));

        Assert.Contains(nameof(InMemoryRelationalStore.SessionHeadersByKeys), store.SessionProjectionReadLog);
        Assert.DoesNotContain(nameof(InMemoryRelationalStore.GetSessionProjection), store.SessionProjectionReadLog);
        Assert.DoesNotContain(store.ReadLog, static read => ContentBearingReads.Contains(read));
    }

    /// <summary>
    /// Seeds the nested timeline session: thirty parentless turns at the top level, three child
    /// spans and a subagent under the first roots, thirty auxiliary spans that group under
    /// <c>(auxiliary)</c>, and two spans whose declared parent is missing so they group under one
    /// <c>(orphan:...)</c> container — more than a page of nodes at two levels.
    /// </summary>
    private static void WriteTimelineSession(InMemoryRelationalStore store, string sessionKey)
    {
        List<CollectedRecord> records =
        [
            .. Enumerable.Range(0, 30).Select(i => Turn(
                $"turn-tl-{i:D3}",
                sessionKey,
                i,
                inputTokens: 1_000,
                outputTokens: 100,
                declaredContextWindow: 2_000)),
        ];

        for (int index = 0; index < 3; index++)
        {
            records.Add(TimelineSpan($"child-a-{index}", sessionKey, "turn-tl-000", 0, $"agent step {index}"));
        }

        records.Add(TimelineSpan("child-sub-agent", sessionKey, "turn-tl-001", 1, "invoke_agent subagent"));
        for (int index = 0; index < 30; index++)
        {
            records.Add(TimelineSpan($"aux-{index:D3}", sessionKey, null, 0, $"chat.title {index}"));
        }

        records.Add(TimelineSpan("orphan-1", sessionKey, "missing-parent", 0, "orphan.tool"));
        records.Add(TimelineSpan("orphan-2", sessionKey, "missing-parent", 0, "orphan.tool"));
        WriteSession(store, sessionKey, records, repo: "kyber-weave", branch: "kw-task/T7", title: "timeline session");
    }

    /// <summary>Builds one non-turn record for the timeline seed: a child, auxiliary, or orphan span.</summary>
    private static CollectedRecord TimelineSpan(
        string spanId,
        string sessionKey,
        string? parentSpanId,
        int minuteOffset,
        string name) =>
        new(
            RecordId: spanId,
            HarnessId: "claude-code",
            Op: RecordOp.Unspecified,
            Kind: RecordKind.Internal,
            Name: name,
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: parentSpanId,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: 500,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);

    /// <summary>Asserts one timeline section row carries today's node fields minus <c>children</c> plus its
    /// <c>childCount</c>, against the same node in today's <c>BuildTimeline</c> tree.</summary>
    private static void AssertTimelineRowCarriesTodaysFields(JsonElement row, JsonElement today)
    {
        Assert.Equal(today.GetProperty("spanId").GetString(), row.GetProperty("spanId").GetString());
        Assert.Equal(today.GetProperty("parentId").GetRawText(), row.GetProperty("parentId").GetRawText());
        Assert.Equal(today.GetProperty("startMs").GetInt64(), row.GetProperty("startMs").GetInt64());
        Assert.Equal(today.GetProperty("durationMs").GetInt64(), row.GetProperty("durationMs").GetInt64());
        Assert.Equal(today.GetProperty("kind").GetString(), row.GetProperty("kind").GetString());
        Assert.Equal(today.GetProperty("name").GetString(), row.GetProperty("name").GetString());
        Assert.Equal(today.GetProperty("status").GetRawText(), row.GetProperty("status").GetRawText());
        Assert.Equal(today.GetProperty("attributes").GetRawText(), row.GetProperty("attributes").GetRawText());
        Assert.Equal(today.GetProperty("isSubagent").GetBoolean(), row.GetProperty("isSubagent").GetBoolean());
        Assert.Equal(today.GetProperty("isAuxiliary").GetBoolean(), row.GetProperty("isAuxiliary").GetBoolean());
        Assert.Equal(today.GetProperty("cost").GetRawText(), row.GetProperty("cost").GetRawText());
        Assert.Equal(today.GetProperty("children").GetArrayLength(), row.GetProperty("childCount").GetInt32());
        Assert.False(row.TryGetProperty("children", out _));
    }

    /// <summary>The whole tree's extent: the earliest node start and the latest node end.</summary>
    private static (long StartMs, long EndMs) TimelineTreeExtent(JsonElement[] topLevel)
    {
        long start = long.MaxValue;
        long end = long.MinValue;
        void Walk(JsonElement node)
        {
            start = Math.Min(start, node.GetProperty("startMs").GetInt64());
            end = Math.Max(end, node.GetProperty("startMs").GetInt64() + node.GetProperty("durationMs").GetInt64());
            if (node.TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }

        foreach (JsonElement node in topLevel)
        {
            Walk(node);
        }

        return (start == long.MaxValue ? 0 : start, end == long.MinValue ? 0 : end);
    }

    /// <summary>
    /// Today's timeline tree: <see cref="CanonContextAnalysis.BuildTimeline"/> over the session's
    /// content-free records — the tree the timeline read model prices — serialized to the row
    /// shape the detail route used to serve, so the section's pages are compared field for field.
    /// </summary>
    private static JsonElement[] TodayTimelineNodes(InMemoryRelationalStore store, string sessionKey)
    {
        List<CanonicalRecord> records =
            [.. store.SessionRecordsContentFree(sessionKey).Select(CanonRecordHydration.Hydrate)];
        records.Sort(static (left, right) =>
        {
            int byTimestamp = string.CompareOrdinal(left.Timestamp, right.Timestamp);
            return byTimestamp != 0 ? byTimestamp : string.CompareOrdinal(left.SpanId, right.SpanId);
        });

        TimelineNode root = CanonContextAnalysis.BuildTimeline(records);
        return [.. root.Children.Select(TodayTimelineNode)];
    }

    private static JsonElement TodayTimelineNode(TimelineNode node) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["spanId"] = node.SpanId,
            ["parentId"] = node.ParentId,
            ["startMs"] = node.StartMs,
            ["durationMs"] = node.DurationMs,
            ["kind"] = node.Kind,
            ["name"] = node.Name,
            ["status"] = node.Status,
            ["attributes"] = node.Attributes,
            ["isSubagent"] = node.IsSubagent,
            ["isAuxiliary"] = node.IsAuxiliary,
            ["cost"] = node.Cost,
            ["children"] = node.Children.Select(TodayTimelineNode).ToList(),
        });

    /// <summary>Seeds one batch of ingest problems attached to <paramref name="sessionKey"/>.</summary>
    private static void WriteProblems(InMemoryRelationalStore store, string sessionKey, int count)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions: [],
            Records: [],
            Problems:
            [
                .. Enumerable.Range(0, count).Select(i => new CollectedProblem(
                    $"problem-{sessionKey}-{i:D2}",
                    "error",
                    "INGEST_TEST",
                    $"problem {i}",
                    null,
                    null,
                    sessionKey,
                    "claude-code",
                    null,
                    null)),
            ]));
    }

    /// <summary>Seeds the 128-turn section session: measurable context, tool figures, and one skill invocation.</summary>
    private static void WriteSectionSession(InMemoryRelationalStore store, string sessionKey)
    {
        List<CollectedRecord> turns =
        [
            .. Enumerable.Range(0, 128).Select(i => Turn(
                $"turn-sec-{i:D3}",
                sessionKey,
                i,
                inputTokens: 1_000 + i,
                cacheRead: 200 + i,
                outputTokens: 100 + i,
                model: i % 2 == 0 ? "claude-sonnet-4-6" : "gpt-5.1-codex",
                reportedCostUsd: 0.01,
                parts: [TextPart(0, "user_prompt", new string('x', 4_096))],
                declaredContextWindow: 2_000)),
        ];
        turns[0] = WithToolFigures(turns[0]);
        turns[5] = turns[5] with { SkillsInvoked = ["commit"] };

        // The last turn's reported input disagrees with its tokenized input, so the first and
        // last context edges price the records' reported input, not the stored measured input.
        turns[127] = turns[127] with { ReportedInputTokens = 900 };
        WriteSession(store, sessionKey, turns, repo: "kyber-weave", branch: "kw-task/T4", title: "section session");
    }

    /// <summary>Attaches one tool invocation ("read") and two tool definitions ("read", "write").</summary>
    private static CollectedRecord WithToolFigures(CollectedRecord record) =>
        record with
        {
            Tool = new CollectedToolCall(
                CallId: "call-sections",
                ToolName: "read",
                McpServerName: null,
                McpToolName: null,
                Input: new CollectedToolContent("tool input text", 15, 15, null, null),
                Result: new CollectedToolContent("tool result text", 16, 16, null, null),
                Succeeded: true,
                Error: null,
                ExitCode: 0),
            ToolDefinitions =
            [
                new CollectedToolDefinition(0, "read", null, 120),
                new CollectedToolDefinition(1, "write", null, 80),
            ],
        };

    /// <summary>
    /// Seeds a thirty-turn session with thirty advertised tools — two invoked, the rest never
    /// invoked, and three servers' worth of definitions — so the tools section has more rows than
    /// a page holds.
    /// </summary>
    private static void WriteManyToolSession(InMemoryRelationalStore store, string sessionKey)
    {
        List<CollectedRecord> turns =
        [
            .. Enumerable.Range(0, 30).Select(i => Turn($"turn-tools-{i:D3}", sessionKey, i, inputTokens: 1_000, outputTokens: 100)),
        ];
        turns[0] = turns[0] with
        {
            Tool = ToolCall("call-tools-0", "tool-00"),
            ToolDefinitions =
            [
                .. Enumerable.Range(0, 30).Select(i => new CollectedToolDefinition(
                    i,
                    $"tool-{i:D2}",
                    i % 3 == 0 ? "mcp-alpha" : i % 3 == 1 ? "mcp-beta" : null,
                    100 + i)),
            ],
        };
        turns[1] = turns[1] with { Tool = ToolCall("call-tools-1", "tool-29") };
        WriteSession(store, sessionKey, turns, repo: "kyber-weave", branch: "kw-task/T6", title: "tools session");
    }

    private static CollectedToolCall ToolCall(string callId, string toolName) => new(
        CallId: callId,
        ToolName: toolName,
        McpServerName: null,
        McpToolName: null,
        Input: null,
        Result: null,
        Succeeded: true,
        Error: null,
        ExitCode: 0);

    private static KyberBridgeEngine CreateBridge(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing),
            clock: () => TurnBase.AddHours(2),
            instanceId: "test-instance");

    private static CanonProjectionService CreateProjection(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing));

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code",
        string? repo = null,
        string? branch = null,
        string? title = null)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: repo is null && branch is null
                        ? null
                        : new CollectedWorkspace("/tmp/working-directory", repo, branch),
                    Title: title,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        string? model = "claude-sonnet-4-6",
        double? reportedCostUsd = null,
        IReadOnlyList<CollectedContentPart>? parts = null,
        long? declaredContextWindow = null,
        string harnessId = "claude-code")
    {
        List<CollectedTokenCount> tokens =
        [
            new CollectedTokenCount(TokenClass.Input, inputTokens),
            new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
            new CollectedTokenCount(TokenClass.CacheWrite, 0),
            new CollectedTokenCount(TokenClass.Output, outputTokens),
        ];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: reportedCostUsd.HasValue
                ? new CollectedCost(reportedCostUsd.Value, "USD")
                : null,
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

    private static CollectedContentPart TextPart(int position, string bucket, string text) =>
        new(
            Position: position,
            Bucket: PromptBucket(bucket),
            PlainText: text,
            Withheld: null,
            ReportedTokens: null,
            Server: null,
            Role: null,
            Resident: null,
            OriginalUtf8BytesWhenTruncated: null);

    private static ContentBucket PromptBucket(string bucket) =>
        bucket switch
        {
            "user_prompt" => ContentBucket.UserPrompt,
            "user_message" => ContentBucket.UserMessage,
            "conversation_history" => ContentBucket.ConversationHistory,
            "tool_result_content" => ContentBucket.ToolResultContent,
            _ => throw new ArgumentException($"Unexpected bucket '{bucket}'.", nameof(bucket)),
        };

    /// <summary>
    /// In-memory store whose timeline records read fails for one session, so a test can pin that
    /// the timeline section isolates a faulted read to its session.
    /// </summary>
    private sealed class TimelineFaultingStore : InMemoryRelationalStore
    {
        public override IReadOnlyList<CollectedRecord> SessionTimelineRecords(string sessionKey)
        {
            if (string.Equals(sessionKey, "sess-timeline-broken", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("projection exploded");
            }

            return base.SessionTimelineRecords(sessionKey);
        }
    }

    /// <summary>
    /// In-memory store recording every scoped prompt-preview read's span ids, so a test can pin
    /// that <see cref="KyberBridgeEngine.GetSessionRequests"/> previews only the page's requests.
    /// </summary>
    private sealed class PreviewCapturingStore : InMemoryRelationalStore
    {
        /// <summary>The span ids each <c>RequestPromptPreviews</c> call received, in call order.</summary>
        public List<IReadOnlyList<string>> PromptPreviewReads { get; } = [];

        public override IReadOnlyList<StoredRequestPromptPreview> RequestPromptPreviews(string sessionKey, IReadOnlyCollection<string> requestSpanIds)
        {
            PromptPreviewReads.Add([.. requestSpanIds]);
            return base.RequestPromptPreviews(sessionKey, requestSpanIds);
        }
    }
}
