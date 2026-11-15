using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberDash.Contracts.Services;
using KyberDash.Domain;
using KyberDash.Mcp;
using Moq;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Exercises the JSON-RPC stdio protocol and the tool surface of <c>kyberdash-mcp</c>, with the
/// analytics service mocked and no canonical store on disk.
/// </summary>
public class McpStdioServerTests
{
    private static readonly string[] AnalyticsOperationNames = ["status", "catalog"];

    private const string ToolPrefix = "dash_";

    private static readonly string[] ExpectedToolNames =
    [
        "dash_list_available_analytics_operations",
        "dash_list_queryable_metrics_dimensions_limits",
        "dash_query_session_cost_tokens_and_duration",
        "dash_search_sessions_by_text_and_scope",
        "dash_read_captured_run_session_or_turn_context",
        "dash_list_detected_context_and_tool_problems",
        "dash_compare_metrics_between_two_scopes",
        "dash_get_run_success_or_failure_outcomes",
    ];

    [Fact]
    public async Task InitializeReportsTheProtocolVersionTheClientAskedFor()
    {
        using Harness harness = new();

        JsonNode? response = await harness.SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");

        Assert.NotNull(response);
        JsonNode result = response!["result"]!;
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("kyberdash-analytics", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task InitializedNotificationIsNotAnswered()
    {
        using Harness harness = new();

        JsonNode? response = await harness.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Null(response);
    }

    [Fact]
    public async Task ToolsListAdvertisesAllEightAnalyticsToolsWithSchemas()
    {
        using Harness harness = new();

        JsonNode? response = await harness.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        Assert.NotNull(response);
        JsonArray tools = response!["result"]!["tools"]!.AsArray();
        Assert.Equal(8, tools.Count);

        List<string> names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(ExpectedToolNames, names);

        foreach (JsonNode? tool in tools)
        {
            Assert.StartsWith(ToolPrefix, tool!["name"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.NotNull(tool["description"]);
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task StatusToolExecutesDirectlyThroughTheAnalyticsService()
    {
        JsonElement envelope = ContractEnvelope(new { operations = AnalyticsOperationNames });
        Mock<IAnalyticsService> analytics = new();
        analytics
            .Setup(a => a.Execute("status", It.IsAny<JsonElement?>()))
            .Returns(new AnalyticsExecutionResult(200, envelope));

        using Harness harness = new(analytics.Object, liveServer: null);

        JsonNode? response = await harness.SendAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"dash_list_available_analytics_operations","arguments":{}}}""");

        Assert.NotNull(response);
        JsonNode result = response!["result"]!;
        Assert.Null(result["isError"]);
        Assert.Equal(1, result["structuredContent"]!["contractVersion"]!.GetValue<int>());
        Assert.Contains("contractVersion", result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        analytics.Verify(a => a.Execute("status", null), Times.Once);
    }

    [Fact]
    public async Task QueryToolReshapesArgumentsBeforeExecuting()
    {
        JsonElement? captured = null;
        Mock<IAnalyticsService> analytics = new();
        analytics
            .Setup(a => a.Execute("query", It.IsAny<JsonElement?>()))
            .Callback<string, JsonElement?>((_, request) => captured = request)
            .Returns(new AnalyticsExecutionResult(200, ContractEnvelope(new { rows = Array.Empty<object>() })));

        using Harness harness = new(analytics.Object, liveServer: null);

        JsonNode? response = await harness.SendAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"dash_query_session_cost_tokens_and_duration","arguments":{"metrics":["cost.usd"],"groups":["harness"],"limit":5}}}""");

        Assert.NotNull(response);
        Assert.NotNull(captured);

        JsonElement body = captured!.Value;
        Assert.Equal("cost.usd", body.GetProperty("metrics")[0].GetProperty("id").GetString());
        Assert.Equal("harness", body.GetProperty("groupBy")[0].GetString());
        Assert.False(body.TryGetProperty("groups", out _));
        Assert.Equal(1, body.GetProperty("catalogVersion").GetInt32());
    }

    [Fact]
    public async Task SearchToolRenamesTextToQuery()
    {
        JsonElement? captured = null;
        Mock<IAnalyticsService> analytics = new();
        analytics
            .Setup(a => a.Execute("search-sessions", It.IsAny<JsonElement?>()))
            .Callback<string, JsonElement?>((_, request) => captured = request)
            .Returns(new AnalyticsExecutionResult(200, ContractEnvelope(new { sessions = Array.Empty<object>() })));

        using Harness harness = new(analytics.Object, liveServer: null);

        await harness.SendAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"dash_search_sessions_by_text_and_scope","arguments":{"text":"retry loop","limit":10}}}""");

        Assert.NotNull(captured);
        Assert.Equal("retry loop", captured!.Value.GetProperty("query").GetString());
        Assert.False(captured.Value.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task WindowDaysBecomesAnExplicitScopeRange()
    {
        JsonElement? captured = null;
        Mock<IAnalyticsService> analytics = new();
        analytics
            .Setup(a => a.Execute("findings", It.IsAny<JsonElement?>()))
            .Callback<string, JsonElement?>((_, request) => captured = request)
            .Returns(new AnalyticsExecutionResult(200, ContractEnvelope(new { findings = Array.Empty<object>() })));

        using Harness harness = new(analytics.Object, liveServer: null);

        await harness.SendAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"dash_list_detected_context_and_tool_problems","arguments":{"windowDays":7}}}""");

        Assert.NotNull(captured);
        JsonElement scope = captured!.Value.GetProperty("scope");
        Assert.True(scope.TryGetProperty("from", out JsonElement from));
        Assert.True(scope.TryGetProperty("to", out JsonElement to));
        Assert.True(DateTimeOffset.TryParse(from.GetString(), System.Globalization.CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset start));
        Assert.True(DateTimeOffset.TryParse(to.GetString(), System.Globalization.CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset end));
        Assert.InRange((end - start).TotalDays, 6.99, 7.01);
    }

    [Fact]
    public async Task AnEnvelopeWithAnErrorIsReportedAsAToolError()
    {
        JsonElement envelope = JsonSerializer.SerializeToElement(new
        {
            contractVersion = 1,
            scope = new { },
            data = (object?)null,
            measurement = new { },
            coverage = new { },
            freshness = new { },
            evidence = Array.Empty<object>(),
            pagination = new { },
            error = new { code = "invalid_request", message = "cursor is not valid" },
        });

        Mock<IAnalyticsService> analytics = new();
        analytics
            .Setup(a => a.Execute(It.IsAny<string>(), It.IsAny<JsonElement?>()))
            .Returns(new AnalyticsExecutionResult(400, envelope));

        using Harness harness = new(analytics.Object, liveServer: null);

        JsonNode? response = await harness.SendAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"dash_list_detected_context_and_tool_problems","arguments":{}}}""");

        Assert.NotNull(response);
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("invalid_request", response["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownToolIsAnInvalidParamsError()
    {
        using Harness harness = new();

        JsonNode? response = await harness.SendAsync(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"dash_not_a_tool","arguments":{}}}""");

        Assert.NotNull(response);
        Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ResourcesAndPromptsAreAdvertisedAsEmpty()
    {
        using Harness harness = new();

        JsonNode? resources = await harness.SendAsync("""{"jsonrpc":"2.0","id":9,"method":"resources/list"}""");
        JsonNode? prompts = await harness.SendAsync("""{"jsonrpc":"2.0","id":10,"method":"prompts/list"}""");

        Assert.Empty(resources!["result"]!["resources"]!.AsArray());
        Assert.Empty(prompts!["result"]!["prompts"]!.AsArray());
    }

    [Fact]
    public async Task PingAndUnknownMethodsBehaveAsJsonRpcRequires()
    {
        using Harness harness = new();

        JsonNode? ping = await harness.SendAsync("""{"jsonrpc":"2.0","id":11,"method":"ping"}""");
        JsonNode? unknown = await harness.SendAsync("""{"jsonrpc":"2.0","id":12,"method":"sampling/createMessage"}""");

        Assert.NotNull(ping!["result"]);
        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedJsonIsAParseError()
    {
        using Harness harness = new();

        JsonNode? response = await harness.SendAsync("{not json");

        Assert.NotNull(response);
        Assert.Equal(-32700, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsyncWritesOneResponsePerRequestAndEndsAtEndOfInput()
    {
        JsonElement envelope = ContractEnvelope(new { ok = true });
        Mock<IAnalyticsService> analytics = new();
        analytics
            .Setup(a => a.Execute(It.IsAny<string>(), It.IsAny<JsonElement?>()))
            .Returns(new AnalyticsExecutionResult(200, envelope));

        using HttpClient httpClient = new();
        McpStdioHost host = new(analytics.Object, IdleLifecycle().Object, httpClient);

        using StringReader input = new(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""" + "\n"
            + """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n"
            + """{"jsonrpc":"2.0","id":2,"method":"ping"}""" + "\n");
        using StringWriter output = new();

        int exitCode = await host.RunAsync(input, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("{\"jsonrpc\":\"2.0\",", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANonLoopbackServerUrlIsIgnoredAndTheDirectPathIsUsed()
    {
        // A rejected --server-url must never become an outbound request to another host.
        JsonElement envelope = ContractEnvelope(new { operations = Array.Empty<string>() });
        Mock<IAnalyticsService> analytics = new();
        analytics
            .Setup(a => a.Execute(It.IsAny<string>(), It.IsAny<JsonElement?>()))
            .Returns(new AnalyticsExecutionResult(200, envelope));

        using HttpClient httpClient = new();
        McpStdioHost host = new(analytics.Object, IdleLifecycle().Object, httpClient, "http://example.com:4747");
        JsonNode? response = await host.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dash_list_available_analytics_operations","arguments":{}}}""",
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.Null(response!["result"]!["isError"]);
        analytics.Verify(a => a.Execute("status", null), Times.Once);
    }

    [Fact]
    public async Task AValidLoopbackServerUrlIsUsedInsteadOfExecutingDirectly()
    {
        // Point the explicit URL at a loopback port nothing listens on: the forwarded request must
        // fail, which proves the host tried the server and did not fall back to the direct path.
        Mock<IAnalyticsService> analytics = new();
        using HttpClient httpClient = new();
        McpStdioHost host = new(analytics.Object, IdleLifecycle().Object, httpClient, "http://127.0.0.1:1");

        JsonNode? response = await host.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dash_list_available_analytics_operations","arguments":{}}}""",
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.StartsWith("engine_unavailable:", response!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        analytics.Verify(a => a.Execute(It.IsAny<string>(), It.IsAny<JsonElement?>()), Times.Never);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4747", true)]
    [InlineData("http://[::1]:4747/", true)]
    [InlineData("http://127.0.0.1:4747/", true)]
    [InlineData("https://127.0.0.1:4747", false)]
    [InlineData("http://example.com:4747", false)]
    [InlineData("http://127.0.0.1:4747/path", false)]
    [InlineData("http://127.0.0.1:4747?q=1", false)]
    [InlineData("http://user:secret@127.0.0.1:4747", false)]
    public void LoopbackServerUrlValidationRejectsAnythingThatIsNotLiteralLoopback(string candidate, bool expected)
    {
        Assert.Equal(expected, McpStdioHost.IsAllowedLoopbackServerUrl(new Uri(candidate, UriKind.Absolute)));
    }

    [Fact]
    public void ProgramOptionsParseTheDocumentedFlags()
    {
        McpOptions options = McpOptions.Parse(
            ["--server-url", "http://127.0.0.1:4747", "--state-dir", "/tmp/kyberdash-state", "--db", "/tmp/canon.db", "--verbose", "--timezone", "Europe/London"]);

        Assert.Equal(new Uri("http://127.0.0.1:4747"), options.ServerUrl);
        Assert.Equal("/tmp/kyberdash-state", options.StateDirectory);
        Assert.True(options.Verbose);
        Assert.Equal("Europe/London", options.TimeZone);
    }

    [Fact]
    public void ProgramOptionsRejectANonLoopbackServerUrl()
    {
        Assert.Throws<KyberDash.Core.Exceptions.DashValidationException>(
            () => McpOptions.Parse(["--server-url", "http://example.com:4747"]));
    }

    private static Mock<IEngineLifecycleService> IdleLifecycle()
    {
        Mock<IEngineLifecycleService> lifecycle = new();
        lifecycle
            .Setup(l => l.ResolveLiveServerUriAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Uri?)null);
        return lifecycle;
    }

    /// <summary>A minimal valid contract-v1 analytics envelope wrapping <paramref name="data"/>.</summary>
    private static JsonElement ContractEnvelope(object data) => JsonSerializer.SerializeToElement(new
    {
        contractVersion = 1,
        scope = new { },
        data,
        measurement = new { },
        coverage = new { },
        freshness = new { },
        evidence = Array.Empty<object>(),
        pagination = new { },
    });

    /// <summary>Drives a host one JSON-RPC line at a time.</summary>
    private class Harness : IDisposable
    {
        private readonly McpStdioHost _host;
        private readonly HttpClient _httpClient;

        public Harness(IAnalyticsService? analytics = null, Uri? liveServer = null)
        {
            IAnalyticsService service = analytics ?? new Mock<IAnalyticsService>().Object;
            Mock<IEngineLifecycleService> lifecycle = new();
            lifecycle
                .Setup(l => l.ResolveLiveServerUriAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(liveServer);
            _httpClient = new HttpClient();
            _host = new McpStdioHost(service, lifecycle.Object, _httpClient);
        }

        public Task<JsonNode?> SendAsync(string line) => _host.HandleLineAsync(line, TestContext.Current.CancellationToken);

        public void Dispose() => _httpClient.Dispose();
    }
}
