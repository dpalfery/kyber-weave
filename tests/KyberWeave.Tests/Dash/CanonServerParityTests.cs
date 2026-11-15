using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Dash;
using Xunit;

namespace KyberWeave.Tests.Dash;

/// <summary>
/// Verifies Slice 4 OTLP Protobuf decoding, loopback OTLP/HTTP receiver, shared operator
/// settings, validated database clean, run/harness rollups, <see cref="KyberJobHost"/> lease
/// and <c>server.json</c> publication, and <see cref="KyberApiServer"/> REST + analytics + SSE routes.
/// </summary>
public sealed class CanonServerParityTests
{
    [Fact]
    public void OtlpCollector_DecodesTracesLogsAndMetricsProtobuf_AndStripsIdentity()
    {
        byte[] traceId = Convert.FromHexString("0102030405060708090a0b0c0d0e0f10");
        byte[] spanId = Convert.FromHexString("1122334455667788");
        byte[] parentSpanId = Convert.FromHexString("0011223344556677");

        byte[] resourceMsg = EncodeMessage(
            PbKeyValue(1, "service.name", PbAnyString("claude-host")),
            PbKeyValue(1, "enduser.id", PbAnyString("user-secret")),
            PbKeyValue(1, "user.email", PbAnyString("dave@example.com")));

        byte[] scopeMsg = EncodeMessage(
            PbString(1, "anthropic.sdk"),
            PbString(2, "1.2.3"));

        byte[] statusMsg = EncodeMessage(
            PbString(2, "ok-note"),
            PbVarint(3, 1));

        byte[] spanMsg = EncodeMessage(
            PbBytes(1, traceId),
            PbBytes(2, spanId),
            PbBytes(4, parentSpanId),
            PbString(5, "llm.request"),
            PbVarint(6, 3), // SPAN_KIND_CLIENT
            PbFixed64(7, 1760000000000000000UL),
            PbFixed64(8, 1760000000250000000UL),
            PbKeyValue(9, "session.id", PbAnyString("pb-sess-1")),
            PbKeyValue(9, "gen_ai.usage.input_tokens", PbAnyInt(1200)),
            PbKeyValue(9, "user.id", PbAnyString("strip-span-user")),
            PbBytes(15, statusMsg));

        byte[] scopeSpansMsg = EncodeMessage(PbBytes(1, scopeMsg), PbBytes(2, spanMsg));
        byte[] resourceSpansMsg = EncodeMessage(PbBytes(1, resourceMsg), PbBytes(2, scopeSpansMsg));
        byte[] traceRequest = EncodeMessage(PbBytes(1, resourceSpansMsg));

        IReadOnlyList<OtlpSpan> spans = OtlpCollector.DecodeTracesProtobuf(traceRequest);
        OtlpSpan span = Assert.Single(spans);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", span.TraceId);
        Assert.Equal("1122334455667788", span.SpanId);
        Assert.Equal("0011223344556677", span.ParentSpanId);
        Assert.Equal("llm.request", span.Name);
        Assert.Equal("client", span.Kind);
        Assert.Equal(250.0, span.DurationMs);
        Assert.Equal("ok", span.Status.Code);
        Assert.Equal("ok-note", span.Status.Message);
        Assert.Equal("anthropic.sdk", span.Scope.Name);
        Assert.Equal("1.2.3", span.Scope.Version);
        Assert.Equal("claude-host", span.Resource["service.name"].GetString());
        Assert.False(span.Resource.ContainsKey("enduser.id"));
        Assert.False(span.Resource.ContainsKey("user.email"));
        Assert.Equal("pb-sess-1", span.Attributes["session.id"].GetString());
        Assert.Equal(1200, span.Attributes["gen_ai.usage.input_tokens"].GetInt64());
        Assert.False(span.Attributes.ContainsKey("user.id"));

        // ExportLogsServiceRequest
        byte[] logRecordMsg = EncodeMessage(
            PbFixed64(1, 1760000001000000000UL),
            PbBytes(5, PbAnyString("assistant response payload")),
            PbKeyValue(6, "session.id", PbAnyString("pb-sess-1")),
            PbKeyValue(6, "organization.id", PbAnyString("strip-log-org")),
            PbBytes(9, traceId),
            PbBytes(10, spanId),
            PbString(12, "claude_code.api_request"));
        byte[] scopeLogsMsg = EncodeMessage(PbBytes(1, scopeMsg), PbBytes(2, logRecordMsg));
        byte[] resourceLogsMsg = EncodeMessage(PbBytes(1, resourceMsg), PbBytes(2, scopeLogsMsg));
        byte[] logsRequest = EncodeMessage(PbBytes(1, resourceLogsMsg));

        IReadOnlyList<OtlpLog> logs = OtlpCollector.DecodeLogsProtobuf(logsRequest);
        OtlpLog log = Assert.Single(logs);
        Assert.Equal("assistant response payload", log.Body);
        Assert.Equal("pb-sess-1", log.SessionId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", log.TraceId);
        Assert.Equal("1122334455667788", log.SpanId);
        Assert.False(log.Resource.ContainsKey("user.email"));
        Assert.False(log.Attributes.ContainsKey("organization.id"));

        // ExportMetricsServiceRequest
        byte[] numberPointMsg = EncodeMessage(
            PbFixed64(2, 1760000000000000000UL),
            PbFixed64(3, 1760000001000000000UL),
            PbFixed64(6, 420UL),
            PbKeyValue(7, "token_type", PbAnyString("input")),
            PbKeyValue(7, "user.id", PbAnyString("strip-metric-user")));
        byte[] sumMsg = EncodeMessage(
            PbBytes(1, numberPointMsg),
            PbVarint(2, 1), // DELTA
            PbVarint(3, 1)); // is_monotonic = true
        byte[] metricMsg = EncodeMessage(
            PbString(1, "gen_ai.client.token.usage"),
            PbString(2, "Token count"),
            PbString(3, "token"),
            PbBytes(7, sumMsg));
        byte[] scopeMetricsMsg = EncodeMessage(PbBytes(1, scopeMsg), PbBytes(2, metricMsg));
        byte[] resourceMetricsMsg = EncodeMessage(PbBytes(1, resourceMsg), PbBytes(2, scopeMetricsMsg));
        byte[] metricsRequest = EncodeMessage(PbBytes(1, resourceMetricsMsg));

        IReadOnlyList<OtlpMetric> metrics = OtlpCollector.DecodeMetricsProtobuf(metricsRequest);
        OtlpMetric metric = Assert.Single(metrics);
        Assert.Equal("gen_ai.client.token.usage", metric.Name);
        Assert.Equal("sum", metric.Kind);
        Assert.Equal(1, metric.AggregationTemporality);
        Assert.True(metric.IsMonotonic);
        OtlpMetricDataPoint pt = Assert.Single(metric.DataPoints);
        Assert.Equal(420.0, pt.Value);
        Assert.Equal("input", pt.Attributes["token_type"].GetString());
        Assert.False(pt.Attributes.ContainsKey("user.id"));

        // Enforces max body bytes cap
        Assert.Throws<OtlpPayloadTooLargeException>(
            () => OtlpCollector.DecodeTracesProtobuf(traceRequest, maxBodyBytes: traceRequest.Length - 1));
    }

    [Fact]
    public async Task OtlpHttpServer_IngestsJsonAndProtobuf_HandlesPauseResumeAndBodyLimit()
    {
        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));
        int persistNotifications = 0;

        await using OtlpHttpServer server = new(
            store,
            port: 0,
            maxBodyBytes: 4096,
            onPersisted: () => Interlocked.Increment(ref persistNotifications));
        server.Start();
        Assert.True(server.Port > 0);
        Assert.True(server.IsListening);

        using HttpClient client = new()
        {
            BaseAddress = new Uri(server.Endpoint),
        };

        // /healthz returns service descriptor
        using (HttpResponseMessage healthRes = await client.GetAsync(Rel(OtlpCollector.HealthzPath)))
        {
            Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);
            using JsonDocument healthDoc = JsonDocument.Parse(await healthRes.Content.ReadAsStringAsync());
            Assert.Equal("kyberdash-otlp", healthDoc.RootElement.GetProperty("service").GetString());
            Assert.False(healthDoc.RootElement.GetProperty("paused").GetBoolean());
        }

        // POST /v1/traces JSON
        string traceJson = """
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [{ "key": "service.name", "value": { "stringValue": "claude-host" } }]
                  },
                  "scopeSpans": [
                    {
                      "scope": { "name": "test", "version": "1.0" },
                      "spans": [
                        {
                          "traceId": "0102030405060708090a0b0c0d0e0f10",
                          "spanId": "aabbccddeeff0011",
                          "name": "chat.turn",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1760000000000000000",
                          "endTimeUnixNano": "1760000000200000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "claude.session_id", "value": { "stringValue": "http-sess-1" } },
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "500" } },
                            { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "80" } }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        using (StringContent jsonContent = new(traceJson, Encoding.UTF8, "application/json"))
        using (HttpResponseMessage traceRes = await client.PostAsync(Rel(OtlpCollector.TracesPath), jsonContent))
        {
            Assert.Equal(HttpStatusCode.OK, traceRes.StatusCode);
            using JsonDocument traceAck = JsonDocument.Parse(await traceRes.Content.ReadAsStringAsync());
            Assert.Equal(1, traceAck.RootElement.GetProperty("acceptedSpans").GetInt32());
        }

        Assert.True(store.HasRecord("aabbccddeeff0011"));

        // POST /v1/logs Protobuf
        byte[] pbLogMsg = EncodeMessage(
            PbFixed64(1, 1760000001000000000UL),
            PbBytes(5, PbAnyString("pb-log-body")),
            PbKeyValue(6, "claude.session_id", PbAnyString("http-sess-1")),
            PbBytes(9, Convert.FromHexString("0102030405060708090a0b0c0d0e0f10")),
            PbBytes(10, Convert.FromHexString("aabbccddeeff0011")));
        byte[] pbLogsReq = EncodeMessage(
            PbBytes(1, EncodeMessage(
                PbBytes(1, EncodeMessage(PbKeyValue(1, "service.name", PbAnyString("claude-host")))),
                PbBytes(2, EncodeMessage(PbBytes(2, pbLogMsg))))));

        using (ByteArrayContent pbLogContent = CreateProtobufContent(pbLogsReq))
        using (HttpResponseMessage logRes = await client.PostAsync(Rel(OtlpCollector.LogsPath), pbLogContent))
        {
            Assert.Equal(HttpStatusCode.OK, logRes.StatusCode);
            using JsonDocument logAck = JsonDocument.Parse(await logRes.Content.ReadAsStringAsync());
            Assert.Equal(1, logAck.RootElement.GetProperty("acceptedLogs").GetInt32());
        }

        // POST /v1/metrics Protobuf
        byte[] pbMetricPoint = EncodeMessage(
            PbFixed64(2, 1760000000000000000UL),
            PbFixed64(3, 1760000001000000000UL),
            PbFixed64(6, 75UL));
        byte[] pbMetric = EncodeMessage(
            PbString(1, "gen_ai.test.metric"),
            PbBytes(5, EncodeMessage(PbBytes(1, pbMetricPoint))));
        byte[] pbMetricsReq = EncodeMessage(
            PbBytes(1, EncodeMessage(
                PbBytes(1, EncodeMessage(PbKeyValue(1, "service.name", PbAnyString("claude-host")))),
                PbBytes(2, EncodeMessage(PbBytes(2, pbMetric))))));

        using (ByteArrayContent pbMetricContent = CreateProtobufContent(pbMetricsReq))
        using (HttpResponseMessage metricRes = await client.PostAsync(Rel(OtlpCollector.MetricsPath), pbMetricContent))
        {
            Assert.Equal(HttpStatusCode.OK, metricRes.StatusCode);
            using JsonDocument metricAck = JsonDocument.Parse(await metricRes.Content.ReadAsStringAsync());
            Assert.Equal(1, metricAck.RootElement.GetProperty("acceptedMetrics").GetInt32());
        }

        Assert.True(persistNotifications >= 3);

        // Unsupported media type -> 415
        using (StringContent xmlContent = new("<otlp/>", Encoding.UTF8, "application/xml"))
        using (HttpResponseMessage xmlRes = await client.PostAsync(Rel(OtlpCollector.TracesPath), xmlContent))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, xmlRes.StatusCode);
        }

        // Oversize body -> 413 at pre-body framing seam
        using (ByteArrayContent hugeContent = new(new byte[5000]))
        {
            hugeContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using HttpResponseMessage hugeRes = await client.PostAsync(Rel(OtlpCollector.TracesPath), hugeContent);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hugeRes.StatusCode);
        }

        // Admin pause blocks cross-origin requests with 403
        using (HttpRequestMessage crossOriginPause = new(HttpMethod.Post, Rel(OtlpHttpServer.AdminPausePath)))
        {
            crossOriginPause.Headers.Add("Origin", "http://evil.example");
            using HttpResponseMessage forbiddenRes = await client.SendAsync(crossOriginPause);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRes.StatusCode);
        }

        // Loopback pause -> signal routes return 503 Retry-After -> resume restores 200
        using (StringContent pauseBody = new("{\"leaseMs\":60000}", Encoding.UTF8, "application/json"))
        using (HttpResponseMessage pauseRes = await client.PostAsync(Rel(OtlpHttpServer.AdminPausePath), pauseBody))
        {
            Assert.Equal(HttpStatusCode.OK, pauseRes.StatusCode);
        }

        Assert.True(server.IsPaused);

        using (StringContent blockedContent = new(traceJson, Encoding.UTF8, "application/json"))
        using (HttpResponseMessage pausedSignalRes = await client.PostAsync(Rel(OtlpCollector.TracesPath), blockedContent))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pausedSignalRes.StatusCode);
            Assert.Contains("5", pausedSignalRes.Headers.GetValues("Retry-After"));
        }

        using (HttpResponseMessage resumeRes = await client.PostAsync(Rel(OtlpHttpServer.AdminResumePath), content: null))
        {
            Assert.Equal(HttpStatusCode.OK, resumeRes.StatusCode);
        }

        Assert.False(server.IsPaused);

        using (StringContent resumedContent = new(traceJson, Encoding.UTF8, "application/json"))
        using (HttpResponseMessage resumedSignalRes = await client.PostAsync(Rel(OtlpCollector.TracesPath), resumedContent))
        {
            Assert.Equal(HttpStatusCode.OK, resumedSignalRes.StatusCode);
        }
    }

    [Fact]
    public void CanonSettings_And_CanonClean_EnforceValidationAndWipeScopes()
    {
        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));

        // Default settings and fail-closed decoding of corrupted values
        SettingsView defaults = CanonSettings.ReadSettings(store);
        Assert.False(defaults.FolderImportScheduled);
        Assert.False(defaults.JobsPaused);
        Assert.Equal(CanonSettings.DefaultRefreshCadenceMinutes, defaults.RefreshCadenceMinutes);
        Assert.False(defaults.ReceiverHosted);
        Assert.Equal(CanonSettings.DefaultDashboardPort, defaults.DashboardPort);

        store.SetMetadata(CanonSettings.FolderImportScheduledKey, "maybe");
        store.SetMetadata(CanonSettings.RefreshCadenceMinutesKey, "9999");
        store.SetMetadata(CanonSettings.DashboardPortKey, "0");
        SettingsView recovered = CanonSettings.ReadSettings(store);
        Assert.False(recovered.FolderImportScheduled);
        Assert.Equal(CanonSettings.DefaultRefreshCadenceMinutes, recovered.RefreshCadenceMinutes);
        Assert.Equal(CanonSettings.DefaultDashboardPort, recovered.DashboardPort);

        // Partial update rejects invalid values atomically before writing valid sibling fields
        using (JsonDocument badPatch = JsonDocument.Parse("{\"folderImportScheduled\": true, \"refreshCadenceMinutes\": 1.5}"))
        {
            bool applied = CanonSettings.TryApplyPartialSettings(store, badPatch.RootElement, out _, out string? err);
            Assert.False(applied);
            Assert.NotNull(err);
        }

        Assert.False(CanonSettings.ReadSettings(store).FolderImportScheduled);

        using (JsonDocument unknownKeyPatch = JsonDocument.Parse("{\"unknownFlag\": true}"))
        {
            Assert.False(CanonSettings.TryApplyPartialSettings(store, unknownKeyPatch.RootElement, out _, out _));
        }

        using (JsonDocument validPatch = JsonDocument.Parse(
            "{\"folderImportScheduled\": true, \"jobsPaused\": true, \"refreshCadenceMinutes\": 15, \"receiverHosted\": true, \"dashboardPort\": 4848}"))
        {
            Assert.True(CanonSettings.TryApplyPartialSettings(store, validPatch.RootElement, out SettingsView? next, out string? error));
            Assert.Null(error);
            Assert.NotNull(next);
            Assert.True(next.FolderImportScheduled);
            Assert.True(next.JobsPaused);
            Assert.Equal(15, next.RefreshCadenceMinutes);
            Assert.True(next.ReceiverHosted);
            Assert.Equal(4848, next.DashboardPort);
        }

        // Seed two harnesses to test selective and full CanonClean scopes
        SeedMultiHarnessData(store);
        CanonSessionProjector.ProjectIncremental(store);
        CanonRollups.ProjectRunsAndHarnesses(store);

        Assert.NotEmpty(store.ListSessions("claude-code"));
        Assert.NotEmpty(store.ListSessions("copilot"));

        // Invalid clean scopes fail fast before invoking pauseIngestion
        int pauseCount = 0;
        int resumeCount = 0;
        Assert.Throws<ArgumentException>(() => CanonClean.CleanDatabase(
            store,
            new CleanScope(All: false, Harnesses: null),
            () => pauseCount++,
            () => resumeCount++));
        Assert.Throws<ArgumentException>(() => CanonClean.CleanDatabase(
            store,
            new CleanScope(All: true, Harnesses: ["claude-code"])));
        Assert.Throws<ArgumentException>(() => CanonClean.CleanDatabase(
            store,
            new CleanScope(Harnesses: ["unknown-harness-xyz"])));
        Assert.Throws<ArgumentException>(() => CanonClean.CleanDatabase(
            store,
            new CleanScope(All: true, ReingestWeeks: CanonClean.MaxCleanReingestWeeks + 1)));
        Assert.Equal(0, pauseCount);

        // Even if reingest throws, finally still invokes resumeIngestion
        Assert.Throws<InvalidOperationException>(() => CanonClean.CleanDatabase(
            store,
            new CleanScope(Harnesses: ["claude-cli"], ReingestWeeks: 2),
            () => pauseCount++,
            () => resumeCount++,
            (_, _) => throw new InvalidOperationException("reingest boom")));
        Assert.Equal(1, pauseCount);
        Assert.Equal(1, resumeCount);

        // claude-cli normalizes to claude-code; copilot stays intact
        Assert.Empty(store.ListSessions("claude-code"));
        Assert.NotEmpty(store.ListSessions("copilot"));
        Assert.Equal(0, store.GetHarnessRollup("claude-code")?.SampleCount ?? 0);
        Assert.True((store.GetHarnessRollup("copilot")?.SampleCount ?? 0) > 0);

        // Re-seed claude-code and run CleanScope(All: true)
        SeedMultiHarnessData(store);
        CanonSessionProjector.ProjectIncremental(store);
        CanonRollups.ProjectRunsAndHarnesses(store);

        CleanReport fullReport = CanonClean.CleanDatabase(
            store,
            new CleanScope(All: true),
            () => pauseCount++,
            () => resumeCount++);
        Assert.Equal(["*"], fullReport.Harnesses);
        Assert.Equal(0, store.CountRecords());
        Assert.Empty(store.ListSessions());
        Assert.All(store.ListHarnessRollups(), static r => Assert.Equal(0, r.SampleCount));
        Assert.Equal(2, pauseCount);
        Assert.Equal(2, resumeCount);
    }

    [Fact]
    public async Task KyberApiServer_ServesSessionsSummaryContentFindingsHarnessesRunsAndAnalytics()
    {
        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));
        SeedMultiHarnessData(store);

        using KyberJobHost jobHost = new(store, directory.Path);
        Assert.True(jobHost.TryStartHosting());

        await using KyberApiServer server = new(store, jobHost: jobHost, port: 0, stateDir: directory.Path);
        server.Start();

        using HttpClient client = new()
        {
            BaseAddress = new Uri(server.Endpoint),
        };

        // Sessions list & root-child hierarchy
        using (HttpResponseMessage sessionsRes = await client.GetAsync(Rel("/api/kyber/sessions")))
        {
            Assert.Equal(HttpStatusCode.OK, sessionsRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await sessionsRes.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement.GetArrayLength() >= 2);

            JsonElement? copilotRoot = null;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (string.Equals(item.GetProperty("session_id").GetString(), "sess-copilot-root", StringComparison.Ordinal))
                    copilotRoot = item.Clone();
            }

            Assert.True(copilotRoot.HasValue);
            Assert.Equal("copilot", copilotRoot.Value.GetProperty("harness").GetString());
            JsonElement subagents = copilotRoot.Value.GetProperty("subagents");
            Assert.Equal(1, subagents.GetArrayLength());
            Assert.Equal("sess-copilot-child", subagents[0].GetProperty("session_id").GetString());
        }

        // Session summary & content
        using (HttpResponseMessage summaryRes = await client.GetAsync(Rel("/api/kyber/session/sess-copilot-root/summary")))
        {
            Assert.Equal(HttpStatusCode.OK, summaryRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await summaryRes.Content.ReadAsStringAsync());
            Assert.Equal("sess-copilot-root", doc.RootElement.GetProperty("session_id").GetString());
            Assert.False(doc.RootElement.TryGetProperty("turns", out _));
        }

        using (HttpResponseMessage contentRes = await client.GetAsync(Rel("/api/kyber/session/sess-copilot-root/content?part=system_prompt")))
        {
            Assert.Equal(HttpStatusCode.OK, contentRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await contentRes.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("items").GetArrayLength() >= 1);
            Assert.Contains("You are Copilot", doc.RootElement.GetProperty("items")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        }

        // Findings & finding detail
        string? firstFindingId = null;
        using (HttpResponseMessage findingsRes = await client.GetAsync(Rel("/api/kyber/findings")))
        {
            Assert.Equal(HttpStatusCode.OK, findingsRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await findingsRes.Content.ReadAsStringAsync());
            JsonElement items = doc.RootElement.GetProperty("findings");
            Assert.True(items.GetArrayLength() >= 1);
            firstFindingId = items[0].GetProperty("Id").GetString();
        }

        Assert.False(string.IsNullOrEmpty(firstFindingId));
        using (HttpResponseMessage findingDetailRes = await client.GetAsync(Rel($"/api/kyber/finding/{Uri.EscapeDataString(firstFindingId)}")))
        {
            Assert.Equal(HttpStatusCode.OK, findingDetailRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await findingDetailRes.Content.ReadAsStringAsync());
            Assert.Equal(firstFindingId, doc.RootElement.GetProperty("Id").GetString());
        }

        // Harnesses, coverage, runs, compare & issue #190 completedPairCount rejection
        using (HttpResponseMessage harnessesRes = await client.GetAsync(Rel("/api/kyber/harnesses")))
        {
            Assert.Equal(HttpStatusCode.OK, harnessesRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await harnessesRes.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetArrayLength() >= 2);
        }

        using (HttpResponseMessage coverageRes = await client.GetAsync(Rel("/api/kyber/coverage")))
        {
            Assert.Equal(HttpStatusCode.OK, coverageRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await coverageRes.Content.ReadAsStringAsync());
            Assert.Equal(6, doc.RootElement.GetProperty("dimensions").GetArrayLength());
        }

        using (HttpResponseMessage runsRes = await client.GetAsync(Rel("/api/kyber/runs")))
        {
            Assert.Equal(HttpStatusCode.OK, runsRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await runsRes.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetArrayLength() >= 2);
        }

        using (HttpResponseMessage compareRunsRes = await client.GetAsync(Rel("/api/kyber/compare/runs?runA=run-copilot-1&runB=run-claude-1")))
        {
            Assert.Equal(HttpStatusCode.OK, compareRunsRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await compareRunsRes.Content.ReadAsStringAsync());
            Assert.Equal("run-copilot-1", doc.RootElement.GetProperty("runA").GetProperty("RunId").GetString());
        }

        using (HttpResponseMessage rejectedCompareRes = await client.GetAsync(Rel("/api/kyber/compare/runs?runA=run-copilot-1&runB=run-claude-1&completedPairCount=5")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejectedCompareRes.StatusCode);
        }

        // Analytics endpoints return envelope with Cache-Control: no-store
        using (HttpResponseMessage statusRes = await client.GetAsync(Rel("/api/kyber/analytics/status")))
        {
            Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
            Assert.True(statusRes.Headers.CacheControl?.NoStore);
            using JsonDocument doc = JsonDocument.Parse(await statusRes.Content.ReadAsStringAsync());
            Assert.Equal(KyberBridge.ApiContractVersion, doc.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("error").ValueKind);
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("value").GetProperty("ready").GetBoolean());
        }

        using (HttpResponseMessage catalogRes = await client.GetAsync(Rel("/api/kyber/analytics/catalog")))
        {
            Assert.Equal(HttpStatusCode.OK, catalogRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await catalogRes.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("operations").GetArrayLength() >= 8);
        }

        using (StringContent searchBody = new("{\"query\":\"copilot\"}", Encoding.UTF8, "application/json"))
        using (HttpResponseMessage searchRes = await client.PostAsync(Rel("/api/kyber/analytics/search-sessions"), searchBody))
        {
            Assert.Equal(HttpStatusCode.OK, searchRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await searchRes.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("items").GetArrayLength() >= 1);
        }

        using (StringContent inspectBody = new("{\"sessionId\":\"sess-copilot-root\"}", Encoding.UTF8, "application/json"))
        using (HttpResponseMessage inspectRes = await client.PostAsync(Rel("/api/kyber/analytics/inspect"), inspectBody))
        {
            Assert.Equal(HttpStatusCode.OK, inspectRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(await inspectRes.Content.ReadAsStringAsync());
            Assert.Equal("sess-copilot-root", doc.RootElement.GetProperty("data").GetProperty("session").GetProperty("session_id").GetString());
        }

        // Clean without confirm: true is rejected with 400; Origin on PUT/POST is rejected with 403
        using (StringContent unconfirmedClean = new("{\"all\":true}", Encoding.UTF8, "application/json"))
        using (HttpResponseMessage clean400 = await client.PostAsync(Rel("/api/kyber/clean"), unconfirmedClean))
        {
            Assert.Equal(HttpStatusCode.BadRequest, clean400.StatusCode);
        }

        using (HttpRequestMessage crossOriginPut = new(HttpMethod.Put, Rel("/api/kyber/settings"))
        {
            Content = new StringContent("{\"jobsPaused\":true}", Encoding.UTF8, "application/json"),
        })
        {
            crossOriginPut.Headers.Add("Origin", "http://evil.example");
            using HttpResponseMessage put403 = await client.SendAsync(crossOriginPut);
            Assert.Equal(HttpStatusCode.Forbidden, put403.StatusCode);
        }

        // SSE /api/events receives initial comment and store_updated notification on mutation
        using HttpClient sseClient = new() { BaseAddress = new Uri(server.Endpoint) };
        using HttpResponseMessage sseResponse = await sseClient.GetAsync(Rel("/api/events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        using Stream sseStream = await sseResponse.Content.ReadAsStreamAsync();
        using StreamReader sseReader = new(sseStream, Encoding.UTF8);

        string? connectedLine = await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(": connected", connectedLine);
        _ = await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)); // blank separator

        using (StringContent putSettings = new("{\"jobsPaused\":true}", Encoding.UTF8, "application/json"))
        using (HttpResponseMessage put200 = await client.PutAsync(Rel("/api/kyber/settings"), putSettings))
        {
            Assert.Equal(HttpStatusCode.OK, put200.StatusCode);
        }

        string? eventLine = await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        string? dataLine = await sseReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("event: update", eventLine);
        Assert.Contains("store_updated", dataLine, StringComparison.Ordinal);
    }

    [Fact]
    public void KyberJobHost_ManagesLeaseRefreshAndAtomicServerJsonWithSymlinkRefusal()
    {
        using TempDirectory directory = new();
        string stateDir = Path.Combine(directory.Path, "state");
        CanonStore store = new(Path.Combine(stateDir, "canon.db"));

        // PublishServerInfo -> 0600 permissions on Unix -> ReadServerInfo -> RemoveServerInfo
        KyberJobHost.PublishServerInfo(stateDir, port: 4747, pid: Environment.ProcessId);
        string serverJsonPath = Path.Combine(stateDir, KyberJobHost.ServerInfoFile);
        Assert.True(File.Exists(serverJsonPath));

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(serverJsonPath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        ServerInfo? readBack = KyberJobHost.ReadServerInfo(stateDir);
        Assert.NotNull(readBack);
        Assert.Equal(4747, readBack.Port);
        Assert.Equal(Environment.ProcessId, readBack.Pid);

        Assert.False(KyberJobHost.RemoveServerInfo(stateDir, pid: Environment.ProcessId + 1));
        Assert.True(File.Exists(serverJsonPath));
        Assert.True(KyberJobHost.RemoveServerInfo(stateDir, pid: Environment.ProcessId));
        Assert.False(File.Exists(serverJsonPath));

        // Symlink refusal on server.json and jobs.lock
        string victimPath = Path.Combine(directory.Path, "victim.txt");
        File.WriteAllText(victimPath, "untouched");
        File.CreateSymbolicLink(serverJsonPath, victimPath);
        try
        {
            Assert.Throws<IOException>(() => KyberJobHost.PublishServerInfo(stateDir, 4747, Environment.ProcessId));
            Assert.Throws<IOException>(() => KyberJobHost.ReadServerInfo(stateDir));
            Assert.Throws<IOException>(() => KyberJobHost.RemoveServerInfo(stateDir, Environment.ProcessId));
            Assert.Equal("untouched", File.ReadAllText(victimPath));
        }
        finally
        {
            File.Delete(serverJsonPath);
        }

        string lockPath = Path.Combine(stateDir, KyberJobHost.JobsLockFile);
        File.CreateSymbolicLink(lockPath, victimPath);
        try
        {
            Assert.Throws<IOException>(() => KyberJobHost.TryAcquireJobsLease(stateDir));
            Assert.Equal("untouched", File.ReadAllText(victimPath));
        }
        finally
        {
            File.Delete(lockPath);
        }

        // Stale lock with exited PID is reclaimed cleanly
        File.WriteAllText(lockPath, "{\"pid\":2147483000,\"acquiredAt\":\"2026-01-01T00:00:00.000Z\"}");
        using (JobsLease? lease = KyberJobHost.TryAcquireJobsLease(stateDir, pid: Environment.ProcessId))
        {
            Assert.NotNull(lease);
            // Live lock blocks acquisition by a competing PID
            Assert.Null(KyberJobHost.TryAcquireJobsLease(stateDir, pid: Environment.ProcessId + 1));
        }

        Assert.False(File.Exists(lockPath));

        // KyberJobHost refresh vs jobsPaused decline vs import-history bypass
        SeedMultiHarnessData(store);
        DateTimeOffset fixedNow = new(2026, 11, 15, 1, 0, 0, TimeSpan.Zero);
        int customPasses = 0;

        using KyberJobHost host = new(store, stateDir, clock: () => fixedNow);
        Assert.True(host.TryStartHosting());

        JobOutcome refreshOk = host.TriggerRefresh(_ => customPasses++);
        Assert.Equal("succeeded", refreshOk.Outcome);
        Assert.Equal(1, customPasses);

        JobHostStatus statusAfterRefresh = host.GetStatus();
        Assert.Equal("idle", statusAfterRefresh.State);
        Assert.False(statusAfterRefresh.Paused);
        Assert.False(statusAfterRefresh.HostedElsewhere);
        Assert.NotNull(statusAfterRefresh.LastSuccessAt);
        Assert.NotNull(statusAfterRefresh.NextDueAt);

        CanonSettings.WriteBooleanSetting(store, CanonSettings.JobsPausedKey, true);
        JobOutcome pausedRefresh = host.TriggerRefresh(_ => customPasses++);
        Assert.Equal("declined", pausedRefresh.Outcome);
        Assert.Equal(1, customPasses);

        JobOutcome importWhilePaused = host.TriggerImportHistory(_ => customPasses++);
        Assert.Equal("succeeded", importWhilePaused.Outcome);
        Assert.Equal(2, customPasses);
    }

    private static void SeedMultiHarnessData(CanonStore store)
    {
        Dictionary<string, JsonElement> copilotAttrs = new(StringComparer.Ordinal)
        {
            ["gen_ai.run.id"] = Elem("run-copilot-1"),
            ["copilot_chat.chat_session_id"] = Elem("sess-copilot-root"),
            ["gen_ai.request.model"] = Elem("gpt-5"),
        };

        string repeatedPrompt = "You are Copilot. " + new string('P', 1_200);
        IReadOnlyList<ContentPart> copilotParts =
        [
            new ContentPart("system_prompt", repeatedPrompt, Order: 0),
            new ContentPart("user_prompt", "Inspect the test failure", Order: 1),
        ];

        CanonicalRecord turn1 = new()
        {
            SpanId = "sp-copilot-t1",
            TraceId = "tr-copilot-1",
            ParentSpanId = null,
            Source = "otlp",
            Harness = "copilot",
            SessionId = "sess-copilot-root",
            Name = "llm.invoke",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = "2026-11-15T00:00:00.000Z",
            DurationMs = 1200,
            Status = "ok",
            Tokens = new TokenUsage(200, 100, 50, 120, 350, 120),
            Content = CanonValidation.ContentFromParts(copilotParts),
            Parts = copilotParts,
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.02, "USD"),
            Raw = copilotAttrs,
        };

        CanonicalRecord turn2 = new()
        {
            SpanId = "sp-copilot-t2",
            TraceId = "tr-copilot-1",
            ParentSpanId = "sp-copilot-t1",
            Source = "otlp",
            Harness = "copilot",
            SessionId = "sess-copilot-root",
            Name = "llm.invoke",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = "2026-11-15T00:00:10.000Z",
            DurationMs = 1400,
            Status = "ok",
            Tokens = new TokenUsage(10, 400, 0, 100, 410, 100),
            Content = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["system_prompt"] = repeatedPrompt + "\nmore",
                ["user_prompt"] = "Fix it",
            },
            Parts = copilotParts,
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.01, "USD"),
            Raw = copilotAttrs,
        };

        IReadOnlyList<ContentPart> oversizedParts =
        [
            new ContentPart("tool_result_content", new string('z', 15_000), Tokens: 3_750),
        ];
        CanonicalRecord oversizedTool = new()
        {
            SpanId = "sp-copilot-tool",
            TraceId = "tr-copilot-1",
            ParentSpanId = "sp-copilot-t2",
            Source = "otlp",
            Harness = "copilot",
            SessionId = "sess-copilot-root",
            Name = "tool.invoke",
            Op = "tool.invoke",
            Kind = "internal",
            Timestamp = "2026-11-15T00:00:12.000Z",
            DurationMs = 500,
            Status = "error",
            Tokens = new TokenUsage(0, 0, 0, 0, 0, 0),
            Content = CanonValidation.ContentFromParts(oversizedParts),
            Parts = oversizedParts,
            Cost = new CostBlock(CostBasis.Published, CostStatus.OutOfScope),
            Raw = new Dictionary<string, JsonElement>(copilotAttrs, StringComparer.Ordinal)
            {
                ["gen_ai.tool.name"] = Elem("bash"),
                ["gen_ai.tool.result_bytes"] = Elem(15_000),
            },
        };

        CanonicalRecord childTurn = new()
        {
            SpanId = "sp-copilot-child",
            TraceId = "tr-copilot-child",
            ParentSpanId = null,
            Source = "otlp",
            Harness = "copilot",
            SessionId = "sess-copilot-child",
            Name = "llm.invoke",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = "2026-11-15T00:00:05.000Z",
            DurationMs = 800,
            Status = "ok",
            Tokens = new TokenUsage(100, 50, 0, 40, 150, 40),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.005, "USD"),
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.run.id"] = Elem("run-copilot-1"),
                ["copilot_chat.chat_session_id"] = Elem("sess-copilot-child"),
                ["parent_session"] = Elem("sess-copilot-root"),
                ["gen_ai.agent.name"] = Elem("explorer"),
                ["gen_ai.is_subagent"] = Elem("true"),
                ["gen_ai.request.model"] = Elem("gpt-5"),
            },
        };

        CanonicalRecord claudeTurn = new()
        {
            SpanId = "sp-claude-t1",
            TraceId = "tr-claude-1",
            ParentSpanId = null,
            Source = "otlp",
            Harness = "claude-code",
            SessionId = "sess-claude-1",
            Name = "llm.invoke",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = "2026-11-15T00:01:00.000Z",
            DurationMs = 900,
            Status = "ok",
            Tokens = new TokenUsage(300, 1200, 100, 180, 1600, 180),
            Content = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["system_prompt"] = "You are Claude.",
            },
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.015, "USD"),
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.run.id"] = Elem("run-claude-1"),
                ["claude.session_id"] = Elem("sess-claude-1"),
                ["gen_ai.request.model"] = Elem("claude-sonnet-4-6"),
            },
        };

        store.UpsertRecords([turn1, turn2, oversizedTool, childTurn, claudeTurn]);
    }

    private static Uri Rel(string relativePath) => new(relativePath, UriKind.Relative);

    private static ByteArrayContent CreateProtobufContent(byte[] payload)
    {
        ByteArrayContent content = new(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return content;
    }

    private static JsonElement Elem(object value) => JsonSerializer.SerializeToElement(value);

    private static byte[] EncodeMessage(params byte[][] parts)
    {
        using MemoryStream ms = new();
        foreach (byte[] part in parts)
            ms.Write(part, 0, part.Length);
        return ms.ToArray();
    }

    private static byte[] PbVarint(int fieldNumber, ulong value)
    {
        using MemoryStream ms = new();
        WriteVarint(ms, (ulong)((fieldNumber << 3) | 0));
        WriteVarint(ms, value);
        return ms.ToArray();
    }

    private static byte[] PbFixed64(int fieldNumber, ulong value)
    {
        byte[] buf = new byte[10];
        using MemoryStream ms = new();
        WriteVarint(ms, (ulong)((fieldNumber << 3) | 1));
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        ms.Write(buf, 0, 8);
        return ms.ToArray();
    }

    private static byte[] PbBytes(int fieldNumber, byte[] payload)
    {
        using MemoryStream ms = new();
        WriteVarint(ms, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(ms, (ulong)payload.Length);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    private static byte[] PbString(int fieldNumber, string value)
    {
        return PbBytes(fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    private static byte[] PbAnyString(string value) => EncodeMessage(PbString(1, value));

    private static byte[] PbAnyInt(long value) => EncodeMessage(PbVarint(3, unchecked((ulong)value)));

    private static byte[] PbKeyValue(int fieldNumber, string key, byte[] anyValue)
    {
        byte[] kv = EncodeMessage(PbString(1, key), PbBytes(2, anyValue));
        return PbBytes(fieldNumber, kv);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }
}
