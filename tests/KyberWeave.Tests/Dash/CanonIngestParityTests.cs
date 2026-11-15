using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Dash;
using Xunit;

namespace KyberWeave.Tests.Dash;

/// <summary>
/// Verifies token validation, UTF-8 content bounding, harness adapter voting,
/// OTLP JSON decoding, bounded ingest backpressure, and end-to-end <see cref="CanonStore"/>
/// persistence parity between Slice 2 C# modules and <c>dash/src/canon/</c> + <c>dash/src/otel/</c>.
/// </summary>
public sealed class CanonIngestParityTests
{
    [Fact]
    public void ValidateTokensEnforcesAllInvariantsAndUnmeasuredOutputRule()
    {
        TokenUsage valid = new(FreshInput: 100, CacheRead: 400, CacheCreation: 50, Output: 80, ReportedInput: 550, ReportedOutput: 80, Reasoning: 30);
        Assert.Null(CanonValidation.ValidateTokens(valid, "span-valid"));

        Problem? negativeFresh = CanonValidation.ValidateTokens(
            new TokenUsage(-5, 400, 0, 10, 395, 10),
            "span-neg-fresh");
        Assert.NotNull(negativeFresh);
        Assert.Equal(ProblemSeverity.Error, negativeFresh.Severity);
        Assert.Equal(CanonValidation.TokenNegativeFresh, negativeFresh.Code);
        Assert.Equal("span-neg-fresh", negativeFresh.Location);

        Problem? negativeClass = CanonValidation.ValidateTokens(
            new TokenUsage(10, -1, 0, 10, 9, 10),
            "span-neg-class");
        Assert.NotNull(negativeClass);
        Assert.Equal(CanonValidation.TokenNegativeClass, negativeClass.Code);

        Problem? reasoningExceeds = CanonValidation.ValidateTokens(
            new TokenUsage(10, 0, 0, 20, 10, 20, Reasoning: 25),
            "span-reasoning");
        Assert.NotNull(reasoningExceeds);
        Assert.Equal(CanonValidation.TokenReasoningExceedsOutput, reasoningExceeds.Code);

        IReadOnlyDictionary<string, JsonElement> unmeasuredOutput = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["output"] = JsonSerializer.SerializeToElement(new { availability = "not_measurable", reason = "test" }),
        };
        Assert.Null(CanonValidation.ValidateTokens(
            new TokenUsage(10, 0, 0, 0, 10, 0, Reasoning: 25),
            "span-reasoning-unmeasured",
            unmeasuredOutput));

        Problem? sumMismatch = CanonValidation.ValidateTokens(
            new TokenUsage(100, 50, 10, 20, 999, 20),
            "span-sum");
        Assert.NotNull(sumMismatch);
        Assert.Equal(CanonValidation.TokenSumMismatch, sumMismatch.Code);
    }

    [Fact]
    public void TruncateContentPartUtf8Respects64KiBBoundAndCodepointBoundaries()
    {
        Assert.Equal(64 * 1024, CanonValidation.MaxContentPartBytes);

        // Repeat 4-byte emoji (U+1F680 🚀) so 64 KiB boundary would land cleanly on multiples of 4 bytes;
        // prefix one 'a' (1 byte) so the 65536-byte limit cuts straight into the middle of an emoji.
        StringBuilder builder = new();
        builder.Append('a');
        while (Encoding.UTF8.GetByteCount(builder.ToString()) <= CanonValidation.MaxContentPartBytes + 64)
            builder.Append("🚀");

        string truncated = CanonValidation.TruncateContentPartUtf8(builder.ToString());
        int utf8Bytes = Encoding.UTF8.GetByteCount(truncated);

        Assert.True(utf8Bytes <= CanonValidation.MaxContentPartBytes);
        Assert.Equal(CanonValidation.MaxContentPartBytes - 3, utf8Bytes);
        Assert.DoesNotContain("\uFFFD", truncated, StringComparison.Ordinal);
        Assert.EndsWith("🚀", truncated, StringComparison.Ordinal);

        IReadOnlyDictionary<string, string> collapsed = CanonValidation.ContentFromParts(
        [
            new ContentPart("conversation_history", "second", Order: 2),
            new ContentPart("conversation_history", "first", Order: 1),
            new ContentPart("system_prompt", builder.ToString(), Order: 0),
        ]);
        Assert.Equal("first\nsecond", collapsed["conversation_history"]);
        Assert.True(Encoding.UTF8.GetByteCount(collapsed["system_prompt"]) <= CanonValidation.MaxContentPartBytes);
    }

    [Fact]
    public void AdapterRegistryRunsPass1VoteAndPass2ExactSourceInheritance()
    {
        AdapterRegistry registry = new(CanonAdapters.DefaultAdapters);

        // Pass 1 tie-break: copilot is registered before antigravity, so copilot_chat.* + antigravity agent ties 1.0 -> copilot.
        RawSpan copilotTie = MakeRawSpan(
            "span-tie",
            "trace-tie",
            "svc-a",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Elem("sess-1"),
                ["gen_ai.agent.name"] = Elem("antigravity"),
                ["gen_ai.usage.input_tokens"] = Elem(100),
            });

        // Pass 2 exact-source inheritance: GenAI-only span (0.4 < 0.6) in the same source inherits copilot,
        // while a different source sharing only a prefix stays unclaimed.
        RawSpan sameSourceAlone = MakeRawSpan(
            "span-inherit",
            "trace-other",
            "svc-a",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.usage.input_tokens"] = Elem(50),
            });

        RawSpan prefixOtherSource = MakeRawSpan(
            "span-prefix",
            "trace-prefix",
            "svc-ab",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.usage.input_tokens"] = Elem(50),
            });

        // Pass 1 live Antigravity shape: gen_ai.system=gemini yields to gen_ai.agent.name=antigravity.
        RawSpan agySpan = MakeRawSpan(
            "span-agy",
            "trace-agy",
            "agy",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.system"] = Elem("gemini"),
                ["gen_ai.agent.name"] = Elem("antigravity"),
                ["gen_ai.usage.input_tokens"] = Elem(200),
            });

        IReadOnlyDictionary<string, string> attributed = registry.Attribute(
            [copilotTie, sameSourceAlone, prefixOtherSource, agySpan]);

        Assert.Equal("copilot", attributed["span-tie"]);
        Assert.Equal("copilot", attributed["span-inherit"]);
        Assert.False(attributed.ContainsKey("span-prefix"));
        Assert.Equal("antigravity", attributed["span-agy"]);
    }

    [Fact]
    public void IngestBatchRoundTripsAcceptedRecordsProblemsQuarantineAndDirtySessions()
    {
        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));

        OtlpSpan copilotTurn = MakeOtlpSpan(
            spanId: "1111111111111111",
            traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            serviceName: "copilot-host",
            name: "chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Elem("sess-copilot"),
                ["gen_ai.usage.input_tokens"] = Elem(1000),
                ["gen_ai.usage.cache_read.input_tokens"] = Elem(600),
                ["gen_ai.usage.cache_creation.input_tokens"] = Elem(100),
                ["gen_ai.usage.output_tokens"] = Elem(250),
                ["gen_ai.system_instructions"] = Elem(new string('s', 400)),
            });

        OtlpSpan piTurn = MakeOtlpSpan(
            spanId: "2222222222222222",
            traceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            serviceName: "pi-host",
            name: "pi.agent.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Elem("sess-pi"),
                ["gen_ai.usage.input_tokens"] = Elem(50),
                ["gen_ai.usage.cache_read.input_tokens"] = Elem(200),
                ["gen_ai.usage.output_tokens"] = Elem(30),
                ["pi.skills"] = Elem("debugging"),
            });

        OtlpSpan badClaudeOutput = MakeOtlpSpan(
            spanId: "3333333333333333",
            traceId: "cccccccccccccccccccccccccccccccc",
            serviceName: "claude-host",
            name: "llm_request",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.deployment_mode"] = Elem("1p"),
                ["session.id"] = Elem("sess-claude-bad"),
                ["input_tokens"] = Elem(20),
                ["output_tokens"] = Elem(-7),
            });

        OtlpSpan structuralSpan = MakeOtlpSpan(
            spanId: "4444444444444444",
            traceId: "dddddddddddddddddddddddddddddddd",
            serviceName: "web-api",
            name: "GET /healthz",
            kind: "server",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["http.method"] = Elem("GET"),
                ["custom.tag"] = Elem("1"),
            });

        OtlpSpan badOpencodeVersion = MakeOtlpSpan(
            spanId: "5555555555555555",
            traceId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            serviceName: "opencode-host",
            name: "opencode.step",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Elem("2"),
                ["opencode.span.kind"] = Elem("step"),
            });

        IngestOutcome outcome = CanonIngest.IngestBatch(
            [copilotTurn, piTurn, badClaudeOutput, structuralSpan, badOpencodeVersion],
            store);

        Assert.Equal(2, outcome.Accepted);
        Assert.Equal(2, outcome.Quarantined);
        Assert.Equal(1, outcome.Rejected);

        CanonicalRecord? loadedCopilot = store.GetRecord("1111111111111111");
        Assert.NotNull(loadedCopilot);
        Assert.Equal("copilot", loadedCopilot.Harness);
        Assert.Equal("copilot-host", loadedCopilot.Source);
        Assert.Equal("sess-copilot", loadedCopilot.SessionId);
        Assert.Equal("llm.invoke", loadedCopilot.Op);
        Assert.Equal(300, loadedCopilot.Tokens.FreshInput);
        Assert.Equal(600, loadedCopilot.Tokens.CacheRead);
        Assert.Equal(100, loadedCopilot.Tokens.CacheCreation);
        Assert.Equal(1000, loadedCopilot.Tokens.ReportedInput);
        Assert.Equal(new string('s', 400), loadedCopilot.Content["system_prompt"]);
        Assert.NotNull(loadedCopilot.Raw);

        CanonicalRecord? loadedPi = store.GetRecord("2222222222222222");
        Assert.NotNull(loadedPi);
        Assert.Equal("pi", loadedPi.Harness);
        Assert.Null(loadedPi.SessionId);
        Assert.Equal(50, loadedPi.Tokens.FreshInput);
        Assert.Equal(200, loadedPi.Tokens.CacheRead);
        Assert.Equal(250, loadedPi.Tokens.ReportedInput);
        Assert.Equal("debugging", loadedPi.Content["instruction_context"]);

        Assert.False(store.HasRecord("3333333333333333"));
        IReadOnlyList<SpanProblemRow> problems = store.GetProblems("3333333333333333");
        SpanProblemRow problem = Assert.Single(problems);
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Code);
        Assert.Equal("claude-code", problem.Harness);

        QuarantineRow? healthRow = store.GetQuarantined("4444444444444444");
        Assert.NotNull(healthRow);
        Assert.Equal("non-model span", healthRow.Reason);
        Assert.Equal(["custom", "http"], healthRow.ObservedNamespaces);

        QuarantineRow? ocVersionRow = store.GetQuarantined("5555555555555555");
        Assert.NotNull(ocVersionRow);
        Assert.Equal(CanonAdapters.UnknownOpencodeContractVersionReason, ocVersionRow.Reason);

        Assert.Equal(1, store.RecordsWriteMarker());
        string? dirtyJson = store.GetMetadata(CanonSchema.ProjectionDirtySessionsKey);
        Assert.NotNull(dirtyJson);
        using JsonDocument dirtyDoc = JsonDocument.Parse(dirtyJson);
        Assert.Equal(0, dirtyDoc.RootElement.GetProperty("fromSeq").GetInt64());
        Assert.Equal(1, dirtyDoc.RootElement.GetProperty("toSeq").GetInt64());
        string[] dirtyKeys = [.. dirtyDoc.RootElement.GetProperty("keys").EnumerateArray().Select(static el => el.GetString()!)];
        Assert.Equal(["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "sess-copilot"], dirtyKeys);

        store.RecordIngest("  otlp-json  ", 2, "2026-11-14T23:00:00.000Z");
        IngestLogEntry audit = Assert.Single(store.GetIngestLog());
        Assert.Equal("otlp-json", audit.Source);
        Assert.Equal(2, audit.Count);
    }

    [Fact]
    public void OtlpCollectorDecodesTracesLogsStripsIdentityAndDerivesDeterministicLogId()
    {
        // Trace with base64 IDs (URL-safe unpadded) and both identity + retained correlation keys.
        byte[] traceBytes = new byte[16];
        for (int i = 0; i < traceBytes.Length; i++)
            traceBytes[i] = (byte)(i + 1);
        byte[] spanBytes = [0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff, 0x01, 0x02];
        string traceB64Url = Convert.ToBase64String(traceBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string spanB64 = Convert.ToBase64String(spanBytes);

        string traceJson = $$"""
            {
              "resourceSpans": [
                {
                  "resource": {
                    "attributes": [
                      { "key": "service.name", "value": { "stringValue": "copilot-cli" } },
                      { "key": "user.email", "value": { "stringValue": "secret@example.com" } },
                      { "key": "organization.id", "value": { "stringValue": "org-123" } }
                    ]
                  },
                  "scopeSpans": [
                    {
                      "scope": { "name": "kyber.scope", "version": "1.2.3" },
                      "spans": [
                        {
                          "traceId": "{{traceB64Url}}",
                          "spanId": "{{spanB64}}",
                          "name": "llm.chat",
                          "kind": "SPAN_KIND_CLIENT",
                          "startTimeUnixNano": "1760000000000000000",
                          "endTimeUnixNano": "1760000000250000000",
                          "status": { "code": "STATUS_CODE_OK" },
                          "attributes": [
                            { "key": "session.id", "value": { "stringValue": "sess-retained" } },
                            { "key": "user.message.id", "value": { "stringValue": "msg-1" } },
                            { "key": "userEmail", "value": { "stringValue": "strip@example.com" } },
                            { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "42" } }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        IReadOnlyList<OtlpSpan> spans = OtlpCollector.DecodeTracesJson(Encoding.UTF8.GetBytes(traceJson));
        OtlpSpan decoded = Assert.Single(spans);
        Assert.Equal(Convert.ToHexStringLower(traceBytes), decoded.TraceId);
        Assert.Equal("aabbccddeeff0102", decoded.SpanId);
        Assert.Equal("client", decoded.Kind);
        Assert.Equal("ok", decoded.Status.Code);
        Assert.Equal(250.0, decoded.DurationMs);
        Assert.Equal("copilot-cli", decoded.Resource["service.name"].GetString());
        Assert.False(decoded.Resource.ContainsKey("user.email"));
        Assert.False(decoded.Resource.ContainsKey("organization.id"));
        Assert.Equal("sess-retained", decoded.Attributes["session.id"].GetString());
        Assert.Equal("msg-1", decoded.Attributes["user.message.id"].GetString());
        Assert.False(decoded.Attributes.ContainsKey("userEmail"));
        Assert.Equal(42, decoded.Attributes["gen_ai.usage.input_tokens"].GetInt64());

        // Oversized body (> 64 MiB) is rejected at decode seam.
        byte[] oversized = new byte[OtlpCollector.DefaultMaxBodyBytes + 1];
        Assert.Throws<OtlpPayloadTooLargeException>(() => OtlpCollector.DecodeTracesJson(oversized));

        // Log decoding & DeriveLogId parity regardless of attribute ordering.
        string logJsonA = """
            {
              "resourceLogs": [{
                "scopeLogs": [{
                  "logRecords": [{
                    "timeUnixNano": "1760000000000000000",
                    "traceId": "0102030405060708090a0b0c0d0e0f10",
                    "spanId": "aabbccddeeff0102",
                    "eventName": "claude_code.api_request_body",
                    "body": { "stringValue": "payload+body" },
                    "attributes": [
                      { "key": "z_key", "value": { "intValue": "2" } },
                      { "key": "a_key", "value": { "stringValue": "alpha" } },
                      { "key": "session.id", "value": { "stringValue": "claude-s1" } }
                    ]
                  }]
                }]
              }]
            }
            """;

        string logJsonB = """
            {
              "resourceLogs": [{
                "scopeLogs": [{
                  "logRecords": [{
                    "timeUnixNano": "1760000000000000000",
                    "traceId": "0102030405060708090a0b0c0d0e0f10",
                    "spanId": "aabbccddeeff0102",
                    "eventName": "claude_code.api_request_body",
                    "body": { "stringValue": "payload+body" },
                    "attributes": [
                      { "key": "session.id", "value": { "stringValue": "claude-s1" } },
                      { "key": "a_key", "value": { "stringValue": "alpha" } },
                      { "key": "z_key", "value": { "intValue": "2" } }
                    ]
                  }]
                }]
              }]
            }
            """;

        OtlpLog logA = Assert.Single(OtlpCollector.DecodeLogsJson(Encoding.UTF8.GetBytes(logJsonA)));
        OtlpLog logB = Assert.Single(OtlpCollector.DecodeLogsJson(Encoding.UTF8.GetBytes(logJsonB)));
        Assert.Equal("claude-s1", logA.SessionId);
        Assert.Equal(logA.LogId, logB.LogId);
        Assert.StartsWith("0102030405060708090a0b0c0d0e0f10:aabbccddeeff0102:1760000000000000000:", logA.LogId, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedIngestWriterEnforces64MiBAnd256MiBCeilingsAndFlushesWithoutLoss()
    {
        BoundedIngestWriter writer = new();

        OtlpSpan singleSpan = MakeOtlpSpan(
            spanId: "1111111111111111",
            traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            serviceName: "pi-svc",
            name: "pi.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Elem("sess-bounded"),
                ["gen_ai.usage.input_tokens"] = Elem(10),
                ["gen_ai.usage.output_tokens"] = Elem(5),
            });

        // 64 MiB request cap -> permanent 413.
        IngestAdmissionResult oversizeResult = writer.TryEnqueueSpans(
            [singleSpan],
            BoundedIngestWriter.DefaultMaxBodyBytes + 1);
        Assert.False(oversizeResult.Accepted);
        Assert.Equal(413, oversizeResult.HttpStatusCode);

        // Fill up to 256 MiB via four 64 MiB chunks -> accepted with 200.
        for (int i = 0; i < 4; i++)
        {
            OtlpSpan chunkSpan = singleSpan with { SpanId = string.Create(CultureInfo.InvariantCulture, $"111111111111111{i}") };
            IngestAdmissionResult admitted = writer.TryEnqueueSpans([chunkSpan], BoundedIngestWriter.DefaultMaxBodyBytes);
            Assert.True(admitted.Accepted);
            Assert.Equal(200, admitted.HttpStatusCode);
        }

        Assert.Equal(BoundedIngestWriter.DefaultMaxPendingBytes, writer.PendingBytes);
        Assert.Equal(4, writer.PendingSpans);

        // Next 1 byte pushes past 256 MiB -> transient 503 with Retry-After: 1.
        IngestAdmissionResult backpressure = writer.TryEnqueueSpans([singleSpan], 1);
        Assert.False(backpressure.Accepted);
        Assert.Equal(503, backpressure.HttpStatusCode);
        Assert.Equal(1, backpressure.RetryAfterSeconds);

        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));
        IngestOutcome flushOutcome = writer.Flush(store);

        Assert.Equal(4, flushOutcome.Accepted);
        Assert.Equal(0, writer.PendingBytes);
        Assert.Equal(0, writer.PendingSpans);
        Assert.True(store.HasRecord("1111111111111110"));
        Assert.True(store.HasRecord("1111111111111113"));
    }

    private static RawSpan MakeRawSpan(
        string spanId,
        string? traceId,
        string source,
        IReadOnlyDictionary<string, JsonElement> attributes) =>
        new(
            SpanId: spanId,
            TraceId: traceId,
            ParentSpanId: null,
            Source: source,
            Attributes: attributes,
            Name: "span",
            Kind: "client",
            Timestamp: "2026-11-14T23:00:00.000Z");

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
            Timestamp: "2026-10-09T09:33:20.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("ok"),
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Elem(serviceName),
            },
            Scope: new OtlpScope("test-scope", "1.0.0"));

    private static JsonElement Elem(object value) => JsonSerializer.SerializeToElement(value);
}
