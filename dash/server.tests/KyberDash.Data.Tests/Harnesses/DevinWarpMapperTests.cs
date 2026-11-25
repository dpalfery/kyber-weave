using System.Globalization;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Devin;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Data.Tokenization;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="DevinMapper"/> and <see cref="WarpMapper"/> porting
/// <c>dash/src/providers/devin.ts</c> and <c>dash/src/providers/warp.ts</c> into
/// typed static DTOs and canonical relational <see cref="CollectedBatch"/> records.
/// </summary>
public class DevinWarpMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 4, 14, 10, 5, 0, TimeSpan.Zero);

    /// <summary>A JSON document whose token counts differ between cl100k_base and the o200k_base stand-in.</summary>
    private const string DivergentJsonText = "{\"summary\":\"こんにちは世界、今日はいい天気ですね。\"}";

    // ------------------------------------------------------------------
    // Devin: per-step usage, tools, and model resolution
    // ------------------------------------------------------------------

    [Fact]
    public void Map_ParsesPerStepAcusTokensToolsAndModelResolution()
    {
        string rawText = """
            {
              "schema_version": "1",
              "session_id": "session-123",
              "agent": { "model_name": "agent-model" },
              "steps": [
                {
                  "step_id": 1,
                  "message": "please inspect the repo",
                  "metadata": { "is_user_input": true, "created_at": "2027-01-15T08:00:00.000Z" }
                },
                {
                  "step_id": 2,
                  "model_name": "step-model",
                  "metadata": {
                    "created_at": "2027-01-15T08:00:01.000Z",
                    "committed_acu_cost": 0.02076149918138981,
                    "generation_model": "claude-opus-4-6",
                    "metrics": {
                      "input_tokens": 100,
                      "output_tokens": 20,
                      "cache_creation_tokens": 10,
                      "cache_read_tokens": 5
                    }
                  },
                  "tool_calls": [{ "tool_call_id": "tc1", "function_name": "read_file" }]
                },
                {
                  "step_id": 3,
                  "model_name": "claude-sonnet-4-6",
                  "metadata": {
                    "created_at": "2027-01-15T08:00:02.000Z",
                    "committed_acu_cost": 0.005421000067144632,
                    "metrics": { "input_tokens": 1 }
                  },
                  "tool_calls": [{ "tool_call_id": "tc2", "function_name": "str_replace" }]
                }
              ]
            }
            """;

        NativeSourceUnit unit = MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/glimmer-platinum.json", rawText);
        DevinMapper mapper = new(acuUsdRate: 1.0);
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal("devin", mapper.HarnessId);
        Assert.Empty(batch.Problems);
        Assert.Equal("devin", batch.Source.HarnessId);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("session-123", session.SessionKey);
        Assert.Equal("session-123", session.NativeSessionId);
        Assert.Equal("devin", session.HarnessId);

        List<CollectedRecord> parents = batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke).ToList();
        Assert.Equal(2, parents.Count);

        CollectedRecord first = parents[0];
        Assert.Equal("devin:Opus 4.6", first.Name);
        Assert.Equal("Opus 4.6", first.Model);
        Assert.Equal("synth:devin:session-123:2", first.RecordId);
        Assert.Equal("synth:devin:session-123", first.TraceId);
        Assert.Equal(
            DateTimeOffset.Parse("2027-01-15T08:00:01.000Z", CultureInfo.InvariantCulture),
            first.StartedAt);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 100);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 20);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.CacheWrite && t.Count == 10);
        Assert.Contains(first.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 5);
        Assert.Equal(115L, first.ReportedInputTokens);
        Assert.Equal(20L, first.ReportedOutputTokens);
        Assert.NotNull(first.ReportedCost);
        Assert.Equal(0.02076149918138981, first.ReportedCost.Amount, 15);
        Assert.Equal("USD", first.ReportedCost.Currency);
        Assert.Equal(ContentBucket.UserMessage, first.Parts[0].Bucket);
        Assert.Equal("please inspect the repo", first.Parts[0].PlainText);
        Assert.Equal("session-123", first.SessionKey);

        CollectedRecord second = parents[1];
        Assert.Equal("Sonnet 4.6", second.Model);
        Assert.Equal("synth:devin:session-123:3", second.RecordId);
        Assert.Equal(
            DateTimeOffset.Parse("2027-01-15T08:00:02.000Z", CultureInfo.InvariantCulture),
            second.StartedAt);
        Assert.Contains(second.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 1);

        List<CollectedRecord> tools = batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke).ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("read_file", tools[0].Tool!.ToolName);
        Assert.Equal("tc1", tools[0].Tool!.CallId);
        Assert.Equal(first.RecordId, tools[0].ParentRecordId);
        Assert.Equal("str_replace", tools[1].Tool!.ToolName);
        Assert.Equal(second.RecordId, tools[1].ParentRecordId);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(4, store.CountRecords("devin"));
    }

    [Theory]
    [InlineData("gpt-5-3-codex-xhigh", "GPT-5.3 Codex (xhigh)")]
    [InlineData("gpt-5-4-low", "GPT-5.4 (low)")]
    [InlineData("gpt-5-5-medium", "GPT-5.5 (medium)")]
    [InlineData("MODEL_PRIVATE_11", "Gemini 3 Flash", "Gemini 3 Flash")]
    [InlineData("MODEL_GOOGLE_GEMINI_3_0_FLASH_MINIMAL", "Gemini 3 Flash", "Gemini 3 Flash")]
    [InlineData("gpt-5", "GPT-5")]
    [InlineData("gpt-5-6-medium", "GPT-5.6 (medium)")]
    [InlineData("gpt-5-codex-xhigh", "GPT-5 Codex (xhigh)")]
    [InlineData("gpt-5-codex", "GPT-5 Codex")]
    [InlineData("gpt-4-1106-preview", "gpt-4-1106-preview")]
    public void GetDisplayModelName_RendersGenerationModelVariantsWithEffortTiers(
        string generationModel, string expected, string fallbackModel = "GPT-5")
    {
        Assert.Equal(expected, DevinMapper.GetDisplayModelName(generationModel, fallbackModel));
    }

    [Fact]
    public void Map_LeavesUnknownModelIdsUnchanged()
    {
        Assert.Equal("agent-model", DevinMapper.GetDisplayModelName(null, "agent-model"));
        Assert.Equal("devin", DevinMapper.GetDisplayModelName(null, "  "));
        Assert.Equal("step-model", DevinMapper.GetDisplayModelName(string.Empty, "step-model"));
    }

    [Fact]
    public void Map_CountsPartsUnderTheRecordModelTokenizer()
    {
        string rawText = """
            {
              "session_id": "session-tok",
              "steps": [
                {
                  "step_id": 1,
                  "message": "{\"summary\":\"こんにちは世界、今日はいい天気ですね。\"}",
                  "metadata": { "is_user_input": true, "created_at": "2027-01-15T08:00:00.000Z" }
                },
                {
                  "step_id": 2,
                  "metadata": {
                    "created_at": "2027-01-15T08:00:01.000Z",
                    "generation_model": "gpt-4-1106-preview",
                    "metrics": { "input_tokens": 10, "output_tokens": 2 }
                  }
                }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/session-tok.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("gpt-4-1106-preview", record.Model);
        CollectedContentPart userPart = Assert.Single(record.Parts);
        Assert.Null(userPart.PlainText);
        Assert.NotNull(userPart.Withheld);
        Assert.Equal(
            LocalTextTokenizer.Shared.Count(DivergentJsonText, record.Model).Count,
            userPart.Withheld!.MeasuredTokens);
    }

    [Fact]
    public void Map_CountsPartsUnderTheExchangeModelOwnTokenizer()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[]}}""",
            queries:
            [
                ("ex-1", "2026-05-18 10:00:00.000000", """[{"Query":{"text":"{\"summary\":\"こんにちは世界、今日はいい天気ですね。\"}"}}]""", "gpt-4", null, "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-tok");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-tok", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("gpt-4", record.Model);
        CollectedContentPart userPart = Assert.Single(record.Parts);
        Assert.Null(userPart.PlainText);
        Assert.NotNull(userPart.Withheld);
        Assert.Equal(
            LocalTextTokenizer.Shared.Count(DivergentJsonText, "gpt-4").Count,
            userPart.Withheld!.MeasuredTokens);
    }

    [Fact]
    public void Map_KeepsTokensButLeavesCostUnknownWhenRateIsMissing()
    {
        string rawText = """
            {
              "session_id": "session-123",
              "steps": [{
                "step_id": 2,
                "metadata": {
                  "created_at": "2027-01-15T08:00:01.000Z",
                  "committed_acu_cost": 0.5,
                  "metrics": { "input_tokens": 10, "output_tokens": 4 }
                }
              }]
            }
            """;

        CollectedBatch batch = new DevinMapper().Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/glimmer-platinum.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 10);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 4);
        Assert.Null(record.ReportedCost);
        Assert.Equal("synth:devin:session-123:2", record.RecordId);
    }

    [Fact]
    public void Map_ConvertsAcusToCostUsingConfiguredRate()
    {
        string rawText = """
            {
              "session_id": "configured-rate",
              "agent": { "model_name": "agent-model" },
              "steps": [
                { "step_id": "s1", "metadata": { "committed_acu_cost": 0.4 } }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 2.5).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/configured-rate.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.NotNull(record.ReportedCost);
        Assert.Equal(1.0, record.ReportedCost.Amount, 12);
    }

    [Fact]
    public void Map_IncludesTokenOnlyStepsAndSkipsUserInputOrEmptySteps()
    {
        string rawText = """
            {
              "session_id": "token-session",
              "agent": { "model_name": "agent-model" },
              "steps": [
                {
                  "step_id": "user-cost",
                  "metadata": { "is_user_input": true, "committed_acu_cost": 99, "metrics": { "input_tokens": 99 } }
                },
                { "step_id": "empty", "metadata": { "created_at": "2026-06-05T10:00:00.000Z" } },
                {
                  "step_id": "tokens",
                  "metadata": { "created_at": "2026-06-05T10:00:01.000Z", "metrics": { "output_tokens": 42 } }
                }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/token-only.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("agent-model", record.Model);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 42);
        Assert.Null(record.ReportedCost);
    }

    [Fact]
    public void Map_FallsBackToFilenameSessionId()
    {
        string rawText = """
            {
              "steps": [
                { "step_id": 1, "metadata": { "request_id": "req-1", "committed_acu_cost": 0.1 } },
                { "step_id": 2, "metadata": { "created_at": "2026-06-05T10:00:00.000Z", "committed_acu_cost": 0.2 } }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/fallback-session.json", rawText),
            BaselineUtc);

        List<CollectedRecord> parents = batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke).ToList();
        Assert.Equal(2, parents.Count);
        Assert.All(parents, static r => Assert.Equal("fallback-session", r.SessionKey));
        Assert.All(parents, static r => Assert.Equal("devin", r.Model));
        Assert.Equal("synth:devin:fallback-session:1", parents[0].RecordId);
        Assert.Equal("synth:devin:fallback-session:2", parents[1].RecordId);
    }

    [Fact]
    public void Map_ExtractsUserMessageFromContentParts()
    {
        string rawText = """
            {
              "session_id": "cp-session",
              "agent": { "model_name": "agent-model" },
              "steps": [
                {
                  "step_id": 1,
                  "message": [
                    { "type": "text", "text": "look at this screenshot" },
                    { "type": "image", "source": { "media_type": "image/png", "path": "/tmp/screenshot.png" } }
                  ],
                  "metadata": { "is_user_input": true, "created_at": "2027-01-15T08:00:00.000Z" }
                },
                {
                  "step_id": 2,
                  "metadata": {
                    "created_at": "2027-01-15T08:00:01.000Z",
                    "committed_acu_cost": 0.1,
                    "metrics": { "input_tokens": 50 }
                  }
                }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/content-parts.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("look at this screenshot /tmp/screenshot.png", record.Parts[0].PlainText);
    }

    [Fact]
    public void Map_NormalizesImageOnlyUserMessageToItsPath()
    {
        string rawText = """
            {
              "session_id": "image-only-session",
              "agent": { "model_name": "agent-model" },
              "steps": [
                {
                  "step_id": 1,
                  "message": [{ "type": "image", "source": { "media_type": "image/png", "path": "/tmp/only.png" } }],
                  "metadata": { "is_user_input": true, "created_at": "2027-01-15T08:00:00.000Z" }
                },
                {
                  "step_id": 2,
                  "metadata": {
                    "created_at": "2027-01-15T08:00:01.000Z",
                    "committed_acu_cost": 0.1,
                    "metrics": { "input_tokens": 40 }
                  }
                }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/image-only.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("/tmp/only.png", record.Parts[0].PlainText);
    }

    [Fact]
    public void Map_PrefersMetadataAcuOverExtraAndStepMetricsOverMetadataMetrics()
    {
        string rawText = """
            {
              "session_id": "priority-session",
              "agent": { "model_name": "agent-model" },
              "steps": [
                {
                  "step_id": 1,
                  "metrics": {
                    "prompt_tokens": 500,
                    "completion_tokens": 100,
                    "cached_tokens": 20,
                    "extra": { "cache_creation_input_tokens": 30 }
                  },
                  "extra": { "committed_acu_cost": 0.99 },
                  "metadata": {
                    "created_at": "2027-01-15T08:00:00.000Z",
                    "committed_acu_cost": 0.11,
                    "metrics": { "input_tokens": 1, "output_tokens": 1, "cache_creation_tokens": 1, "cache_read_tokens": 1 }
                  }
                }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/priority.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 500);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 100);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite && t.Count == 30);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 20);
        Assert.NotNull(record.ReportedCost);
        Assert.Equal(0.11, record.ReportedCost.Amount, 12);
    }

    [Fact]
    public void Map_FallsBackToMetadataMetricsWhenStepMetricsAreEmpty()
    {
        string rawText = """
            {
              "session_id": "empty-metrics-session",
              "agent": { "model_name": "agent-model" },
              "steps": [
                {
                  "step_id": 1,
                  "metrics": {},
                  "metadata": {
                    "created_at": "2027-01-15T08:00:00.000Z",
                    "committed_acu_cost": 0.1,
                    "metrics": { "input_tokens": 80, "output_tokens": 20, "cache_read_tokens": 5 }
                  }
                }
              ]
            }
            """;

        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/empty-step-metrics.json", rawText),
            BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 80);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 20);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 5);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public void Map_EmitsProblemForMalformedDevinTranscripts(string rawText)
    {
        CollectedBatch batch = new DevinMapper(acuUsdRate: 1.0).Map(
            MakeNativeUnit("devin", "/home/u/.local/share/devin/cli/transcripts/bad.json", rawText),
            BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(DevinMapper.MalformedDocumentCode, problem.Code);
    }

    // ------------------------------------------------------------------
    // Warp: per-exchange calls, token budgets, and command attribution
    // ------------------------------------------------------------------

    [Fact]
    public void Map_CountsEveryModelsFullReportedTotalAsSessionUsageAndLeavesExchangesNotMeasured()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[{"model_id":"GPT-5.3 Codex (medium reasoning)","warp_tokens":300,"byok_tokens":0,"warp_token_usage_by_category":{"primary_agent":300},"byok_token_usage_by_category":{}},{"model_id":"Claude Haiku 4.5","warp_tokens":90,"byok_tokens":0,"warp_token_usage_by_category":{"full_terminal_use":90},"byok_token_usage_by_category":{}}]}}""",
            queries:
            [
                ("ex-1", "2026-05-18 10:00:00.000000", """[{"Query":{"text":"short prompt"}}]""", "auto-efficient", "/Users/test/project-a", "\"Completed\""),
                ("ex-2", "2026-05-18 10:03:00.000000", """[{"Query":{"text":"longer prompt with substantially more detail for weighting"}}]""", "auto-efficient", "/Users/test/project-a", "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-1");

        NativeSourceUnit unit = MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-1", rawText);
        CollectedBatch batch = new WarpMapper().Map(unit, BaselineUtc);

        Assert.Equal("warp", new WarpMapper().HarnessId);
        Assert.Empty(batch.Problems);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("conv-1", session.SessionKey);
        Assert.Equal("conv-1", session.NativeSessionId);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/Users/test/project-a", session.Workspace.WorkingDirectory);

        Assert.Equal(2, batch.Records.Count);
        Assert.Equal(
            ["synth:warp:conv-1:ex-1", "synth:warp:conv-1:ex-2"],
            batch.Records.Select(static r => r.RecordId).ToArray());
        Assert.Equal(
            ["short prompt", "longer prompt with substantially more detail for weighting"],
            batch.Records.Select(static r => r.Parts[0].PlainText).ToArray());
        Assert.All(batch.Records, static r => Assert.Equal("gpt-5.3-codex", r.Model));
        Assert.All(batch.Records, static r => Assert.Null(r.ReportedCost));
        Assert.All(batch.Records, static r => Assert.Empty(r.Tokens));
        Assert.All(batch.Records, static r => Assert.Null(r.ReportedInputTokens));
        Assert.All(batch.Records, static r => Assert.Null(r.ReportedOutputTokens));
        Assert.Equal(
            [
                new CollectedModelUsage(0, "gpt-5.3-codex", TokenClass.Total, 300L),
                new CollectedModelUsage(0, "claude-haiku-4-5", TokenClass.Total, 90L),
            ],
            session.ReportedUsage);
        Assert.All(batch.Records, static r => Assert.Equal("conv-1", r.SessionKey));

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(2, store.CountRecords("warp"));
        Assert.Empty(store.TokenCounts(sessionKey: "conv-1"));
        Assert.Equal(390L, store.SessionModelUsages("conv-1").Sum(static u => u.Usage.Count));
    }

    [Fact]
    public void Map_SumsPerModelConversationTotalsWhenNoCategoryIsReported()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[{"model_id":"GPT-5.3 Codex (medium reasoning)","warp_tokens":70,"byok_tokens":5,"warp_token_usage_by_category":{},"byok_token_usage_by_category":{}},{"model_id":"Claude Sonnet 4.5","warp_tokens":40,"byok_tokens":0},{"model_id":"GPT-5.3 Codex (medium reasoning)","warp_tokens":25,"byok_tokens":0}]}}""",
            queries:
            [
                ("ex-1", "2026-05-18 10:00:00.000000", """[{"Query":{"text":"a much longer prompt that a text-length guess would weight heavily"}}]""", "auto-efficient", null, "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-5");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-5", rawText),
            BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal(2, session.ReportedUsage.Count);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5.3-codex", TokenClass.Total, 100L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Total, 40L), session.ReportedUsage);
        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedInputTokens);
    }

    [Fact]
    public void Map_EntryWithNoModelAndTheLargestTotal_GoesToTheLargestNamedModel()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[{"warp_tokens":300,"byok_tokens":0},{"model_id":"Claude Haiku 4.5","warp_tokens":90,"byok_tokens":0}]}}""",
            queries:
            [
                ("ex-1", "2026-05-18 10:00:00.000000", """[{"Query":{"text":"prompt"}}]""", "auto-efficient", null, "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-6");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-6", rawText),
            BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal([new CollectedModelUsage(0, "claude-haiku-4-5", TokenClass.Total, 390L)], session.ReportedUsage);
        Assert.Equal("claude-haiku-4-5", Assert.Single(batch.Records).Model);
    }

    [Fact]
    public void Map_TokenTotalPastLongMax_RecordsAProblemAndReportsNoUsage()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[{"model_id":"Claude Haiku 4.5","warp_tokens":9223372036854775807,"byok_tokens":1}]}}""",
            queries:
            [
                ("ex-1", "2026-05-18 10:00:00.000000", """[{"Query":{"text":"prompt"}}]""", "auto-efficient", null, "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-7");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-7", rawText),
            BaselineUtc);

        Assert.Empty(Assert.Single(batch.Sessions).ReportedUsage);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(WarpMapper.MalformedDocumentCode, problem.Code);
        Assert.Contains("token", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_AttributesCommandBlocksToNearestPrecedingExchange()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[{"model_id":"GPT-5.3 Codex (medium reasoning)","warp_tokens":120,"byok_tokens":0,"warp_token_usage_by_category":{"primary_agent":120},"byok_token_usage_by_category":{}}]}}""",
            queries:
            [
                ("ex-a", "2026-05-18 11:00:00.000000", """[{"Query":{"text":"run tests"}}]""", "auto-efficient", null, "\"Completed\""),
                ("ex-b", "2026-05-18 11:05:00.000000", """[{"Query":{"text":"summarize results"}}]""", "auto-efficient", null, "\"Completed\""),
            ],
            blocks: [("block-1", "2026-05-18 11:01:00.000000", "npm test && git status")],
            conversationId: "conv-2");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-2", rawText),
            BaselineUtc);

        List<CollectedRecord> parents = batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke).ToList();
        Assert.Equal(2, parents.Count);

        List<CollectedRecord> tools = batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke).ToList();
        CollectedRecord bash = Assert.Single(tools);
        Assert.Equal("Bash", bash.Tool!.ToolName);
        Assert.Equal("block-1", bash.Tool!.CallId);
        Assert.Equal(parents[0].RecordId, bash.ParentRecordId);
        Assert.Contains("npm test", bash.Tool!.Input!.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_SkipsPendingOrInvalidExchanges()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[{"model_id":"GPT-5.3 Codex (medium reasoning)","warp_tokens":42,"byok_tokens":0,"warp_token_usage_by_category":{"primary_agent":42},"byok_token_usage_by_category":{}}]}}""",
            queries:
            [
                ("ex-pending", "2026-05-18 12:01:00.000000", """[{"Query":{"text":"still running"}}]""", "auto-efficient", null, "\"Pending\""),
                ("ex-invalid-ts", "not-a-timestamp", """[{"Query":{"text":"bad timestamp"}}]""", "auto-efficient", null, "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-3");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-3", rawText),
            BaselineUtc);

        Assert.Empty(batch.Records);
    }

    [Fact]
    public void Map_ResolvesExchangeModelThroughAliasesWithoutDominantModel()
    {
        string rawText = BuildWarpExport(
            conversationData: """{"conversation_usage_metadata":{"token_usage":[]}}""",
            queries:
            [
                ("ex-1", "2026-05-18 10:00:00.000000", """[{"Query":{"text":"hello"}}]""", "Claude Sonnet 4.5", "/Users/test/project-a", "\"Completed\""),
                ("ex-2", "2026-05-18 10:01:00.000000", """[{"Query":{"text":"world"}}]""", "auto-efficient", "/Users/test/project-a", "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-4");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-4", rawText),
            BaselineUtc);

        Assert.Equal(2, batch.Records.Count);
        Assert.Equal("claude-sonnet-4-5", batch.Records[0].Model);
        Assert.Equal("warp-auto-efficient", batch.Records[1].Model);
        Assert.Empty(Assert.Single(batch.Sessions).ReportedUsage);
        Assert.All(batch.Records, static r => Assert.Empty(r.Tokens));
        Assert.All(batch.Records, static r => Assert.Null(r.ReportedInputTokens));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public void Map_EmitsProblemForMalformedWarpExports(string rawText)
    {
        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-9", rawText),
            BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(WarpMapper.MalformedDocumentCode, problem.Code);
    }

    [Fact]
    public void Map_NullWarpPayload_EmitsOneWarningProblemNamingTheSourceKey()
    {
        const string sourceKey = "/home/u/Library/warp.sqlite#conv-7";
        NativeSourceUnit unit = new(
            new SourceUnit("warp", sourceKey, "warp", "warp-reader", "1", "warp-test", "warp", "1-1", BaselineUtc, 0),
            RawText: null,
            SizeBytes: 0,
            LastWriteUtc: BaselineUtc);

        CollectedBatch batch = new WarpMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal(WarpMapper.MalformedDocumentCode, problem.Code);
        Assert.Contains(sourceKey, problem.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_SourceKeyIdNotInExport_RecordsAProblemInsteadOfDroppingTheSession()
    {
        string rawText = BuildWarpExport(
            conversationData: "{}",
            queries:
            [
                ("ex-1", "2026-05-18 10:00:00.000000", """[{"Query":{"text":"hi"}}]""", "auto-efficient", "/Users/test/project-a", "\"Completed\""),
            ],
            blocks: [],
            conversationId: "conv-1");

        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-other", rawText),
            BaselineUtc);

        Assert.Empty(batch.Records);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal(WarpMapper.MalformedDocumentCode, problem.Code);
        Assert.Contains("conv-other", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\n\t")]
    public void Map_WhitespaceWarpPayload_ReturnsEmptyBatchWithoutProblems(string rawText)
    {
        CollectedBatch batch = new WarpMapper().Map(
            MakeNativeUnit("warp", "/home/u/Library/warp.sqlite#conv-8", rawText),
            BaselineUtc);

        Assert.Empty(batch.Records);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Problems);
    }

    [Theory]
    [InlineData("Claude Sonnet 4.6", "claude-sonnet-4-6")]
    [InlineData("Claude Haiku 4.5", "claude-haiku-4-5")]
    [InlineData("GPT-5.3 Codex (low reasoning)", "gpt-5.3-codex")]
    [InlineData("auto-efficient", "warp-auto-efficient")]
    [InlineData("auto-powerful", "warp-auto-powerful")]
    [InlineData("gpt-5.3-codex", "gpt-5.3-codex")]
    public void NormalizeModel_AppliesWarpAliases(string rawModel, string expected)
    {
        Assert.Equal(expected, WarpMapper.NormalizeModel(rawModel));
    }

    [Theory]
    [InlineData("/Users/test/project-a", "Users-test-project-a")]
    [InlineData("/", "")]
    public void SanitizeProject_StripsLeadingSlashes(string projectPath, string expected)
    {
        Assert.Equal(expected, WarpMapper.SanitizeProject(projectPath));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static NativeSourceUnit MakeNativeUnit(string harnessId, string path, string rawText)
    {
        SourceUnit sourceUnit = new(
            HarnessId: harnessId,
            SourceKey: path,
            ProviderId: harnessId,
            ParserId: $"{harnessId}-reader",
            ParserContractVersion: "1",
            Format: $"{harnessId}-test",
            SourceRootLabel: harnessId,
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(
            Unit: sourceUnit,
            RawText: rawText,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: BaselineUtc);
    }

    private static string BuildWarpExport(
        string conversationData,
        (string ExchangeId, string StartTs, string Input, string? ModelId, string? WorkingDirectory, string OutputStatus)[] queries,
        (string BlockId, string StartTs, string Command)[] blocks,
        string conversationId = "conv-1")
    {
        StringBuilder sb = new();
        sb.Append("{\"conversation\":{\"conversation_id\":");
        AppendJsonString(sb, conversationId);
        sb.Append(",\"conversation_data\":");
        AppendJsonString(sb, conversationData);
        sb.Append(",\"last_modified_at\":\"2026-05-18 10:10:00\"},\"queries\":[");
        for (int i = 0; i < queries.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            (string exchangeId, string startTs, string input, string? modelId, string? workingDirectory, string outputStatus) = queries[i];
            sb.Append("{\"exchange_id\":");
            AppendJsonString(sb, exchangeId);
            sb.Append(",\"conversation_id\":");
            AppendJsonString(sb, conversationId);
            sb.Append(",\"start_ts\":");
            AppendJsonString(sb, startTs);
            sb.Append(",\"input\":");
            AppendJsonString(sb, input);
            sb.Append(",\"working_directory\":");
            if (workingDirectory is null)
            {
                sb.Append("null");
            }
            else
            {
                AppendJsonString(sb, workingDirectory);
            }

            sb.Append(",\"output_status\":");
            AppendJsonString(sb, outputStatus);
            sb.Append(",\"model_id\":");
            AppendJsonString(sb, modelId ?? string.Empty);
            sb.Append(",\"planning_model_id\":\"\",\"coding_model_id\":\"\"}");
        }

        sb.Append("],\"blocks\":[");
        for (int i = 0; i < blocks.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"block_id\":");
            AppendJsonString(sb, blocks[i].BlockId);
            sb.Append(",\"start_ts\":");
            AppendJsonString(sb, blocks[i].StartTs);
            sb.Append(",\"stylized_command\":");
            AppendJsonString(sb, blocks[i].Command);
            sb.Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
