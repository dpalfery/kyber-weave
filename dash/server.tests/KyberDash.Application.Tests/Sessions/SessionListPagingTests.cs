using System.Globalization;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Sessions;

/// <summary>
/// Server-side paging for the sessions list: <see cref="KyberBridgeEngine.GetSessionPage"/> and
/// <see cref="KyberBridgeEngine.GetSubagentPage"/> answer one page of roots that is filtered,
/// searched, and paged in the store, price only the keys on the page, and attach the first 25
/// subagents of every page root together with each root's child total.
/// </summary>
public sealed class SessionListPagingTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Page 2 of 250 roots answers exactly its 100 rows in started-descending order with the
    /// filtered root total, prices only the page's keys through the keyed figures read, and issues
    /// the page reads — the header page, the subagent preview, the keyed header fetch, and the
    /// keyed figures pass — without reading any session row or projection list per row.
    /// </summary>
    [Fact]
    public void TheSessionsListPagesRootsInSqlAndPricesOnlyThePage()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new(snapshot: new Dictionary<string, ModelCosts>(StringComparer.Ordinal)
        {
            ["claude-sonnet-4-6"] = new ModelCosts(
                InputCostPerToken: 0.000003,
                OutputCostPerToken: 0.000015,
                CacheWriteCostPerToken: 0.00000375,
                CacheReadCostPerToken: 0.0000003,
                WebSearchCostPerRequest: 0,
                FastMultiplier: 1,
                CacheWriteCostIsExplicit: true),
        });
        SeedRoots(store, rootCount: 250);
        SeedSubagentRows(store, rootCount: 250, everyNth: 10);
        _ = CreateProjection(store, pricing).ProjectAll();
        MarkSubagents(store, rootCount: 250, everyNth: 10);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        _ = bridge.GetSessionPage(page: 1, limit: 100);

        store.ClearReadLog();
        JsonElement pageTwo = bridge.GetSessionPage(page: 2, limit: 100);

        JsonElement rows = pageTwo.GetProperty("sessions");
        Assert.Equal(100, rows.GetArrayLength());
        Assert.Equal(250, pageTwo.GetProperty("total").GetInt32());
        Assert.Equal(2, pageTwo.GetProperty("page").GetInt32());
        Assert.Equal(100, pageTwo.GetProperty("limit").GetInt32());
        Assert.Equal("sess-root-149", rows[0].GetProperty("session_id").GetString());
        Assert.Equal("sess-root-050", rows[99].GetProperty("session_id").GetString());

        // The page's rows keep today's figures: only the page was priced.
        Assert.Equal(400, rows[0].GetProperty("total_input").GetInt64());
        Assert.Equal(60, rows[0].GetProperty("total_output").GetInt64());

        // EnsureProjected's steady-state guards precede the page — IsProjectionCurrent and the
        // rollup-missing check each confirm projections exist — and removing those guard reads is
        // out of this task's scope. The page build itself is the tail that follows them: exactly
        // the header page, the one-statement preview, and the keyed header fetch, with no
        // projection list or single-projection read of its own.
        Assert.Equal(
            (IReadOnlyList<string>)
            [
                "ListSessionProjections",
                "ListSessionProjections",
                nameof(InMemoryRelationalStore.SessionHeaderPage),
                nameof(InMemoryRelationalStore.SessionChildPreview),
                nameof(InMemoryRelationalStore.SessionHeadersByKeys),
            ],
            store.SessionProjectionReadLog);
        Assert.DoesNotContain(
            store.ReadLog,
            static read => read is "GetSession" or "Sessions" or "SessionSummaryFigures" or "ListSessionProjections");

        // The keyed figures pass carried exactly the page's roots plus their preview children.
        string[] expectedRoots = [.. Enumerable.Range(50, 100).Reverse().Select(static i => $"sess-root-{i:D3}")];
        string[] expectedChildren = [.. Enumerable.Range(50, 100)
            .Where(static i => i % 10 == 0)
            .Select(static i => $"sess-root-{i:D3}-sub")];
        HashSet<string> expectedKeys = [.. expectedRoots.Concat(expectedChildren)];
        IReadOnlyList<IReadOnlyList<string>> keyedReads = store.KeyedSessionFigureReads;
        Assert.Single(keyedReads);
        Assert.Equal(expectedKeys, new HashSet<string>(keyedReads[0], StringComparer.Ordinal));

        JsonElement rootWithChild = Assert.Single(rows.EnumerateArray(), static row => row.GetProperty("session_id").GetString() == "sess-root-140");
        JsonElement subagents = rootWithChild.GetProperty("subagents");
        JsonElement attached = Assert.Single(subagents.EnumerateArray());
        Assert.Equal("sess-root-140-sub", attached.GetProperty("session_id").GetString());
        Assert.Equal(1, rootWithChild.GetProperty("subagent_total").GetInt32());

        JsonElement childless = Assert.Single(rows.EnumerateArray(), static row => row.GetProperty("session_id").GetString() == "sess-root-149");
        Assert.Empty(childless.GetProperty("subagents").EnumerateArray());
        Assert.Equal(0, childless.GetProperty("subagent_total").GetInt32());
    }

    /// <summary>
    /// The harness filter and the search text both scope the store read. A search containing
    /// <c>%</c>, <c>_</c>, a backslash, or a quote matches that literal text — never its wildcard
    /// or escape reading — over the label and the session id.
    /// </summary>
    [Fact]
    public void HarnessAndSearchFilterOnTheServer()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        SeedSearchSessions(store);
        _ = CreateProjection(store, pricing).ProjectAll();
        Relabel(store, "sess-percent", "rate 100% done");
        Relabel(store, "sess-wild-trap", "underXscore trap");
        Relabel(store, "sess-underscore", "under_score fix");
        Relabel(store, "sess-backslash", "log C:\\temp path");
        Relabel(store, "sess-quote", "it's quoted");
        Relabel(store, "sess-plain", "ordinary work");
        Relabel(store, "sess-codex", "rate 100% done");
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        _ = bridge.GetSessionPage();

        string[] SearchIds(string? harness, string? search) =>
        [
            .. bridge.GetSessionPage(page: 1, limit: 100, harness: harness, search: search)
                .GetProperty("sessions")
                .EnumerateArray()
                .Select(static row => row.GetProperty("session_id").GetString() ?? string.Empty),
        ];

        // The wildcard characters are data, not pattern: "under_score" matches the literal label
        // and not the label an unescaped "_" would also cover.
        Assert.Equal(["sess-underscore"], SearchIds("claude-code", "under_score"));
        Assert.Equal(["sess-percent"], SearchIds("claude-code", "100% d"));
        Assert.Equal(["sess-backslash"], SearchIds("claude-code", "C:\\te"));
        Assert.Equal(["sess-quote"], SearchIds("claude-code", "t's qu"));
        Assert.Equal(["sess-plain"], SearchIds("claude-code", "ordinary"));

        // The search also matches the session id, and a search hitting nothing answers an empty
        // page with the matching total.
        Assert.Equal(["sess-plain"], SearchIds("claude-code", "sess-pl"));
        Assert.Empty(SearchIds("claude-code", "nothing matches this"));

        // The harness filter composes with the search: the codex session carries the same label
        // but leaves the claude-code scope, and answers under the codex scope alone.
        Assert.Equal(6, bridge.GetSessionPage(page: 1, limit: 100, harness: "claude-code").GetProperty("total").GetInt32());
        Assert.Equal(["sess-percent"], SearchIds("claude-code", "100% d"));
        Assert.Equal(["sess-codex"], SearchIds("codex", "100% d"));
        Assert.Equal(["sess-codex", "sess-percent"], SearchIds(null, "100% d"));
    }

    /// <summary>
    /// A root with 30 children answers the first 25 children inline — priced like the page — with
    /// the child total the client needs to offer the drill-down.
    /// </summary>
    [Fact]
    public void SubagentsAttachWithTheFirst25AndATotal()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        SeedRootWithChildren(store, "sess-big", childCount: 30);
        _ = CreateProjection(store, pricing).ProjectAll();
        MarkChildren(store, "sess-big", [.. Enumerable.Range(0, 30).Select(static k => $"sess-big-k{k:D2}")]);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        _ = bridge.GetSessionPage();

        JsonElement root = Assert.Single(bridge.GetSessionPage().GetProperty("sessions").EnumerateArray());

        Assert.Equal("sess-big", root.GetProperty("session_id").GetString());
        Assert.Equal(30, root.GetProperty("subagent_total").GetInt32());
        JsonElement subagents = root.GetProperty("subagents");
        Assert.Equal(25, subagents.GetArrayLength());
        Assert.Equal("sess-big-k00", subagents[0].GetProperty("session_id").GetString());
        Assert.Equal("sess-big-k24", subagents[24].GetProperty("session_id").GetString());
        Assert.All(subagents.EnumerateArray(), static child => Assert.True(child.GetProperty("is_subagent").GetBoolean()));
    }

    /// <summary>The subagent route pages a root's children at 25, answering the child total.</summary>
    [Fact]
    public void SubagentPagesAnswer25ByPage()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        SeedRootWithChildren(store, "sess-big", childCount: 30);
        _ = CreateProjection(store, pricing).ProjectAll();
        MarkChildren(store, "sess-big", [.. Enumerable.Range(0, 30).Select(static k => $"sess-big-k{k:D2}")]);
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        _ = bridge.GetSessionPage();

        JsonElement pageOne = bridge.GetSubagentPage("sess-big", page: 1);
        JsonElement pageOneRows = pageOne.GetProperty("subagents");
        Assert.Equal(25, pageOneRows.GetArrayLength());
        Assert.Equal(30, pageOne.GetProperty("total").GetInt32());
        Assert.Equal(1, pageOne.GetProperty("page").GetInt32());
        Assert.Equal(25, pageOne.GetProperty("limit").GetInt32());
        Assert.Equal("sess-big-k00", pageOneRows[0].GetProperty("session_id").GetString());
        Assert.Equal("sess-big-k24", pageOneRows[24].GetProperty("session_id").GetString());

        JsonElement pageTwo = bridge.GetSubagentPage("sess-big", page: 2);
        JsonElement pageTwoRows = pageTwo.GetProperty("subagents");
        Assert.Equal(5, pageTwoRows.GetArrayLength());
        Assert.Equal(30, pageTwo.GetProperty("total").GetInt32());
        Assert.Equal("sess-big-k25", pageTwoRows[0].GetProperty("session_id").GetString());
        Assert.Equal("sess-big-k29", pageTwoRows[4].GetProperty("session_id").GetString());
    }

    /// <summary>A page past the end answers an empty items array while keeping the filtered total.</summary>
    [Fact]
    public void APagePastTheEndAnswersEmptyWithTheTotal()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        SeedRoots(store, rootCount: 250);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        _ = bridge.GetSessionPage();

        JsonElement past = bridge.GetSessionPage(page: 4, limit: 100);

        Assert.Empty(past.GetProperty("sessions").EnumerateArray());
        Assert.Equal(250, past.GetProperty("total").GetInt32());
        Assert.Equal(4, past.GetProperty("page").GetInt32());
    }

    /// <summary>An explicit small limit answers that many rows from the top, with the full total.</summary>
    [Fact]
    public void LimitTenStillAnswersTheFirstTenRoots()
    {
        InMemoryRelationalStore store = new();
        FakePricingRepository pricing = new();
        SeedRoots(store, rootCount: 250);
        _ = CreateProjection(store, pricing).ProjectAll();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        _ = bridge.GetSessionPage();

        JsonElement limited = bridge.GetSessionPage(page: 1, limit: 10);
        JsonElement rows = limited.GetProperty("sessions");

        Assert.Equal(10, rows.GetArrayLength());
        Assert.Equal(250, limited.GetProperty("total").GetInt32());
        Assert.Equal(10, limited.GetProperty("limit").GetInt32());
        Assert.Equal("sess-root-249", rows[0].GetProperty("session_id").GetString());
        Assert.Equal("sess-root-240", rows[9].GetProperty("session_id").GetString());
    }

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

    private static void SeedRoots(InMemoryRelationalStore store, int rootCount)
    {
        for (int i = 0; i < rootCount; i++)
        {
            string key = $"sess-root-{i:D3}";
            WriteSession(store, key, [Turn($"span-root-{i:D3}", key, i, inputTokens: 400, outputTokens: 60)]);
        }
    }

    private static void SeedSubagentRows(InMemoryRelationalStore store, int rootCount, int everyNth)
    {
        for (int i = 0; i < rootCount; i += everyNth)
        {
            string childKey = $"sess-root-{i:D3}-sub";
            WriteSession(store, childKey, [Turn($"span-root-{i:D3}-sub", childKey, i + 1, inputTokens: 40, outputTokens: 6)]);
        }
    }

    private static void MarkSubagents(InMemoryRelationalStore store, int rootCount, int everyNth)
    {
        for (int i = 0; i < rootCount; i += everyNth)
        {
            MarkChildren(store, $"sess-root-{i:D3}", [$"sess-root-{i:D3}-sub"]);
        }
    }

    private static void SeedRootWithChildren(InMemoryRelationalStore store, string rootKey, int childCount)
    {
        WriteSession(store, rootKey, [Turn("span-big", rootKey, 0, inputTokens: 400, outputTokens: 60)]);
        for (int k = 0; k < childCount; k++)
        {
            string childKey = $"sess-big-k{k:D2}";
            WriteSession(store, childKey, [Turn($"span-big-k{k:D2}", childKey, k + 1, inputTokens: 40, outputTokens: 6)]);
        }
    }

    private static void SeedSearchSessions(InMemoryRelationalStore store)
    {
        WriteSession(store, "sess-percent", [Turn("span-percent", "sess-percent", 1, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-wild-trap", [Turn("span-wild-trap", "sess-wild-trap", 2, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-underscore", [Turn("span-underscore", "sess-underscore", 3, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-backslash", [Turn("span-backslash", "sess-backslash", 4, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-quote", [Turn("span-quote", "sess-quote", 5, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-plain", [Turn("span-plain", "sess-plain", 6, inputTokens: 100, outputTokens: 10)]);
        WriteSession(store, "sess-codex", [Turn("span-codex", "sess-codex", 7, inputTokens: 100, outputTokens: 10, harnessId: "codex")], harnessId: "codex");
    }

    /// <summary>Rewrites one projected label, after the projection pass, so a search seed controls its text.</summary>
    private static void Relabel(InMemoryRelationalStore store, string sessionKey, string label)
    {
        StoredSessionProjection existing = store.GetSessionProjection(sessionKey)
            ?? throw new InvalidOperationException($"No projection for {sessionKey}.");
        ((IProjectionStore)store).ReplaceSessionProjection(existing with { Label = label });
    }

    /// <summary>
    /// Rewrites the child projections with their parent link, after the projection pass, so the
    /// tree shape is the seed's rather than whatever the records alone imply.
    /// </summary>
    private static void MarkChildren(InMemoryRelationalStore store, string parentKey, IReadOnlyList<string> childKeys)
    {
        foreach (string childKey in childKeys)
        {
            StoredSessionProjection existing = store.GetSessionProjection(childKey)
                ?? throw new InvalidOperationException($"No projection for {childKey}.");
            ((IProjectionStore)store).ReplaceSessionProjection(existing with
            {
                IsSubagent = true,
                ParentSessionId = parentKey,
                AgentName = "worker",
            });
        }
    }

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code")
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions: [new CollectedSession(sessionKey, harnessId, sessionKey, null, null, [])],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens = 0,
        long outputTokens = 0,
        string harnessId = "claude-code")
    {
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
            Model: "claude-sonnet-4-6",
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, inputTokens),
                new CollectedTokenCount(TokenClass.CacheRead, 0),
                new CollectedTokenCount(TokenClass.CacheWrite, 0),
                new CollectedTokenCount(TokenClass.Output, outputTokens),
            ],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: inputTokens,
            ReportedOutputTokens: outputTokens);
    }
}
