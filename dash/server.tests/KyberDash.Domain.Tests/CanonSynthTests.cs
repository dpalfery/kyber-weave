using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Domain.Tests;

/// <summary>
/// Fallback transcript synthesis, child tool-call bounding, and cross-path deduplication ported from
/// the C# engine's synth parity suite.
/// </summary>
public class CanonSynthTests
{
    [Fact]
    public void SynthesizeCall_AppliesProviderConventionsReasoningFoldAndCopilotAbsentOutput()
    {
        Assert.True(CanonSynth.ProviderConventions.Count >= 44);
        Assert.Equal("exclusive", CanonSynth.ConventionFor("claude"));
        Assert.Equal("exclusive", CanonSynth.ConventionFor("unseen-future-provider"));

        ParsedProviderCall claudeCall = new()
        {
            Timestamp = "2026-11-14T10:00:00.000Z",
            Provider = "claude",
            Model = "claude-sonnet-4-20250514",
            InputTokens = 120,
            OutputTokens = 80,
            CacheReadInputTokens = 500,
            CacheCreationInputTokens = 40,
            ReasoningTokens = 30,
            CostUsd = 0.045,
            CostIsCalculated = false,
            SessionId = "sess-1",
            TurnId = "msg-1",
            SessionTitle = "Fix build pipeline",
            Skills = ["docs", "docs", "lint"],
        };

        CanonicalRecord claudeRecord = CanonSynth.SynthesizeCall(claudeCall);
        Assert.Equal("synth:claude:sess-1:msg-1", claudeRecord.SpanId);
        Assert.Equal("synth:claude:sess-1", claudeRecord.TraceId);
        Assert.Equal("codeburn/claude", claudeRecord.Source);
        Assert.Equal("llm.invoke", claudeRecord.Op);
        Assert.Equal(80, claudeRecord.Tokens.Output);
        Assert.Equal(30, claudeRecord.Tokens.Reasoning);
        Assert.Equal(660, claudeRecord.Tokens.ReportedInput);
        Assert.Equal(CostBasis.Harness, claudeRecord.Cost.Basis);
        Assert.Equal(CostStatus.Priced, claudeRecord.Cost.Status);
        Assert.NotNull(claudeRecord.Raw);
        Assert.Equal("Fix build pipeline", claudeRecord.Raw![CanonSynth.SessionLabelKey].GetString());
        Assert.Equal(2, claudeRecord.Raw[CanonSynth.SkillsInvokedKey].GetArrayLength());

        ParsedProviderCall piCall = new()
        {
            Timestamp = "2026-11-14T10:01:00.000Z",
            Provider = "pi",
            Model = "gpt-5",
            InputTokens = 100,
            OutputTokens = 60,
            ReasoningTokens = 40,
            CostUsd = 0,
            CostIsCalculated = true,
            SessionId = "sess-pi",
            DeduplicationKey = "pi:sess-pi:call-9",
        };

        CanonicalRecord piRecord = CanonSynth.SynthesizeCall(piCall);
        Assert.Equal(100, piRecord.Tokens.Output);
        Assert.Equal(100, piRecord.Tokens.ReportedOutput);
        Assert.Equal(40, piRecord.Tokens.Reasoning);
        Assert.Equal(CostBasis.Published, piRecord.Cost.Basis);
        Assert.Equal(CostStatus.NoRate, piRecord.Cost.Status);

        ParsedProviderCall cursorWithTurnId = new()
        {
            Timestamp = "2026-11-14T10:02:00.000Z",
            Provider = "cursor",
            Model = "claude-4-sonnet",
            InputTokens = 50,
            OutputTokens = 25,
            SessionId = "cursor-s1",
            TurnId = "pairing-only-id",
            DeduplicationKey = "cursor:cursor-s1",
        };
        ParsedProviderCall cursorWithoutTurnId = cursorWithTurnId with { TurnId = null };
        (string cursorIdWith, string? digestWith) = CanonSynth.NativeRecordIdentity(cursorWithTurnId, ordinal: 0);
        (string cursorIdWithout, string? digestWithout) = CanonSynth.NativeRecordIdentity(cursorWithoutTurnId, ordinal: 0);
        Assert.Equal(cursorIdWithout, cursorIdWith);
        Assert.NotNull(digestWith);
        Assert.Equal(digestWithout, digestWith);

        ParsedProviderCall copilotShutdown = new()
        {
            Timestamp = "2026-11-14T10:03:00.000Z",
            Provider = "copilot",
            Model = "gpt-4.1",
            InputTokens = 300,
            OutputTokens = 0,
            CostUsd = 0.02,
            SessionId = "cop-1",
            DeduplicationKey = "copilot:cop-1:shutdown:gpt-4.1:0",
        };

        CanonicalRecord copilotRecord = CanonSynth.SynthesizeCall(copilotShutdown);
        Assert.Equal(CostBasis.Published, copilotRecord.Cost.Basis);
        Assert.NotNull(copilotRecord.Measurability);
        Assert.True(copilotRecord.Measurability!.TryGetValue("output", out JsonElement outputMeasurability));
        Assert.Equal("not_measurable", outputMeasurability.GetProperty("availability").GetString());
    }

    [Fact]
    public void SynthesizeToolCalls_Bounds64KiBPayloadsAndExtractsStructuredErrors()
    {
        ParsedProviderCall parentCall = new()
        {
            Timestamp = "2026-11-14T11:00:00.000Z",
            Provider = "claude",
            Model = "claude-sonnet-4-20250514",
            InputTokens = 200,
            OutputTokens = 50,
            SessionId = "sess-tools",
            TurnId = "turn-tools-1",
        };

        ReaderTurn turn = new()
        {
            Parts = [new ContentPart("conversation_history", "Run tests", Order: 0)],
            ToolCalls =
            [
                new ReaderToolCall
                {
                    Id = "tc-big",
                    Name = "write_file",
                    Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        ["path"] = "src/Program.cs",
                        ["content"] = new string('x', 80_000),
                    }),
                    Timestamp = "2026-11-14T11:00:01.000Z",
                },
            ],
            ToolResults =
            [
                new ReaderToolResult
                {
                    ToolCallId = "tc-big",
                    Content = new string('é', 40_000),
                    IsError = true,
                    Timestamp = "2026-11-14T11:00:02.250Z",
                },
            ],
        };

        CanonicalRecord parentRecord = CanonSynth.SynthesizeCall(parentCall, readerTurn: turn);
        IReadOnlyList<CanonicalRecord> childRecords = CanonSynth.SynthesizeToolCalls(parentRecord, parentCall, turn);

        CanonicalRecord toolRecord = Assert.Single(childRecords);
        Assert.StartsWith($"{parentRecord.SpanId}-t", toolRecord.SpanId, StringComparison.Ordinal);
        Assert.Equal(parentRecord.SpanId, toolRecord.ParentSpanId);
        Assert.Equal("tool.invoke", toolRecord.Op);
        Assert.Equal("error", toolRecord.Status);
        Assert.Equal(1250, toolRecord.DurationMs);

        Assert.NotNull(toolRecord.Parts);
        ContentPart resultPart = Assert.Single(toolRecord.Parts!);
        Assert.True(Encoding.UTF8.GetByteCount(resultPart.Text) <= CanonSynth.MaxToolResultBytes);
        Assert.True(resultPart.Truncated);

        Assert.NotNull(toolRecord.Raw);
        Assert.True(toolRecord.Raw!["gen_ai.tool.arguments_truncated"].GetBoolean());
        Assert.False(string.IsNullOrEmpty(toolRecord.Raw["gen_ai.tool.arguments_hash"].GetString()));
        JsonElement boundedArguments = toolRecord.Raw["arguments"];
        Assert.Equal(JsonValueKind.Object, boundedArguments.ValueKind);
        Assert.Equal("src/Program.cs", boundedArguments.GetProperty("path").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(boundedArguments.GetRawText()) <= CanonSynth.MaxToolArgumentsBytes);
    }

    [Fact]
    public void Deduplicate_CollapsesOtlpAndFileTurnsAndRecordsDisagreement()
    {
        List<SpanProblemRow> problems = [];

        ParsedProviderCall fileCall = new()
        {
            Timestamp = "2026-11-14T12:00:00.000Z",
            Provider = "claude-code",
            Model = "claude-sonnet-4-20250514",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01,
            SessionId = "s-shared",
            TurnId = "msg-42",
        };
        ReaderTurn fileTurn = new()
        {
            Parts = [new ContentPart("conversation_history", "File transcript body", Order: 0)],
        };
        CanonicalRecord synthRecord = CanonSynth.SynthesizeCall(fileCall, readerTurn: fileTurn);

        CanonicalRecord otlpRecord = new()
        {
            SpanId = "abcdef0123456789",
            TraceId = "0102030405060708090a0b0c0d0e0f10",
            Source = "claude-code",
            Harness = "claude-code",
            SessionId = "s-shared",
            Name = "claude-sonnet-4-20250514",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = "2026-11-14T12:00:00.000Z",
            DurationMs = 500,
            Status = "ok",
            Tokens = new TokenUsage(220, 0, 0, 50, 220, 50),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Parts = null,
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.02, "USD"),
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["session.id"] = JsonSerializer.SerializeToElement("s-shared"),
                ["gen_ai.response.id"] = JsonSerializer.SerializeToElement("msg-42"),
            },
        };

        CanonicalRecord geminiRecord = otlpRecord with { SpanId = "synth:gemini:g1:m1", Harness = "gemini" };

        Assert.Equal("synth:claude-code:s-shared", CanonSynthDedup.DeduplicationKeyFor(synthRecord));
        Assert.Equal("synth:claude-code:s-shared", CanonSynthDedup.DeduplicationKeyFor(otlpRecord));

        IReadOnlyList<CanonicalRecord> result = CanonSynthDedup.Deduplicate(
            [synthRecord, geminiRecord],
            [otlpRecord],
            problem => problems.Add(problem));

        CanonicalRecord survivor = Assert.Single(result);
        Assert.Equal("abcdef0123456789", survivor.SpanId);
        Assert.Equal(220, survivor.Tokens.FreshInput);
        Assert.NotNull(survivor.Parts);
        Assert.Equal("File transcript body", survivor.Parts![0].Text);
        Assert.Equal("File transcript body", survivor.Content["conversation_history"]);

        SpanProblemRow problem = Assert.Single(problems);
        Assert.Equal(CanonSynthDedup.DedupDisagreementCode, problem.Code);
        Assert.Equal("warning", problem.Severity);
        Assert.Contains("fresh input (100 vs 220)", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryHookMapperEscapesIdsRedactsPromptsAndCapsMultibyteText()
    {
        string traceA = FactoryHookMapper.TraceIdFor("a|b\\c", "m1");
        string traceB = FactoryHookMapper.TraceIdFor("a", "b\\c|m1");
        Assert.Equal(32, traceA.Length);
        Assert.NotEqual(traceA, traceB);

        string promptSpan = FactoryHookMapper.SpanIdFor(FactorySpanKind.Turn, "sess-1", "msg-1", FactoryTurnRole.Prompt);
        string completionSpan = FactoryHookMapper.SpanIdFor(FactorySpanKind.Turn, "sess-1", "msg-1", FactoryTurnRole.Completion);
        Assert.Equal(16, promptSpan.Length);
        Assert.Equal(16, completionSpan.Length);
        Assert.NotEqual(promptSpan, completionSpan);

        string longPrompt = new('\u20ac', (FactoryHookMapper.MaxPromptBytes / 3) + 50);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "sess-factory-1",
            message_id = "msg-1",
            model = "claude-sonnet-4-5",
            prompt = longPrompt,
        }));

        FactoryWireSpan redactedSpan = Assert.Single(FactoryHookMapper.MapHookEvent(
            document.RootElement,
            new FactoryMapOptions(ContentAllowed: false)));
        Assert.Equal("factory.turn", redactedSpan.Name);
        Assert.DoesNotContain(redactedSpan.Attributes, static attribute => attribute.Key == FactoryKeys.PromptText);
        Assert.Contains(redactedSpan.Attributes, static attribute => attribute.Key == FactoryKeys.ModelId && attribute.Value.StringValue == "claude-sonnet-4-5");

        FactoryWireSpan consentedSpan = Assert.Single(FactoryHookMapper.MapHookEvent(
            document.RootElement,
            new FactoryMapOptions(ContentAllowed: true)));
        FactoryWireEntry promptAttribute = Assert.Single(consentedSpan.Attributes, static attribute => attribute.Key == FactoryKeys.PromptText);
        string captured = promptAttribute.Value.StringValue!;
        Assert.EndsWith("[kyberdash: truncated]", captured, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(captured) <= FactoryHookMapper.MaxPromptBytes);
        Assert.DoesNotContain('\uFFFD', captured);
    }

    [Fact]
    public void FactoryHookMapperMapsSessionToolAndNoOpEventsUnderContractV1()
    {
        using JsonDocument sessionDocument = JsonDocument.Parse("""
        {
          "hook_event_name": "SessionStart",
          "session_id": "sess-1",
          "cwd": "/tmp/project"
        }
        """);
        FactoryWireSpan sessionSpan = Assert.Single(FactoryHookMapper.MapHookEvent(sessionDocument.RootElement));
        Assert.Equal("factory.session", sessionSpan.Name);
        Assert.Contains(sessionSpan.Attributes, static attribute => attribute.Key == FactoryKeys.ContractVersion && attribute.Value.StringValue == "1");
        Assert.Contains(sessionSpan.Attributes, static attribute => attribute.Key == FactoryKeys.Cwd && attribute.Value.StringValue == "/tmp/project");

        using JsonDocument postToolDocument = JsonDocument.Parse("""
        {
          "hook_event_name": "PostToolUse",
          "session_id": "sess-1",
          "tool_name": "read_file",
          "tool_call_id": "call-9",
          "tool_input": { "path": "README.md" },
          "tool_status": "error",
          "tool_error_type": "NotFound",
          "tool_response": { "ok": false }
        }
        """);
        FactoryWireSpan toolSpan = Assert.Single(FactoryHookMapper.MapHookEvent(postToolDocument.RootElement));
        Assert.Equal("factory.tool", toolSpan.Name);
        Assert.Contains(toolSpan.Attributes, static attribute => attribute.Key == FactoryKeys.ToolStatus && attribute.Value.StringValue == "error");
        Assert.Contains(toolSpan.Attributes, static attribute => attribute.Key == FactoryKeys.ToolErrorType && attribute.Value.StringValue == "NotFound");
        Assert.Contains(toolSpan.Attributes, static attribute =>
            attribute.Key == FactoryKeys.ToolInputBytes
            && int.Parse(attribute.Value.IntValue!, CultureInfo.InvariantCulture) > 0);
        Assert.Contains(toolSpan.Attributes, static attribute =>
            attribute.Key == FactoryKeys.ToolResultBytes
            && int.Parse(attribute.Value.IntValue!, CultureInfo.InvariantCulture) > 0);

        using JsonDocument noopDocument = JsonDocument.Parse("""{"hook_event_name":"SubagentStop","session_id":"sess-1"}""");
        Assert.Empty(FactoryHookMapper.MapHookEvent(noopDocument.RootElement));
    }

    [Fact]
    public void CheckpointCoverageAndRetentionFloorFollowTheRefreshContract()
    {
        SourceCheckpoint checkpoint = new(
            HarnessId: "claude-code",
            SourceKey: "/tmp/a.jsonl",
            ProviderId: "claude",
            ParserId: "reader",
            ParserContractVersion: "1",
            Format: "claude-jsonl",
            SourceRootLabel: "root",
            RevisionToken: "rev-1",
            CoveredFromUtc: "2026-11-01T00:00:00.000Z",
            CoveredThroughUtc: "2026-11-10T00:00:00.000Z",
            LastAttemptUtc: "2026-11-10T00:00:00.000Z",
            LastSuccessUtc: "2026-11-10T00:00:00.000Z",
            LastStatus: "success",
            LastErrorCode: null,
            UnitCount: 1,
            RecordCount: 2);

        Assert.True(CanonRefreshEngine.CheckpointIsReusable(checkpoint, new CoverageRequest("rev-1", "1")));
        Assert.False(CanonRefreshEngine.CheckpointIsReusable(checkpoint, new CoverageRequest("rev-2", "1")));
        Assert.False(CanonRefreshEngine.CheckpointIsReusable(null, new CoverageRequest("rev-1", "1")));
        Assert.Empty(CanonRefreshEngine.UncoveredIntervals(
            checkpoint,
            new CoverageInterval("2026-11-01T00:00:00.000Z", "2026-11-10T00:00:00.000Z"),
            new CoverageRequest("rev-1", "1")));

        CoverageInterval gap = Assert.Single(CanonRefreshEngine.UncoveredIntervals(
            checkpoint,
            new CoverageInterval("2026-10-20T00:00:00.000Z", "2026-11-15T00:00:00.000Z"),
            new CoverageRequest("rev-2", "1")));
        Assert.Equal("2026-10-20T00:00:00.000Z", gap.FromUtc);

        DateTimeOffset reference = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(reference.AddDays(-CanonRefreshEngine.ContentRetentionDays), CanonRefreshEngine.ContentRetentionFloor(reference));
        Assert.Equal(14, CanonRefreshEngine.ContentRetentionDays);
    }

    [Fact]
    public void HistoryNormalizationRejectsOutOfRangeValues()
    {
        Assert.Equal(2, CanonRefreshEngine.NormalizeHistoryWeeks(2));
        Assert.Null(CanonRefreshEngine.NormalizeHistoryWeeks(0));
        Assert.Null(CanonRefreshEngine.NormalizeHistoryWeeks(-3));
        Assert.Null(CanonRefreshEngine.NormalizeHistoryWeeks(null));
        Assert.Null(CanonRefreshEngine.NormalizeHistoryWeeks(long.MaxValue));
    }
}
