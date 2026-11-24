using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Domain.Tests;

/// <summary>
/// Token validation, UTF-8 bounding, harness adapter voting, OTLP JSON decoding, ingest accounting,
/// and bounded-writer backpressure ported from the C# engine's ingest parity suite.
/// </summary>
public class CanonIngestTests
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

        StringBuilder builder = new();
        builder.Append('a');
        while (Encoding.UTF8.GetByteCount(builder.ToString()) <= CanonValidation.MaxContentPartBytes + 64)
        {
            builder.Append("🚀");
        }

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

        RawSpan copilotTie = MakeRawSpan(
            "span-tie",
            "trace-tie",
            "svc-a",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element("sess-1"),
                ["gen_ai.agent.name"] = Element("antigravity"),
                ["gen_ai.usage.input_tokens"] = Element(100),
            });

        RawSpan sameSourceAlone = MakeRawSpan(
            "span-inherit",
            "trace-other",
            "svc-a",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.usage.input_tokens"] = Element(50),
            });

        RawSpan prefixOtherSource = MakeRawSpan(
            "span-prefix",
            "trace-prefix",
            "svc-ab",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.usage.input_tokens"] = Element(50),
            });

        RawSpan antigravitySpan = MakeRawSpan(
            "span-agy",
            "trace-agy",
            "agy",
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.system"] = Element("gemini"),
                ["gen_ai.agent.name"] = Element("antigravity"),
                ["gen_ai.usage.input_tokens"] = Element(200),
            });

        IReadOnlyDictionary<string, string> attributed = registry.Attribute(
            [copilotTie, sameSourceAlone, prefixOtherSource, antigravitySpan]);

        Assert.Equal("copilot", attributed["span-tie"]);
        Assert.Equal("copilot", attributed["span-inherit"]);
        Assert.False(attributed.ContainsKey("span-prefix"));
        Assert.Equal("antigravity", attributed["span-agy"]);
    }

    [Fact]
    public void IngestBatchRoundTripsAcceptedRecordsProblemsAndQuarantine()
    {
        InMemoryRelationalStore store = new();

        OtlpSpan copilotTurn = MakeOtlpSpan(
            spanId: "1111111111111111",
            traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            serviceName: "copilot-host",
            name: "chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element("sess-copilot"),
                ["gen_ai.usage.input_tokens"] = Element(1000),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(600),
                ["gen_ai.usage.cache_creation.input_tokens"] = Element(100),
                ["gen_ai.usage.output_tokens"] = Element(250),
                ["gen_ai.system_instructions"] = Element(new string('s', 400)),
            });

        OtlpSpan piTurn = MakeOtlpSpan(
            spanId: "2222222222222222",
            traceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            serviceName: "pi-host",
            name: "pi.agent.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Element("  sess-pi  "),
                ["gen_ai.usage.input_tokens"] = Element(50),
                ["gen_ai.usage.cache_read.input_tokens"] = Element(200),
                ["gen_ai.usage.output_tokens"] = Element(30),
                ["pi.skills"] = Element("debugging"),
            });

        OtlpSpan piBlankSession = MakeOtlpSpan(
            spanId: "2222222222222223",
            traceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbc",
            serviceName: "pi-host-blank",
            name: "pi.agent.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Element("   \t  "),
                ["gen_ai.usage.input_tokens"] = Element(10),
                ["gen_ai.usage.output_tokens"] = Element(5),
            });

        OtlpSpan badClaudeOutput = MakeOtlpSpan(
            spanId: "3333333333333333",
            traceId: "cccccccccccccccccccccccccccccccc",
            serviceName: "claude-host",
            name: "llm_request",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.deployment_mode"] = Element("1p"),
                ["session.id"] = Element("sess-claude-bad"),
                ["input_tokens"] = Element(20),
                ["output_tokens"] = Element(-7),
            });

        OtlpSpan structuralSpan = MakeOtlpSpan(
            spanId: "4444444444444444",
            traceId: "dddddddddddddddddddddddddddddddd",
            serviceName: "web-api",
            name: "GET /healthz",
            kind: "server",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["http.method"] = Element("GET"),
                ["custom.tag"] = Element("1"),
            });

        OtlpSpan badOpencodeVersion = MakeOtlpSpan(
            spanId: "5555555555555555",
            traceId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            serviceName: "opencode-host",
            name: "opencode.step",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = Element("2"),
                ["opencode.span.kind"] = Element("step"),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch(
            [copilotTurn, piTurn, piBlankSession, badClaudeOutput, structuralSpan, badOpencodeVersion],
            store,
            store);

        Assert.Equal(3, outcome.Accepted);
        Assert.Equal(2, outcome.Quarantined);
        Assert.Equal(1, outcome.Rejected);

        CollectedRecord? loadedCopilot = store.FindRecord("1111111111111111");
        Assert.NotNull(loadedCopilot);
        Assert.Equal("copilot", loadedCopilot.HarnessId);
        Assert.Equal("sess-copilot", loadedCopilot.SessionKey);
        Assert.Equal(RecordOp.LlmInvoke, loadedCopilot.Op);
        Assert.Equal(300, TokenCount(loadedCopilot, TokenClass.Input));
        Assert.Equal(600, TokenCount(loadedCopilot, TokenClass.CacheRead));
        Assert.Equal(100, TokenCount(loadedCopilot, TokenClass.CacheWrite));
        Assert.Equal(250, TokenCount(loadedCopilot, TokenClass.Output));
        Assert.Equal(1000, loadedCopilot.ReportedInputTokens);
        Assert.Contains(
            loadedCopilot.Parts,
            static part => part.Bucket == ContentBucket.SystemPrompt && part.PlainText == new string('s', 400));

        CollectedRecord? loadedPi = store.FindRecord("2222222222222222");
        Assert.NotNull(loadedPi);
        Assert.Equal("pi", loadedPi.HarnessId);
        Assert.Equal("sess-pi", loadedPi.SessionKey);
        Assert.Equal(50, TokenCount(loadedPi, TokenClass.Input));
        Assert.Equal(200, TokenCount(loadedPi, TokenClass.CacheRead));
        Assert.Equal(250, loadedPi.ReportedInputTokens);
        Assert.Contains(
            loadedPi.Parts,
            static part => part.Bucket == ContentBucket.InstructionContext && part.PlainText == "debugging");

        CollectedRecord? loadedPiBlank = store.FindRecord("2222222222222223");
        Assert.NotNull(loadedPiBlank);
        Assert.Equal("pi", loadedPiBlank.HarnessId);
        Assert.Null(loadedPiBlank.SessionKey);

        Assert.Null(store.FindRecord("3333333333333333"));
        StoredIngestProblem problem = Assert.Single(store.ListIngestProblems(spanId: "3333333333333333"));
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Problem.Code);
        Assert.Equal("claude-code", problem.Problem.HarnessId);

        StoredQuarantinedSpan? healthRow = store.GetQuarantinedSpan("4444444444444444");
        Assert.NotNull(healthRow);
        Assert.Equal("non-model span", healthRow.Reason);
        Assert.Equal(["custom", "http"], healthRow.ObservedNamespaces);

        StoredQuarantinedSpan? opencodeVersionRow = store.GetQuarantinedSpan("5555555555555555");
        Assert.NotNull(opencodeVersionRow);
        Assert.Equal(CanonAdapters.UnknownOpencodeContractVersionReason, opencodeVersionRow.Reason);
    }

    [Fact]
    public void OtlpCollectorDecodesTracesLogsStripsIdentityAndDerivesDeterministicLogId()
    {
        byte[] traceBytes = new byte[16];
        for (int index = 0; index < traceBytes.Length; index++)
        {
            traceBytes[index] = (byte)(index + 1);
        }

        byte[] spanBytes = [0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff, 0x01, 0x02];
        string traceBase64Url = Convert.ToBase64String(traceBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string spanBase64 = Convert.ToBase64String(spanBytes);

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
                          "traceId": "{{traceBase64Url}}",
                          "spanId": "{{spanBase64}}",
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

        byte[] oversized = new byte[OtlpCollector.DefaultMaxBodyBytes + 1];
        Assert.Throws<OtlpPayloadTooLargeException>(() => OtlpCollector.DecodeTracesJson(oversized));

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
        InMemoryRelationalStore store = new();
        BoundedIngestWriter writer = new();

        OtlpSpan singleSpan = MakeOtlpSpan(
            spanId: "1111111111111111",
            traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            serviceName: "pi-svc",
            name: "pi.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Element("sess-bounded"),
                ["gen_ai.usage.input_tokens"] = Element(10),
                ["gen_ai.usage.output_tokens"] = Element(5),
            });

        IngestAdmissionResult oversizeResult = writer.TryEnqueueSpans(
            [singleSpan],
            BoundedIngestWriter.DefaultMaxBodyBytes + 1);
        Assert.False(oversizeResult.Accepted);
        Assert.Equal(413, oversizeResult.HttpStatusCode);

        for (int index = 0; index < 4; index++)
        {
            OtlpSpan chunkSpan = singleSpan with
            {
                SpanId = string.Create(CultureInfo.InvariantCulture, $"111111111111111{index}"),
            };
            IngestAdmissionResult admitted = writer.TryEnqueueSpans([chunkSpan], BoundedIngestWriter.DefaultMaxBodyBytes);
            Assert.True(admitted.Accepted);
            Assert.Equal(200, admitted.HttpStatusCode);
        }

        Assert.Equal(BoundedIngestWriter.DefaultMaxPendingBytes, writer.PendingBytes);
        Assert.Equal(4, writer.PendingSpans);

        IngestAdmissionResult backpressure = writer.TryEnqueueSpans([singleSpan], 1);
        Assert.False(backpressure.Accepted);
        Assert.Equal(503, backpressure.HttpStatusCode);
        Assert.Equal(BoundedIngestWriter.RetryAfterSeconds, backpressure.RetryAfterSeconds);

        IngestOutcome flushOutcome = writer.Flush(store, store);
        Assert.Equal(4, flushOutcome.Accepted);
        Assert.Equal(0, writer.PendingBytes);
        Assert.Equal(0, writer.PendingSpans);
        Assert.NotNull(store.FindRecord("1111111111111110"));
        Assert.NotNull(store.FindRecord("1111111111111113"));
    }

    [Fact]
    public void CachedTokenCounterFlushesBufferedMissesIntoTheStore()
    {
        InMemoryRelationalStore store = new();
        CachedTokenCounter counter = new(store, "model-x", static text => text.Length, maxMemoEntries: 0);

        string text = new('b', CanonTokenCache.MinCachedTextLength);
        long counted = counter.Count(text);
        Assert.Equal(text.Length, counted);
        Assert.Null(store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-x")));

        counter.Flush();
        Assert.Equal(counted, store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-x")));
        counter.Flush();
        Assert.Equal(counted, store.GetCachedTokenCount(CanonTokenCache.CacheKey(text, "model-x")));
    }

    [Fact]
    public void CanonIngestPipelineHelpersExposeNamespacesAndStructuralSpans()
    {
        IReadOnlyDictionary<string, JsonElement> attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["gen_ai.usage.input_tokens"] = Element(1),
            ["session.id"] = Element("s"),
            ["gen_ai.response.model"] = Element("m"),
            ["plain"] = Element("p"),
        };

        Assert.Equal(["gen_ai", "plain", "session"], CanonIngestPipeline.ObservedNamespaces(attributes));

        RawSpan structural = new(
            SpanId: "s1",
            TraceId: "t1",
            ParentSpanId: null,
            Source: "web",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["http.method"] = Element("GET") },
            Name: "GET /healthz",
            Kind: "server",
            Timestamp: "2026-11-14T00:00:00.000Z");
        Assert.True(CanonIngestPipeline.IsStructuralSpan(structural));

        RawSpan modelCall = structural with { Kind = "client", Name = "llm.invoke", Attributes = attributes };
        Assert.False(CanonIngestPipeline.IsStructuralSpan(modelCall));
    }

    private static long TokenCount(CollectedRecord record, TokenClass tokenClass) =>
        record.Tokens.Single(token => token.TokenClass == tokenClass).Count;

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
                ["service.name"] = Element(serviceName),
            },
            Scope: new OtlpScope("test-scope", "1.0.0"));

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
}
