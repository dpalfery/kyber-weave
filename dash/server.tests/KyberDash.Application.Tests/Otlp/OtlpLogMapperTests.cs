using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Otlp;

/// <summary>
/// Unit tests for <see cref="OtlpLogMapper"/> verifying on-receipt OTLP log mapping to
/// relational collector DTOs and payload-free <c>ingest_problem</c> recording for unmappable logs.
/// </summary>
public class OtlpLogMapperTests
{
    [Fact]
    public void Map_EmptyBatch_ReturnsEmptyBatchesProblemsAndZeroOutcome()
    {
        OtlpLogMapResult result = OtlpLogMapper.Map([]);

        Assert.Empty(result.Batches);
        Assert.Empty(result.StandaloneProblems);
        Assert.Equal(new IngestOutcome(0, 0, 0), result.Outcome);
    }

    [Fact]
    public void Map_AttributedClaudeLogWithJsonBody_MapsBatchAndWithholdsRawJsonBody()
    {
        string rawBody = "{\"model\":\"claude-sonnet-4-5\",\"secret\":\"never-persist-in-plain-text\"}";
        OtlpLog log = new(
            LogId: "log-claude-1",
            TraceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            SpanId: "1111111111111111",
            SessionId: "sess-claude-log",
            Timestamp: "2026-11-19T12:00:00.000Z",
            Body: rawBody,
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude_code.event"] = JsonSerializer.SerializeToElement("api_request_body"),
                ["event.name"] = JsonSerializer.SerializeToElement("claude_code.api_request_body"),
                ["gen_ai.request.model"] = JsonSerializer.SerializeToElement("claude-sonnet-4-5"),
                ["gen_ai.usage.input_tokens"] = JsonSerializer.SerializeToElement(120),
                ["gen_ai.usage.output_tokens"] = JsonSerializer.SerializeToElement(30),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = JsonSerializer.SerializeToElement("claude-code"),
            },
            Scope: new OtlpScope("claude_code", "1.0.0"));

        OtlpLogMapResult result = OtlpLogMapper.Map([log]);

        Assert.Equal(new IngestOutcome(1, 0, 0), result.Outcome);
        Assert.Empty(result.StandaloneProblems);

        CollectedBatch batch = Assert.Single(result.Batches);
        Assert.Equal("claude-code", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
        Assert.Equal("claude-code", batch.Source.Locator);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-claude-log", session.SessionKey);
        Assert.Equal("claude-code", session.HarnessId);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("log-claude-1", record.RecordId);
        Assert.Equal("claude-code", record.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("sess-claude-log", record.SessionKey);
        Assert.Equal("1111111111111111", record.ParentRecordId);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", record.TraceId);
        Assert.Equal("claude-sonnet-4-5", record.Model);

        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.Null(part.PlainText);
        Assert.NotNull(part.Withheld);
        Assert.True(part.Withheld.Utf8Bytes > 0);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Equal(1, store.CountRecords("claude-code"));
        Assert.Empty(store.ListIngestProblems());
        Assert.All(store.ContentParts("log-claude-1"), static p => Assert.Null(p.Part.PlainText));
    }

    [Fact]
    public void Map_AttributedLogWithPlainTextBody_PersistsBoundedPlainTextPart()
    {
        OtlpLog log = new(
            LogId: "log-codex-1",
            TraceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            SpanId: null,
            SessionId: "sess-codex-log",
            Timestamp: "2026-11-19T12:05:00.000Z",
            Body: "Summarize the failing test output.",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = JsonSerializer.SerializeToElement("codex.user_prompt"),
                ["codex.turn"] = JsonSerializer.SerializeToElement("1"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope("codex", "0.1.0"));

        OtlpLogMapResult result = OtlpLogMapper.Map([log]);

        Assert.Equal(new IngestOutcome(1, 0, 0), result.Outcome);
        CollectedBatch batch = Assert.Single(result.Batches);
        Assert.Equal("codex", batch.Source.HarnessId);
        Assert.Equal("otlp", batch.Source.Locator);

        CollectedRecord record = Assert.Single(batch.Records);
        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.Equal(ContentBucket.UserPrompt, part.Bucket);
        Assert.Equal("Summarize the failing test output.", part.PlainText);
        Assert.Null(part.Withheld);
    }

    [Fact]
    public void Map_UnattributableLog_RecordsIngestProblemWithZeroRawPayloadKept()
    {
        OtlpLog unknown = new(
            LogId: "log-unknown-1",
            TraceId: "cccccccccccccccccccccccccccccccc",
            SpanId: "2222222222222222",
            SessionId: "sess-unknown",
            Timestamp: "2026-11-19T12:10:00.000Z",
            Body: "sensitive-raw-log-body-never-persist",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["mystery.vendor_secret"] = JsonSerializer.SerializeToElement("secret-value-do-not-store"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = JsonSerializer.SerializeToElement("mystery-service"),
            },
            Scope: new OtlpScope());

        OtlpLogMapResult result = OtlpLogMapper.Map([unknown]);

        Assert.Empty(result.Batches);
        Assert.Equal(new IngestOutcome(0, 0, 1), result.Outcome);

        CollectedProblem problem = Assert.Single(result.StandaloneProblems);
        Assert.Equal("OTLP_LOG_UNATTRIBUTED", problem.Code);
        Assert.DoesNotContain("sensitive-raw-log-body-never-persist", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value-do-not-store", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", problem.Message, StringComparison.Ordinal);
        Assert.Equal("2222222222222222", problem.SpanId);
        Assert.Equal("sess-unknown", problem.SessionKey);
        Assert.Null(problem.HarnessId);

        InMemoryRelationalStore store = new();
        store.WriteProblem(problem);
        Assert.Equal(1, store.CountIngestProblems());
        Assert.Equal(0, store.CountRecords());
    }

    [Fact]
    public void Map_InvalidTimestampOrValidationFailure_RecordsIngestProblemWithoutKeepingPayload()
    {
        OtlpLog badTimestamp = new(
            LogId: "log-bad-ts",
            TraceId: null,
            SpanId: null,
            SessionId: null,
            Timestamp: "not-a-timestamp",
            Body: "raw-body-must-not-appear",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude_code.event"] = JsonSerializer.SerializeToElement("api_request"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope());

        OtlpLog negativeTokens = new(
            LogId: "log-neg-tokens",
            TraceId: null,
            SpanId: null,
            SessionId: "sess-bad",
            Timestamp: "2026-11-19T12:15:00.000Z",
            Body: "{\"secret\":\"negative-token-body\"}",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.session_id"] = JsonSerializer.SerializeToElement("sess-bad"),
                ["gen_ai.usage.input_tokens"] = JsonSerializer.SerializeToElement(10),
                ["gen_ai.usage.output_tokens"] = JsonSerializer.SerializeToElement(-5),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope());

        OtlpLogMapResult result = OtlpLogMapper.Map([badTimestamp, negativeTokens]);

        Assert.Empty(result.Batches);
        Assert.Equal(new IngestOutcome(0, 0, 2), result.Outcome);
        Assert.Equal(2, result.StandaloneProblems.Count);

        Assert.Contains(result.StandaloneProblems, static p => p.Code == "OTLP_LOG_INVALID_TIMESTAMP");
        Assert.Contains(result.StandaloneProblems, static p => p.Code == CanonValidation.TokenNegativeClass);
        foreach (CollectedProblem problem in result.StandaloneProblems)
        {
            Assert.DoesNotContain("raw-body-must-not-appear", problem.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("negative-token-body", problem.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Map_MixedBatchAndUnsupportedEnvelope_RoutesMappedRecordsAndProblemsWithoutKeepingPayload()
    {
        OtlpLog validCopilotLog = new(
            LogId: "log-copilot-1",
            TraceId: "dddddddddddddddddddddddddddddddd",
            SpanId: null,
            SessionId: "sess-copilot-log",
            Timestamp: "2026-11-19T12:20:00.000Z",
            Body: "copilot log message",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = JsonSerializer.SerializeToElement("sess-copilot-log"),
                ["gen_ai.usage.input_tokens"] = JsonSerializer.SerializeToElement(40),
                ["gen_ai.usage.output_tokens"] = JsonSerializer.SerializeToElement(10),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = JsonSerializer.SerializeToElement("copilot-host"),
            },
            Scope: new OtlpScope());

        OtlpLog badOpencodeVersion = new(
            LogId: "log-opencode-bad",
            TraceId: null,
            SpanId: null,
            SessionId: null,
            Timestamp: "2026-11-19T12:21:00.000Z",
            Body: "secret-opencode-payload",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["opencode.contract_version"] = JsonSerializer.SerializeToElement("999"),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            Scope: new OtlpScope());

        OtlpLogMapResult result = OtlpLogMapper.Map([validCopilotLog, badOpencodeVersion]);

        Assert.Equal(new IngestOutcome(1, 0, 1), result.Outcome);
        CollectedBatch batch = Assert.Single(result.Batches);
        Assert.Equal("copilot", batch.Source.HarnessId);
        Assert.Equal("log-copilot-1", Assert.Single(batch.Records).RecordId);

        CollectedProblem problem = Assert.Single(result.StandaloneProblems);
        Assert.Equal(OtlpLogMapper.UnknownOpencodeContractCode, problem.Code);
        Assert.DoesNotContain("secret-opencode-payload", problem.Message, StringComparison.Ordinal);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        store.WriteProblem(problem);
        Assert.Equal(1, store.CountRecords("copilot"));
        Assert.Equal(1, store.CountIngestProblems());
    }
}
