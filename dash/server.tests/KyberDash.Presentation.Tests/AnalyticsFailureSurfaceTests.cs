using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberDash.Application;
using KyberDash.Contracts.Services;
using KyberDash.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Holds the two failure surfaces of the analytics boundary: the HTTP endpoint always answers a
/// JSON error body, and the MCP adapter reports the engine's real status and body instead of
/// calling every bad reply malformed JSON.
/// </summary>
public class AnalyticsFailureSurfaceTests
{
    [Fact]
    public async Task AStatusReadThatThrowsAnswersAJsonErrorEnvelope()
    {
        await using Host host = await Host.StartAsync(builder => builder.Analytics
            .Setup(a => a.Execute("status", It.IsAny<JsonElement?>()))
            .Throws(new InvalidOperationException("The JSON value could not be converted to System.String.")));

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/analytics/status", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal("internal_error", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(
            "The JSON value could not be converted to System.String.",
            root.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APostedAnalyticsOperationThatThrowsAnswersAJsonErrorEnvelope()
    {
        await using Host host = await Host.StartAsync(builder => builder.Analytics
            .Setup(a => a.Execute("query", It.IsAny<JsonElement?>()))
            .Throws(new InvalidOperationException("projection is unavailable")));

        using StringContent payload = new("{\"metrics\":[]}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await host.Client.PostAsync(
            new Uri("/api/kyber/analytics/query", UriKind.Relative),
            payload);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("internal_error", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("'query'", body.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A500WithAnEmptyBodyIsReportedAsAStatusAndAnEmptyBody()
    {
        using Reply reply = new(HttpStatusCode.InternalServerError, string.Empty);

        string message = await ForwardedStatusMessageAsync(reply);

        Assert.StartsWith("engine_unavailable:", message, StringComparison.Ordinal);
        Assert.Contains("HTTP 500", message, StringComparison.Ordinal);
        Assert.Contains("empty body", message, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed JSON", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A500WithANonJsonBodyIsReportedAsANonJsonBody()
    {
        using Reply reply = new(HttpStatusCode.InternalServerError, "<html><body>Internal Server Error</body></html>");

        string message = await ForwardedStatusMessageAsync(reply);

        Assert.StartsWith("engine_unavailable:", message, StringComparison.Ordinal);
        Assert.Contains("HTTP 500", message, StringComparison.Ordinal);
        Assert.Contains("non-JSON body", message, StringComparison.Ordinal);
        Assert.Contains("Internal Server Error", message, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed JSON", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A500ThatCarriesAnErrorEnvelopeIsHandedBackAsAToolError()
    {
        string envelope = JsonSerializer.Serialize(new
        {
            contractVersion = 1,
            scope = new { },
            data = (object?)null,
            measurement = new { },
            coverage = new { },
            freshness = new { },
            evidence = Array.Empty<object>(),
            pagination = new { },
            error = new { code = "internal_error", message = "the analytics operation 'status' failed (InvalidOperationException: boom)." },
        });
        using Reply reply = new(HttpStatusCode.InternalServerError, envelope);

        JsonNode? response = await ForwardedCallAsync(reply);

        Assert.NotNull(response);
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("internal_error", response["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
        Assert.Contains(
            "InvalidOperationException: boom",
            response["result"]!["structuredContent"]!["error"]!["message"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A200WithAnEmptyBodyIsReportedAsAnEmptyBodyNotAsMalformedJson()
    {
        using Reply reply = new(HttpStatusCode.OK, string.Empty);

        string message = await ForwardedStatusMessageAsync(reply);

        Assert.StartsWith("incompatible_engine:", message, StringComparison.Ordinal);
        Assert.Contains("HTTP 200", message, StringComparison.Ordinal);
        Assert.Contains("empty body", message, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed JSON", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A200WithMalformedJsonStillNamesMalformedJsonAndTheStatus()
    {
        using Reply reply = new(HttpStatusCode.OK, "{\"contractVersion\":1,");

        string message = await ForwardedStatusMessageAsync(reply);

        Assert.StartsWith("incompatible_engine:", message, StringComparison.Ordinal);
        Assert.Contains("HTTP 200", message, StringComparison.Ordinal);
        Assert.Contains("malformed JSON", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidReplyIsForwardedAsTheToolResult()
    {
        using Reply reply = new(HttpStatusCode.OK, PresentationTestData.ContractEnvelope(new { value = new { ready = true } }).GetRawText());

        JsonNode? response = await ForwardedCallAsync(reply);

        Assert.NotNull(response);
        Assert.Null(response!["result"]!["isError"]);
        Assert.True(response["result"]!["structuredContent"]!["data"]!["value"]!["ready"]!.GetValue<bool>());
        Assert.Contains("/api/kyber/analytics/status", reply.RequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCatalogOperationAndMethodIsMappedByTheDashEndpoints()
    {
        await using Host host = await Host.StartAsync();

        List<RouteEndpoint> routes = [.. ((IEndpointRouteBuilder)host.App).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()];

        foreach (DashToolCatalog.DashTool tool in DashToolCatalog.Tools)
        {
            string path = "/api/kyber/analytics/" + tool.Operation;
            RouteEndpoint endpoint = Assert.Single(
                routes,
                route => string.Equals(route.RoutePattern.RawText, path, StringComparison.Ordinal));

            IHttpMethodMetadata? methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
            Assert.NotNull(methods);
            Assert.Contains(tool.Method, methods.HttpMethods);
        }

        string[] mappedOperations = [.. routes
            .Select(static route => route.RoutePattern.RawText ?? string.Empty)
            .Where(static path => path.StartsWith("/api/kyber/analytics/", StringComparison.Ordinal))
            .Select(static path => path["/api/kyber/analytics/".Length..])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        string[] catalogOperations = [.. DashToolCatalog.Tools
            .Select(static tool => tool.Operation)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(catalogOperations, mappedOperations);
        Assert.Equal(AnalyticsService.Operations.Order(StringComparer.Ordinal), catalogOperations);
    }

    private static async Task<string> ForwardedStatusMessageAsync(Reply reply)
    {
        JsonNode? response = await ForwardedCallAsync(reply);

        Assert.NotNull(response);
        Assert.NotNull(response!["error"]);
        return response["error"]!["message"]!.GetValue<string>();
    }

    private static Task<JsonNode?> ForwardedCallAsync(Reply reply)
    {
        Mock<IAnalyticsService> analytics = new();
        Mock<IEngineLifecycleService> lifecycle = new();
        lifecycle
            .Setup(l => l.ResolveLiveServerUriAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Uri?)null);

        using HttpClient httpClient = new(reply.Handler);
        McpStdioHost host = new(analytics.Object, lifecycle.Object, httpClient, "http://127.0.0.1:4747");
        return host.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dash_list_available_analytics_operations","arguments":{}}}""",
            TestContext.Current.CancellationToken);
    }

    /// <summary>One scripted engine reply, with the request URI the host asked for.</summary>
    private sealed class Reply : IDisposable
    {
        public Reply(HttpStatusCode status, string body)
        {
            Handler = new StubHandler(status, body, this);
        }

        public HttpMessageHandler Handler { get; }

        public string RequestUri { get; private set; } = string.Empty;

        public void Record(string requestUri) => RequestUri = requestUri;

        public void Dispose() => Handler.Dispose();

        private sealed class StubHandler(HttpStatusCode status, string body, Reply owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Record(request.RequestUri?.ToString() ?? string.Empty);
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }
    }

    /// <summary>Starts the real endpoint map with the analytics service substituted.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private Host(WebApplication app, HttpClient client, PresentationBuilder builder)
        {
            App = app;
            Client = client;
            Builder = builder;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public PresentationBuilder Builder { get; }

        public static async Task<Host> StartAsync(Action<PresentationBuilder>? configure = null)
        {
            PresentationBuilder builder = new();
            builder.ApplyCannedResults();
            configure?.Invoke(builder);

            WebApplication app = await PresentationTestHost.StartAsync(builder.AllSubstitutes(), builder.SettingsRuntime);
            return new Host(app, PresentationTestHost.CreateClient(app), builder);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
