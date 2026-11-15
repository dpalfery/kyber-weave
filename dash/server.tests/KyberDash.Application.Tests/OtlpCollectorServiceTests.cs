using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// OTLP admission policy: payload ceilings, decode failures, pause/resume, backlog backpressure, and
/// the store-changed signal, with every repository mocked.
/// </summary>
public class OtlpCollectorServiceTests
{
    private const string CopilotTracePayload = """
        {
          "resourceSpans": [{
            "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "copilot-host" } } ] },
            "scopeSpans": [{
              "scope": { "name": "test", "version": "1" },
              "spans": [{
                "traceId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "spanId": "1111111111111111",
                "name": "chat",
                "kind": "SPAN_KIND_CLIENT",
                "startTimeUnixNano": "1760000000000000000",
                "endTimeUnixNano": "1760000000100000000",
                "status": { "code": "STATUS_CODE_OK" },
                "attributes": [
                  { "key": "copilot_chat.chat_session_id", "value": { "stringValue": "sess-otlp" } },
                  { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "100" } },
                  { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "20" } }
                ]
              }]
            }]
          }]
        }
        """;

    [Fact]
    public async Task OversizePayloadIsRejectedWith413WithoutDecoding()
    {
        Harness harness = new(maxBodyBytes: 16);
        byte[] payload = Encoding.UTF8.GetBytes(CopilotTracePayload);

        IngestAdmissionResult result = await harness.Collector.AdmitTracesAsync(
            payload,
            "application/json",
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Equal(413, result.HttpStatusCode);
        Assert.Contains("exceeds", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        harness.Ingestion.Verify(
            service => service.IngestSpans(It.IsAny<IReadOnlyList<OtlpSpan>>()),
            Times.Never);
    }

    [Fact]
    public async Task MalformedJsonSurfacesAsADecodeFailure()
    {
        Harness harness = new();

        await Assert.ThrowsAsync<OtlpDecodeException>(() => harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes("{\"resourceSpans\": 5}"),
            "application/json",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedContentTypeIsRefusedWithoutDecoding()
    {
        Harness harness = new();

        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collector.AdmitLogsAsync(
            Encoding.UTF8.GetBytes("{}"),
            "text/plain",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptedPayloadIsQueuedAndFlushedThroughIngestionService()
    {
        Harness harness = new();
        List<OtlpSpan> ingested = [];
        harness.Ingestion
            .Setup(service => service.IngestSpans(It.IsAny<IReadOnlyList<OtlpSpan>>()))
            .Callback<IReadOnlyList<OtlpSpan>>(spans => ingested.AddRange(spans))
            .Returns(new IngestOutcome(1, 0, 0));

        IngestAdmissionResult result = await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json; charset=utf-8",
            TestContext.Current.CancellationToken);

        // Admission queues; the flush is what writes. Before a flush is owed, nothing has landed.
        Assert.True(result.Accepted);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(1, harness.Collector.PendingCount);
        Assert.True(harness.Collector.PendingBytes > 0);

        int flushed = await harness.Collector.FlushPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, flushed);
        Assert.Equal(0, harness.Collector.PendingCount);
        Assert.Equal(0, harness.Collector.PendingBytes);
        OtlpSpan span = Assert.Single(ingested);
        Assert.Equal("1111111111111111", span.SpanId);
        Assert.NotNull(harness.Collector.LastAcceptedAt);
    }

    /// <summary>
    /// The regression that made live capture invisible: nothing on the request path flushes, so
    /// accepted telemetry must reach the store from the running collector alone. Asserting the
    /// store write rather than the queue is deliberate: a queued batch satisfies every status-code
    /// assertion while a harness's spans never appear in any reader.
    /// </summary>
    [Fact]
    public async Task StartedCollectorFlushesAdmittedSpansWithoutAnExplicitFlush()
    {
        Harness harness = new();
        List<OtlpSpan> ingested = [];
        harness.Ingestion
            .Setup(service => service.IngestSpans(It.IsAny<IReadOnlyList<OtlpSpan>>()))
            .Callback<IReadOnlyList<OtlpSpan>>(spans => ingested.AddRange(spans))
            .Returns(new IngestOutcome(1, 0, 0));

        int storeUpdated = 0;
        harness.Collector.StoreUpdated += (_, _) => Interlocked.Increment(ref storeUpdated);
        harness.Collector.Start();

        try
        {
            IngestAdmissionResult result = await harness.Collector.AdmitTracesAsync(
                Encoding.UTF8.GetBytes(CopilotTracePayload),
                "application/json",
                TestContext.Current.CancellationToken);
            Assert.True(result.Accepted);

            // The floor is the loop's own interval plus generous scheduling room; the wake on
            // admission normally lands it in milliseconds.
            DateTime deadline = DateTime.UtcNow + OtlpCollectorService.FlushInterval + TimeSpan.FromSeconds(10);
            while (ingested.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.Single(ingested);
            Assert.Equal(0, harness.Collector.PendingCount);
            Assert.True(Volatile.Read(ref storeUpdated) >= 1);
            Assert.NotNull(harness.Collector.LastAcceptedAt);
        }
        finally
        {
            await harness.Collector.StopAsync();
        }
    }

    [Fact]
    public async Task FlushRaisesStoreUpdatedExactlyOnceForABatchThatLanded()
    {
        Harness harness = new();
        harness.Ingestion
            .Setup(service => service.IngestSpans(It.IsAny<IReadOnlyList<OtlpSpan>>()))
            .Returns(new IngestOutcome(1, 0, 0));

        int raised = 0;
        harness.Collector.StoreUpdated += (_, _) => raised++;

        await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json",
            TestContext.Current.CancellationToken);
        _ = await harness.Collector.FlushPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task FlushThatQuarantinesStillSignalsBecauseStoreStateChanged()
    {
        Harness harness = new();
        harness.Ingestion
            .Setup(service => service.IngestSpans(It.IsAny<IReadOnlyList<OtlpSpan>>()))
            .Returns(new IngestOutcome(0, 1, 0));

        int raised = 0;
        harness.Collector.StoreUpdated += (_, _) => raised++;

        await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json",
            TestContext.Current.CancellationToken);
        _ = await harness.Collector.FlushPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, raised);
        Assert.Equal(0, harness.Collector.PendingCount);
    }

    [Fact]
    public async Task EmptyFlushRaisesNoStoreUpdatedAndKerpPendingAtZero()
    {
        Harness harness = new();

        int raised = 0;
        harness.Collector.StoreUpdated += (_, _) => raised++;

        int flushed = await harness.Collector.FlushPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, flushed);
        Assert.Equal(0, raised);
        Assert.Equal(0, harness.Collector.PendingCount);
    }

    [Fact]
    public async Task PauseFlushesAndRefusesAdmissionUntilResumed()
    {
        Harness harness = new();
        harness.Ingestion
            .Setup(service => service.IngestSpans(It.IsAny<IReadOnlyList<OtlpSpan>>()))
            .Returns(new IngestOutcome(1, 0, 0));

        await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json",
            TestContext.Current.CancellationToken);

        harness.Collector.PauseIngestion();

        Assert.True(harness.Collector.IsPaused);
        Assert.Equal(0, harness.Collector.PendingCount);

        IngestAdmissionResult paused = await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json",
            TestContext.Current.CancellationToken);
        Assert.False(paused.Accepted);
        Assert.Equal(503, paused.HttpStatusCode);
        Assert.Equal(OtlpCollectorService.PauseRetryAfterSeconds, paused.RetryAfterSeconds);

        harness.Collector.ResumeIngestion();
        Assert.False(harness.Collector.IsPaused);

        IngestAdmissionResult resumed = await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json",
            TestContext.Current.CancellationToken);
        Assert.True(resumed.Accepted);
    }

    [Fact]
    public async Task ExhaustedBacklogReturns503WithRetryAfter()
    {
        Harness harness = new(maxPendingBytes: 64);
        byte[] payload = Encoding.UTF8.GetBytes(CopilotTracePayload);
        Assert.True(payload.Length > 64);

        IngestAdmissionResult result = await harness.Collector.AdmitTracesAsync(
            payload,
            "application/json",
            TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Equal(503, result.HttpStatusCode);
        Assert.Equal(BoundedIngestWriter.RetryAfterSeconds, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task AdmitAndFlushPersistsLogsAndMetricsWithoutSpans()
    {
        Harness harness = new();
        Mock<ICanonStoreRepository> store = harness.Store;

        IngestAdmissionResult logs = harness.Collector.AdmitAndFlush(
            Encoding.UTF8.GetBytes("""
                {"resourceLogs":[{"scopeLogs":[{"logRecords":[{"timeUnixNano":"1760000000000000000","body":{"stringValue":"hi"}}]}]}]}
                """),
            "application/json");
        Assert.True(logs.Accepted);
        store.Verify(repository => repository.StorePendingLogs(It.IsAny<IEnumerable<OtlpLog>>(), It.IsAny<string?>()), Times.Once);

        IngestAdmissionResult metrics = harness.Collector.AdmitAndFlush(
            Encoding.UTF8.GetBytes("""
                {"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"gen_ai.client.token.usage","gauge":{"dataPoints":[{"asDouble":1.0,"timeUnixNano":"1760000000000000000"}]}}]}]}]}
                """),
            "application/json");
        Assert.True(metrics.Accepted);
        store.Verify(repository => repository.StoreMetrics(It.IsAny<IEnumerable<OtlpMetric>>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task OtlpConstructorRejectsNullDependencies()
    {
        Mock<ICanonStoreRepository> store = new();
        Mock<ITranscriptIngestionService> ingestion = new();

        Assert.Throws<ArgumentNullException>(() => new OtlpCollectorService(null!, ingestion.Object, new DashOptions()));
        Assert.Throws<ArgumentNullException>(() => new OtlpCollectorService(store.Object, null!, new DashOptions()));
        Assert.Throws<ArgumentNullException>(() => new OtlpCollectorService(store.Object, ingestion.Object, null!));
        await Task.CompletedTask;
    }

    /// <summary>Collector over mocked collaborators with configurable ceilings.</summary>
    private class Harness
    {
        public Harness(long? maxBodyBytes = null, long? maxPendingBytes = null)
        {
            DashOptions options = new();
            if (maxBodyBytes.HasValue)
            {
                options.MaxOtlpBodyBytes = maxBodyBytes.Value;
            }

            if (maxPendingBytes.HasValue)
            {
                options.MaxPendingBytes = maxPendingBytes.Value;
            }

            Collector = new OtlpCollectorService(Store.Object, Ingestion.Object, options);
        }

        public Mock<ICanonStoreRepository> Store { get; } = new();

        public Mock<ITranscriptIngestionService> Ingestion { get; } = new();

        public OtlpCollectorService Collector { get; }
    }
}
