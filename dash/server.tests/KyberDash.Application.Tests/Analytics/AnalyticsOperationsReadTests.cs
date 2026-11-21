using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Analytics;

/// <summary>
/// Read-path parity for the four discovery analytics operations over <see cref="AllHarnessFixtureSet"/>:
/// <see cref="KyberBridgeEngine"/> and <see cref="AnalyticsService"/> answer HTTP 200 contract-v1
/// envelopes for <c>status</c>, <c>catalog</c>, <c>query</c>, and <c>search-sessions</c> with
/// expected filtered, unfiltered, and empty values, while harnesses without a produced
/// <c>tool.invoke</c> record report tool calls as not reported (<see langword="null"/>) rather
/// than zero.
/// </summary>
public sealed class AnalyticsOperationsReadTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Status reports the fixture set's session, record, and finding counts as ready.</summary>
    [Fact]
    public void StatusAnswersFixtureSessionRecordAndFindingCounts()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("status", null);

        Assert.Equal(200, statusCode);
        AssertSuccessEnvelope(envelope);
        JsonElement value = envelope.GetProperty("data").GetProperty("value");
        Assert.True(value.GetProperty("ready").GetBoolean());
        Assert.Equal(store.Sessions().Count, value.GetProperty("sessionCount").GetInt32());
        Assert.Equal(store.CountRecords(), value.GetProperty("recordCount").GetInt32());
        Assert.Equal(store.ListFindings().Count, value.GetProperty("findingCount").GetInt32());
        Assert.Equal("test-instance", envelope.GetProperty("freshness").GetProperty("instanceId").GetString());

        AnalyticsService service = new(bridge);
        AnalyticsExecutionResult direct = service.Execute("status", null);

        Assert.Equal(200, direct.StatusCode);
        Assert.True(AnalyticsService.IsSupported("status"));
        Assert.True(AnalyticsService.IsSupported("catalog"));
        Assert.True(AnalyticsService.IsSupported("query"));
        Assert.True(AnalyticsService.IsSupported("search-sessions"));
    }

    /// <summary>Catalog advertises the four operations and every fixture harness.</summary>
    [Fact]
    public void CatalogAdvertisesOperationsDimensionsAndFixtureHarnesses()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics("catalog", null);

        Assert.Equal(200, statusCode);
        AssertSuccessEnvelope(envelope);
        JsonElement data = envelope.GetProperty("data");
        List<string> operations = [.. data.GetProperty("operations").EnumerateArray().Select(static op => op.GetString() ?? string.Empty)];
        Assert.Contains("status", operations, StringComparer.Ordinal);
        Assert.Contains("catalog", operations, StringComparer.Ordinal);
        Assert.Contains("query", operations, StringComparer.Ordinal);
        Assert.Contains("search-sessions", operations, StringComparer.Ordinal);
        Assert.Contains("harness", [.. data.GetProperty("dimensions").EnumerateArray().Select(static d => d.GetString() ?? string.Empty)], StringComparer.Ordinal);
        Assert.NotEmpty(data.GetProperty("metrics").EnumerateArray());
        List<string> harnesses = [.. data.GetProperty("source").GetProperty("harnesses").EnumerateArray().Select(static h => h.GetString() ?? string.Empty)];
        Assert.Equal(store.Harnesses(), harnesses.OrderBy(static h => h, StringComparer.Ordinal));
    }

    /// <summary>Query groups fixture sessions per harness, honoring filters and empty matches.</summary>
    [Fact]
    public void QueryGroupsSessionsByHarnessWithFilteredAndEmptyCases()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        (int unfilteredStatus, JsonElement unfiltered) = bridge.ExecuteAnalytics("query", null);

        Assert.Equal(200, unfilteredStatus);
        AssertSuccessEnvelope(unfiltered);
        List<JsonElement> groups = [.. unfiltered.GetProperty("data").GetProperty("groups").EnumerateArray()];
        Assert.NotEmpty(groups);
        Assert.Equal(groups.Count, unfiltered.GetProperty("pagination").GetProperty("returnedCount").GetInt32());
        foreach (JsonElement group in groups)
        {
            string harness = group.GetProperty("key").GetProperty("harness").GetString() ?? string.Empty;
            Assert.Equal(store.Sessions(harness).Count, group.GetProperty("sessions").GetInt32());
        }

        (int filteredStatus, JsonElement filtered) = bridge.ExecuteAnalytics("query", Json(new { harness = "codex-cli" }));

        Assert.Equal(200, filteredStatus);
        AssertSuccessEnvelope(filtered);
        JsonElement codexGroup = Assert.Single(filtered.GetProperty("data").GetProperty("groups").EnumerateArray());
        Assert.Equal("codex-cli", codexGroup.GetProperty("key").GetProperty("harness").GetString());
        Assert.Equal(store.Sessions("codex-cli").Count, codexGroup.GetProperty("sessions").GetInt32());

        (int aliasStatus, JsonElement aliased) = bridge.ExecuteAnalytics("query", Json(new { harness = AllHarnessFixtureSet.CursorAgentAlias }));

        Assert.Equal(200, aliasStatus);
        JsonElement cursorGroup = Assert.Single(aliased.GetProperty("data").GetProperty("groups").EnumerateArray());
        Assert.Equal("cursor", cursorGroup.GetProperty("key").GetProperty("harness").GetString());

        (int emptyStatus, JsonElement empty) = bridge.ExecuteAnalytics("query", Json(new { harness = "no-such-harness" }));

        Assert.Equal(200, emptyStatus);
        AssertSuccessEnvelope(empty);
        Assert.Empty(empty.GetProperty("data").GetProperty("groups").EnumerateArray());
        Assert.Equal(0, empty.GetProperty("pagination").GetProperty("returnedCount").GetInt32());
    }

    /// <summary>Search-sessions lists fixture sessions, honoring harness, text, and empty matches.</summary>
    [Fact]
    public void SearchSessionsMatchesFiltersAndEmptyQuery()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);

        (int unfilteredStatus, JsonElement unfiltered) = bridge.ExecuteAnalytics("search-sessions", null);

        Assert.Equal(200, unfilteredStatus);
        AssertSuccessEnvelope(unfiltered);
        List<JsonElement> items = [.. unfiltered.GetProperty("data").GetProperty("items").EnumerateArray()];
        Assert.Equal(store.Sessions().Count, items.Count);
        Assert.Equal(items.Count, unfiltered.GetProperty("pagination").GetProperty("returnedCount").GetInt32());
        foreach (JsonElement item in items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("sessionId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("harness").GetString()));
        }

        (int harnessStatus, JsonElement byHarness) = bridge.ExecuteAnalytics("search-sessions", Json(new { harness = "copilot-cli" }));

        Assert.Equal(200, harnessStatus);
        List<JsonElement> harnessItems = [.. byHarness.GetProperty("data").GetProperty("items").EnumerateArray()];
        Assert.NotEmpty(harnessItems);
        Assert.All(harnessItems, static item => Assert.Equal("copilot-cli", item.GetProperty("harness").GetString()));
        Assert.Contains(harnessItems, static item => item.GetProperty("sessionId").GetString() == "sess-all-copilot-cli");

        (int textStatus, JsonElement byText) = bridge.ExecuteAnalytics("search-sessions", Json(new { query = "sess-all-devin" }));

        Assert.Equal(200, textStatus);
        JsonElement devinItem = Assert.Single(byText.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("sess-all-devin", devinItem.GetProperty("sessionId").GetString());
        Assert.Equal("devin", devinItem.GetProperty("harness").GetString());

        (int emptyStatus, JsonElement empty) = bridge.ExecuteAnalytics("search-sessions", Json(new { query = "no-such-session-xyz" }));

        Assert.Equal(200, emptyStatus);
        AssertSuccessEnvelope(empty);
        Assert.Empty(empty.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal(0, empty.GetProperty("pagination").GetProperty("returnedCount").GetInt32());
    }

    /// <summary>Sessions without <c>tool.invoke</c> report tool calls as not reported, not zero.</summary>
    [Fact]
    public void SessionsWithoutToolInvokeReportNotReportedRatherThanZero()
    {
        InMemoryRelationalStore store = AllHarnessFixtureSet.CreateStore();
        FakePricingRepository pricing = new();
        KyberBridgeEngine bridge = CreateBridge(store, pricing);
        (int queryStatus, _) = bridge.ExecuteAnalytics("query", null);
        Assert.Equal(200, queryStatus);

        Assert.Empty(store.ToolCalls(sessionKey: "sess-all-devin"));

        SessionAggregateView? devin = store.GetSessionAggregate("sess-all-devin", pricing.LoadPricingTables());

        Assert.NotNull(devin);
        Assert.Null(devin.ToolCalls);
        Assert.Empty(devin.ToolsInvoked);

        SessionAggregateView? codex = store.GetSessionAggregate("sess-all-codex", pricing.LoadPricingTables());

        Assert.NotNull(codex);
        Assert.Equal(2, codex.ToolCalls);
        Assert.Contains("local_shell", codex.ToolsInvoked, StringComparer.Ordinal);

        (int devinStatus, JsonElement devinQuery) = bridge.ExecuteAnalytics("query", Json(new { harness = "devin" }));

        Assert.Equal(200, devinStatus);
        JsonElement devinGroup = Assert.Single(devinQuery.GetProperty("data").GetProperty("groups").EnumerateArray());
        Assert.Equal("devin", devinGroup.GetProperty("key").GetProperty("harness").GetString());
        Assert.Equal(1, devinGroup.GetProperty("sessions").GetInt32());
    }

    private static KyberBridgeEngine CreateBridge(InMemoryRelationalStore store, FakePricingRepository pricing) =>
        new(
            store,
            store,
            store,
            pricing,
            new CanonSessionProjectorEngine(store, store, store, pricing),
            new CanonRollupsEngine(store, store, pricing),
            clock: () => FixedTimestamp,
            instanceId: "test-instance");

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static void AssertSuccessEnvelope(JsonElement envelope)
    {
        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);
        Assert.Equal(1, envelope.GetProperty("contractVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("scope").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("measurement").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("coverage").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("freshness").ValueKind);
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("evidence").ValueKind);
        Assert.Equal(JsonValueKind.Object, envelope.GetProperty("pagination").ValueKind);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
    }
}
