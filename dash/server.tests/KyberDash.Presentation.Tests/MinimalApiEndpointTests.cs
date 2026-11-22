using System.Net;
using System.Text;
using System.Text.Json;
using KyberDash.Api;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;
using Microsoft.AspNetCore.Builder;
using Moq;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Drives the real Minimal API endpoint map through the test server, substituting every
/// application service with a mock. These assert the wire contract: status codes, headers, and the
/// bodies other clients already parse.
/// </summary>
public class MinimalApiEndpointTests
{
    [Fact]
    public async Task HealthzReportsOk()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/healthz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(body.RootElement.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task VersionReturnsTheQueryServicePayload()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/version", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("0.21.0", body.RunProperty("version").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task MetaReturnsTheQueryServicePayload()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/meta", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task SessionsWrapsThePayloadInASessionsEnvelope()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/sessions?limit=10&harness=claude-code", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("sessions").ValueKind);
        host.Builder.Query.Verify(q => q.GetSessions(10, "claude-code"), Times.Once);
    }

    [Fact]
    public async Task CompareRunsRejectsCallerSuppliedCompletedPairCount()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri("/api/kyber/compare/runs?runA=a&runB=b&completedPairCount=1", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("completedPairCount", body.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        host.Builder.Query.Verify(q => q.GetCompareRuns(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CompareRunsRequiresBothRunIds()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/compare/runs?runA=a", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingSessionReturnsNotFound()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/session/absent", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SettingsGetReturnsCurrentSettings()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/settings", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(4747, body.RootElement.GetProperty("dashboardPort").GetInt32());
    }

    [Fact]
    public async Task SettingsPutAppliesThePatchOnALoopbackRequestWithoutOrigin()
    {
        await using Host host = await Host.StartAsync();

        using StringContent patch = new("{\"refreshCadenceMinutes\":15}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await host.Client.PutAsync(new Uri("/api/kyber/settings", UriKind.Relative), patch);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        host.Builder.Settings.Verify(s => s.UpdateSettings(It.IsAny<JsonElement>()), Times.Once);
    }

    [Fact]
    public async Task SettingsPutRejectsABrowserOrigin()
    {
        await using Host host = await Host.StartAsync();

        using HttpRequestMessage request = new(HttpMethod.Put, "/api/kyber/settings")
        {
            Content = new StringContent("{\"refreshCadenceMinutes\":15}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "http://127.0.0.1:4747");

        HttpResponseMessage response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        host.Builder.Settings.Verify(s => s.UpdateSettings(It.IsAny<JsonElement>()), Times.Never);
    }

    [Fact]
    public async Task SettingsPutMapsValidationFailureToBadRequest()
    {
        await using Host host = await Host.StartAsync(b =>
            b.Settings
                .Setup(s => s.UpdateSettings(It.IsAny<JsonElement>()))
                .Throws(new DashValidationException("refreshCadenceMinutes must be between 1 and 1440.")));

        using StringContent patch = new("{\"refreshCadenceMinutes\":0}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await host.Client.PutAsync(new Uri("/api/kyber/settings", UriKind.Relative), patch);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TracesAcceptsAValidPayload()
    {
        await using Host host = await Host.StartAsync(b =>
            b.Otlp
                .Setup(o => o.AdmitTracesAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IngestAdmissionResult(true, 200)));

        using StringContent payload = new("{\"resourceSpans\":[]}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await host.Client.PostAsync(new Uri("/v1/traces", UriKind.Relative), payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        host.Builder.Otlp.Verify(
            o => o.AdmitTracesAsync(It.IsAny<ReadOnlyMemory<byte>>(), "application/json", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TracesReturnsPayloadTooLargeForAnOversizeBody()
    {
        await using Host host = await Host.StartAsync(b =>
        {
            b.Otlp
                .Setup(o => o.AdmitTracesAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Throws(new OtlpPayloadTooLargeException("payload exceeds the limit"));
            b.SettingsRuntime.MaxOtlpBodyBytes = 8;
        });

        using StringContent payload = new("{\"resourceSpans\":[]}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await host.Client.PostAsync(new Uri("/v1/traces", UriKind.Relative), payload);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OTLP_PAYLOAD_TOO_LARGE", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task TracesReturnsServiceUnavailableWithRetryAfterUnderBackpressure()
    {
        await using Host host = await Host.StartAsync(b =>
            b.Otlp
                .Setup(o => o.AdmitTracesAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IngestAdmissionResult(false, 503, RetryAfterSeconds: 2, ErrorMessage: "buffer full")));

        using StringContent payload = new("{\"resourceSpans\":[]}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await host.Client.PostAsync(new Uri("/v1/traces", UriKind.Relative), payload);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task TracesRejectsAnUnsupportedMediaType()
    {
        await using Host host = await Host.StartAsync();

        using StringContent payload = new("{}", Encoding.UTF8, "text/plain");
        HttpResponseMessage response = await host.Client.PostAsync(new Uri("/v1/traces", UriKind.Relative), payload);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        host.Builder.Otlp.Verify(
            o => o.AdmitTracesAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnalyticsPostReturnsTheServiceEnvelopeWithNoStore()
    {
        JsonElement envelope = JsonSerializer.SerializeToElement(new
        {
            contractVersion = 1,
            data = new { rows = Array.Empty<object>() },
        });

        await using Host host = await Host.StartAsync(b =>
            b.Analytics
                .Setup(a => a.Execute("query", It.IsAny<JsonElement?>()))
                .Returns(new AnalyticsExecutionResult(200, envelope)));

        using StringContent payload = new("{\"metrics\":[]}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await host.Client.PostAsync(new Uri("/api/kyber/analytics/query", UriKind.Relative), payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("contractVersion").GetInt32());
    }

    [Fact]
    public async Task RecordPredictionReturnsCreatedForALoopbackRequest()
    {
        await using Host host = await Host.StartAsync();

        using StringContent payload = new(
            "{\"findingId\":\"finding-1\",\"runId\":\"run-1\",\"predictedWasteTokens\":1200,\"confidence\":0.8}",
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage response = await host.Client.PostAsync(new Uri("/api/kyber/predictions", UriKind.Relative), payload);

        // The endpoint accepted the write on a loopback request; the recorded row itself is the
        // application layer's business, which its own tests cover.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        host.Builder.Query.Verify(q => q.TryRecordPrediction(It.IsAny<JsonElement>(), out It.Ref<PredictionRow?>.IsAny, out It.Ref<string?>.IsAny), Times.Once);
    }

    [Fact]
    public async Task EventsStreamsAConnectedCommentAndSubscribesTheClient()
    {
        await using Host host = await Host.StartAsync();

        // A mock can report whether a handler subscribed, which is the observable proof that the
        // stream is wired to the collector without depending on Moq's event-raise helper.
        int subscribers = 0;
        host.Builder.Otlp
            .SetupAdd(o => o.StoreUpdated += It.IsAny<EventHandler>())
            .Callback<EventHandler>(_ => Interlocked.Increment(ref subscribers));

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/events");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using StreamReader reader = new(stream, Encoding.UTF8);
        string? first = await reader.ReadLineAsync(timeout.Token);

        Assert.Equal(": connected", first);
        Assert.Equal(1, Volatile.Read(ref subscribers));
    }

    /// <summary>
    /// The stream is a subscription, not a one-shot document: it must stay open after the preamble
    /// and end only when the client goes away. A handler that returns after the comment is
    /// indistinguishable from a healthy one to the test above, so the close is what this pins.
    /// </summary>
    [Fact]
    public async Task EventsStaysOpenAfterThePreambleUntilTheClientCancels()
    {
        await using Host host = await Host.StartAsync();

        // The handler's own subscription is the proof of lifetime: it is released the moment the
        // handler returns, so a handler that quits after the comment cannot hide behind a 200.
        int live = 0;
        host.Builder.Otlp
            .SetupAdd(o => o.StoreUpdated += It.IsAny<EventHandler>())
            .Callback<EventHandler>(_ => Interlocked.Increment(ref live));
        host.Builder.Otlp
            .SetupRemove(o => o.StoreUpdated -= It.IsAny<EventHandler>())
            .Callback<EventHandler>(_ => Interlocked.Decrement(ref live));

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/events");
        using CancellationTokenSource cancel = new();
        using HttpResponseMessage response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancel.Token);
        using StreamReader reader = new(stream, Encoding.UTF8);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(30));
        Assert.Equal(": connected", await reader.ReadLineAsync(guard.Token));

        // Reading to the end is what a handler that returned after the preamble allows at once; a
        // held-open stream leaves this read waiting for the next event.
        using CancellationTokenSource grace = new(TimeSpan.FromSeconds(5));
        Task<string> rest = reader.ReadToEndAsync(grace.Token);
        await Task.WhenAny(rest, Task.Delay(TimeSpan.FromMilliseconds(750), grace.Token));

        Assert.False(
            rest.IsCompleted,
            "the SSE handler returned after its preamble, so the client saw the stream end, not a subscription");
        Assert.Equal(1, Volatile.Read(ref live));

        await cancel.CancelAsync();
    }

    [Fact]
    public async Task RootServesTheSpaDocumentFromTheAssetService()
    {
        await using Host host = await Host.StartAsync(b =>
            b.Assets
                .Setup(a => a.TryResolveAsset(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(new WebAssetDocument("text/html; charset=utf-8", Encoding.UTF8.GetBytes("<!doctype html><div id=\"root\"></div>"))));

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id=\"root\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootFallsBackToAnExplainablePageWhenNoBundleIsPresent()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"root\"", html, StringComparison.Ordinal);
        Assert.Contains("not built", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The regression that stopped the dashboard loading at all: the SPA's own bundle path carries
    /// a file extension, and the parameterless <c>MapFallback</c> registers <c>{*path:nonfile}</c>,
    /// so <c>/assets/index-*.js</c> matched no endpoint, 404'd, and the app never booted.
    /// </summary>
    [Theory]
    [InlineData("/assets/index-abc123.js")]
    [InlineData("/assets/index-abc123.css")]
    [InlineData("/favicon.svg")]
    [InlineData("/kyberdash-logo.png")]
    public async Task FilePathsReachTheAssetService(string path)
    {
        await using Host host = await Host.StartAsync(b =>
            b.Assets
                .Setup(a => a.TryResolveAsset(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns<string, string?>((requested, _) => new WebAssetDocument(
                    "application/octet-stream",
                    Encoding.UTF8.GetBytes("asset-body"),
                    ResolvedPath: requested.TrimStart('/'))));

        HttpResponseMessage response = await host.Client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("asset-body", await response.Content.ReadAsStringAsync());
        host.Builder.Assets.Verify(a => a.TryResolveAsset(It.Is<string>(p => p == path), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ClientSideRoutesStillReceiveTheSpaDocument()
    {
        await using Host host = await Host.StartAsync(b =>
            b.Assets
                .Setup(a => a.TryResolveAsset(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(new WebAssetDocument("text/html; charset=utf-8", Encoding.UTF8.GetBytes("<html>spa</html>"))));

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/harness/claude-code", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<html>spa</html>", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A file the bundle does not hold must be a 404. The asset service answers an unknown key with
    /// index.html, so without this check a missing <c>/assets/*.js</c> would be served as HTML.
    /// </summary>
    [Fact]
    public async Task MissingFileIsNotFoundRatherThanTheSpaDocument()
    {
        await using Host host = await Host.StartAsync(b =>
            b.Assets
                .Setup(a => a.TryResolveAsset(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(new WebAssetDocument("text/html; charset=utf-8", Encoding.UTF8.GetBytes("<html>spa</html>"))));

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/assets/gone.js", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("spa", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportPassesTrimmedFiltersDefaultDaysAndLimitAndAllowsLoopbackReadWithoutOrigin()
    {
        JsonElement reportPayload = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            generatedAt = "2026-11-20T12:00:00.000Z",
            kyberdashVersion = "0.21.0",
            scope = new { days = 7, harness = "claude-code", sessionId = "sess-1", runId = "run-1" },
            coverage = new
            {
                storePath = "canon.db",
                harnesses = Array.Empty<object>(),
                quarantineCount = 0,
                problemCount = 0,
                hints = Array.Empty<string>(),
            },
            findings = Array.Empty<object>(),
            harnesses = Array.Empty<object>(),
            latestSession = (object?)null,
            cost = Array.Empty<object>(),
        });
        await using Host host = await Host.StartAsync(b =>
            b.Query
                .Setup(q => q.GetReport("claude-code", "sess-1", "run-1", 50, 7))
                .Returns(reportPayload));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri("/api/kyber/report?harness=%20claude-code%20&session=%20sess-1%20&run=%20run-1%20", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RunProperty("schemaVersion").GetInt32());
        Assert.Equal("0.21.0", body.RunProperty("kyberdashVersion").GetString());
        Assert.Equal(7, body.RunProperty("scope").GetProperty("days").GetInt32());
        Assert.Equal("canon.db", body.RunProperty("coverage").GetProperty("storePath").GetString());
        Assert.Equal(JsonValueKind.Array, body.RunProperty("harnesses").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.RunProperty("findings").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.RunProperty("cost").ValueKind);
        host.Builder.Query.Verify(q => q.GetReport("claude-code", "sess-1", "run-1", 50, 7), Times.Once);
    }

    [Theory]
    [InlineData("/api/kyber/report", null, null, null, 50, 7)]
    [InlineData("/api/kyber/report?days=", null, null, null, 50, 7)]
    [InlineData("/api/kyber/report?days=%20%20&harness=%20%20&session=%20&run=%20%20", null, null, null, 50, 7)]
    [InlineData("/api/kyber/report?days=1&harness=all", null, null, null, 50, 1)]
    [InlineData("/api/kyber/report?days=1&harness=%20GEMINI%20", "GEMINI", null, null, 50, 1)]
    [InlineData("/api/kyber/report?days=30&limit=10&harness=codex", "codex", null, null, 10, 30)]
    [InlineData("/api/kyber/report?days=%2014%20&limit=500", null, null, null, 200, 14)]
    public async Task ReportNormalizesDaysLimitAndScopeParameters(
        string requestPath,
        string? expectedHarness,
        string? expectedSession,
        string? expectedRun,
        int expectedLimit,
        int expectedDays)
    {
        JsonElement reportPayload = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            generatedAt = "2026-11-20T12:00:00.000Z",
            kyberdashVersion = "0.21.0",
            scope = new { days = expectedDays, harness = expectedHarness },
            coverage = new { storePath = "canon.db", harnesses = Array.Empty<object>() },
            findings = Array.Empty<object>(),
            harnesses = Array.Empty<object>(),
            latestSession = (object?)null,
            cost = Array.Empty<object>(),
        });
        await using Host host = await Host.StartAsync(b =>
            b.Query
                .Setup(q => q.GetReport(expectedHarness, expectedSession, expectedRun, expectedLimit, expectedDays))
                .Returns(reportPayload));

        HttpResponseMessage response = await host.Client.GetAsync(new Uri(requestPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedDays, body.RunProperty("scope").GetProperty("days").GetInt32());
        host.Builder.Query.Verify(
            q => q.GetReport(expectedHarness, expectedSession, expectedRun, expectedLimit, expectedDays),
            Times.Once);
    }

    [Theory]
    [InlineData("/api/kyber/report?days=0")]
    [InlineData("/api/kyber/report?days=-1")]
    [InlineData("/api/kyber/report?days=abc")]
    [InlineData("/api/kyber/report?days=1.5")]
    public async Task ReportRejectsInvalidDaysWithBadRequest(string requestPath)
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri(requestPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("days must be a positive whole number", body.RunProperty("error").GetString());
        host.Builder.Query.Verify(
            q => q.GetReport(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    [Theory]
    [InlineData("/api/kyber/report?limit=0")]
    [InlineData("/api/kyber/report?limit=-3")]
    [InlineData("/api/kyber/report?limit=abc")]
    [InlineData("/api/kyber/report?limit=")]
    public async Task ReportRejectsInvalidLimitWithBadRequest(string requestPath)
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri(requestPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("limit must be a positive whole number", body.RunProperty("error").GetString());
        host.Builder.Query.Verify(
            q => q.GetReport(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task UnknownApiPathReturnsNotFound()
    {
        await using Host host = await Host.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(new Uri("/api/kyber/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Owns a started test application and the mocks behind it.</summary>
    private class Host : IAsyncDisposable
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

/// <summary>Small read helpers that keep the assertions above one line each.</summary>
internal static class JsonDocumentExtensions
{
    /// <summary>Reads a property, failing the test with the property name when it is absent.</summary>
    /// <param name="document">The parsed body.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The property value.</returns>
    public static JsonElement RunProperty(this JsonDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.RootElement.TryGetProperty(name, out JsonElement value))
            throw new Xunit.Sdk.XunitException($"Response body has no '{name}' property: {document.RootElement.GetRawText()}");

        return value;
    }
}
