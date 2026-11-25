using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests;

/// <summary>
/// OTLP admission policy: payload ceilings, decode failures, pause/resume, backlog backpressure,
/// log mapping, metric persistence, DI wiring, and the store-changed signal against
/// <see cref="InMemoryRelationalStore"/>.
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

    private const string UnknownTracePayload = """
        {
          "resourceSpans": [{
            "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "mystery-host" } } ] },
            "scopeSpans": [{
              "scope": { "name": "test", "version": "1" },
              "spans": [{
                "traceId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "spanId": "2222222222222222",
                "name": "mystery.op",
                "kind": "SPAN_KIND_INTERNAL",
                "startTimeUnixNano": "1760000000000000000",
                "endTimeUnixNano": "1760000000100000000",
                "status": { "code": "STATUS_CODE_OK" },
                "attributes": [
                  { "key": "mystery.key", "value": { "stringValue": "mystery-value" } }
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
        Assert.Equal(0, harness.Store.CountRecords());
    }

    [Fact]
    public async Task MalformedJsonSurfacesAsADecodeFailure()
    {
        Harness harness = new();

        await Assert.ThrowsAsync<OtlpDecodeException>(() => harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes("{\"resourceSpans\": 5}"),
            "application/json",
            TestContext.Current.CancellationToken));

        Assert.Equal(1, harness.Collector.CountFailuresSince(DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.Equal("OTLP_MALFORMED_PAYLOAD", harness.Collector.LastErrorCode);
    }

    [Fact]
    public void WiderFailureWindowIsNotClippedToFiveMinutes()
    {
        DateTimeOffset now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddMinutes(-10), OtlpCollectorService.FailureRetentionCutoff(now.AddMinutes(-10), now));
        Assert.Equal(now.AddMinutes(-5), OtlpCollectorService.FailureRetentionCutoff(now.AddMinutes(-1), now));
    }

    [Fact]
    public async Task FailureWindowStopsGrowingPastItsCap()
    {
        Harness harness = new();
        byte[] payload = Encoding.UTF8.GetBytes("{\"resourceSpans\": 5}");

        for (int i = 0; i < 1100; i++)
        {
            await Assert.ThrowsAsync<OtlpDecodeException>(() => harness.Collector.AdmitTracesAsync(
                payload,
                "application/json",
                TestContext.Current.CancellationToken));
        }

        int counted = harness.Collector.CountFailuresSince(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(1024, counted);
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

        IngestAdmissionResult result = await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json; charset=utf-8",
            TestContext.Current.CancellationToken);

        // Admission queues; the flush is what writes. Before a flush is owed, nothing has landed.
        Assert.True(result.Accepted);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(1, harness.Collector.PendingCount);
        Assert.True(harness.Collector.PendingBytes > 0);
        Assert.Equal(0, harness.Store.CountRecords());

        int flushed = await harness.Collector.FlushPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, flushed);
        Assert.Equal(0, harness.Collector.PendingCount);
        Assert.Equal(0, harness.Collector.PendingBytes);
        CollectedRecord? record = harness.Store.FindRecord("1111111111111111");
        Assert.NotNull(record);
        Assert.Equal("copilot", record.HarnessId);
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
            // Wait for everything asserted below: the store write lands before the flush raises
            // StoreUpdated and drains the pending count, so waiting on the record alone races them.
            while ((harness.Store.CountRecords() == 0
                    || harness.Collector.PendingCount != 0
                    || Volatile.Read(ref storeUpdated) == 0)
                && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);

            Assert.NotNull(harness.Store.FindRecord("1111111111111111"));
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

        int raised = 0;
        harness.Collector.StoreUpdated += (_, _) => raised++;

        await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(UnknownTracePayload),
            "application/json",
            TestContext.Current.CancellationToken);
        _ = await harness.Collector.FlushPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, raised);
        Assert.Equal(0, harness.Collector.PendingCount);
        Assert.Equal(1, harness.Store.CountQuarantinedSpans());
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

        await harness.Collector.AdmitTracesAsync(
            Encoding.UTF8.GetBytes(CopilotTracePayload),
            "application/json",
            TestContext.Current.CancellationToken);

        harness.Collector.PauseIngestion();

        Assert.True(harness.Collector.IsPaused);
        Assert.Equal(0, harness.Collector.PendingCount);
        Assert.Equal(1, harness.Store.CountRecords());

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
    public void AdmitAndFlushPersistsMappedLogsProblemsForUnmappableLogsAndMetricsWithoutRemovedLogTables()
    {
        Harness harness = new();
        InMemoryRelationalStore store = harness.Store;

        IngestAdmissionResult mappedLog = harness.Collector.AdmitAndFlush(
            Encoding.UTF8.GetBytes("""
                {
                  "resourceLogs": [{
                    "resource": { "attributes": [{ "key": "service.name", "value": { "stringValue": "claude-code" } }] },
                    "scopeLogs": [{
                      "scope": { "name": "claude_code", "version": "1.0" },
                      "logRecords": [{
                        "timeUnixNano": "1760000000000000000",
                        "traceId": "0102030405060708090a0b0c0d0e0f10",
                        "spanId": "aabbccddeeff0102",
                        "body": { "stringValue": "mapped prompt text" },
                        "attributes": [
                          { "key": "claude_code.event", "value": { "stringValue": "user_prompt" } },
                          { "key": "event.name", "value": { "stringValue": "claude_code.user_prompt" } },
                          { "key": "session.id", "value": { "stringValue": "sess-log-1" } }
                        ]
                      }]
                    }]
                  }]
                }
                """),
            "application/json");
        Assert.True(mappedLog.Accepted);
        Assert.Equal(1, store.CountRecords("claude-code"));
        Assert.NotNull(store.GetSession("sess-log-1"));
        Assert.Equal(0, store.CountIngestProblems());

        IngestAdmissionResult unmappableLog = harness.Collector.AdmitAndFlush(
            Encoding.UTF8.GetBytes("""
                {"resourceLogs":[{"scopeLogs":[{"logRecords":[{"timeUnixNano":"1760000000000000000","body":{"stringValue":"secret-unmapped-hi"}}]}]}]}
                """),
            "application/json");
        Assert.True(unmappableLog.Accepted);
        Assert.Equal(1, store.CountRecords("claude-code"));
        Assert.Equal(1, store.CountIngestProblems());
        StoredIngestProblem problem = Assert.Single(store.ListIngestProblems());
        Assert.DoesNotContain("secret-unmapped-hi", problem.Problem.Message, StringComparison.Ordinal);

        IngestAdmissionResult metrics = harness.Collector.AdmitAndFlush(
            Encoding.UTF8.GetBytes("""
                {"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"gen_ai.client.token.usage","gauge":{"dataPoints":[{"asDouble":1.0,"timeUnixNano":"1760000000000000000"}]}}]}]}]}
                """),
            "application/json");
        Assert.True(metrics.Accepted);
        StoredMetricPoint point = Assert.Single(store.ListMetricPoints("gen_ai.client.token.usage"));
        Assert.Equal("gauge", point.Kind);
        Assert.Equal(1.0, point.Value);
    }

    [Fact]
    public async Task ServiceCollectionExtensions_WiresOtlpCollectorServiceThroughRelationalWriterAndOperationalStore()
    {
        InMemoryRelationalStore store = new();

        ServiceCollection services = new();
        services.AddSingleton<IRelationalStoreWriter>(store);
        services.AddSingleton<IOperationalStore>(store);
        _ = services.AddDashApplication(new DashOptions());

        await using ServiceProvider provider = services.BuildServiceProvider();
        IOtlpCollectorService collector = provider.GetRequiredService<IOtlpCollectorService>();

        IngestAdmissionResult logResult = await collector.AdmitLogsAsync(
            Encoding.UTF8.GetBytes("""
                {"resourceLogs":[{"scopeLogs":[{"logRecords":[{"timeUnixNano":"1760000000000000000","body":{"stringValue":"unmapped"}}]}]}]}
                """),
            "application/json",
            TestContext.Current.CancellationToken);
        Assert.True(logResult.Accepted);

        int flushed = await collector.FlushPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, flushed);
        Assert.Equal(1, store.CountIngestProblems());
    }

    [Fact]
    public async Task OtlpConstructorRejectsNullDependencies()
    {
        InMemoryRelationalStore store = new();
        Mock<ITranscriptIngestionService> ingestion = new();

        Assert.Throws<ArgumentNullException>(() => new OtlpCollectorService(null!, store, ingestion.Object, new DashOptions()));
        Assert.Throws<ArgumentNullException>(() => new OtlpCollectorService(store, null!, ingestion.Object, new DashOptions()));
        Assert.Throws<ArgumentNullException>(() => new OtlpCollectorService(store, store, null!, new DashOptions()));
        Assert.Throws<ArgumentNullException>(() => new OtlpCollectorService(store, store, ingestion.Object, null!));
        await Task.CompletedTask;
    }

    /// <summary>Collector over <see cref="InMemoryRelationalStore"/> with configurable ceilings.</summary>
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

            Store = new InMemoryRelationalStore();
            Ingestion = new TranscriptIngestionService(Store, Store, new HarnessSourceRegistry());
            Collector = new OtlpCollectorService(Store, Store, Ingestion, options);
        }

        public InMemoryRelationalStore Store { get; }

        public TranscriptIngestionService Ingestion { get; }

        public OtlpCollectorService Collector { get; }
    }
}
