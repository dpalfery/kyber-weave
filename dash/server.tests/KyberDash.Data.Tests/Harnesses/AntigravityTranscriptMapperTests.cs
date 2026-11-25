using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Antigravity;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="AntigravityTranscriptMapper"/> covering Antigravity transcript parsing,
/// <c>PLANNER_RESPONSE.tool_calls</c> to <c>tool.invoke</c> emission, <c>SKILL.md</c> skill inference,
/// <c>agy-step:</c> segmentation, and <c>antigravity-cli</c> <c>cache/antigravity-statusline.jsonl</c> mapping.
/// </summary>
public class AntigravityTranscriptMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A JSON document whose token counts differ between cl100k_base and the o200k_base stand-in.</summary>
    private const string DivergentJsonText = "{\"summary\":\"こんにちは世界、今日はいい天気ですね。\"}";

    [Fact]
    public void Map_ThrowsArgumentNullException_WhenUnitIsNull()
    {
        AntigravityTranscriptMapper mapper = new();

        Assert.Throws<ArgumentNullException>(() => mapper.Map(null!, ImportedAtUtc));
    }

    [Theory]
    [InlineData("", "antigravity-jsonl")]
    [InlineData("   \r\n\t ", "antigravity-jsonl")]
    public void Map_ReturnsEmptyBatch_WhenRawTextIsWhitespace(string rawText, string format)
    {
        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(
            harnessId: "antigravity",
            sourceKey: "/home/dev/.gemini/antigravity/brain/conv-1/.system_generated/logs/transcript.jsonl",
            format: format,
            rawText: rawText);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Equal("antigravity", mapper.HarnessId);
        Assert.Equal("antigravity", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_TranscriptFixture_EmitsLlmInvokeAndToolInvokeWithDecodedArgsAndGenericOutput()
    {
        string jsonl = string.Join('\n',
        [
            JsonSerializer.Serialize(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                status = "DONE",
                created_at = "2026-06-21T13:32:10Z",
                content = "Please inspect the repository status",
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-06-21T13:32:11Z",
                content = "Checking status now.",
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 3,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-06-21T13:32:21Z",
                tool_calls = new[]
                {
                    new
                    {
                        name = "run_command",
                        args = new Dictionary<string, string>
                        {
                            ["CommandLine"] = EncodeTwice("synthetic-command --dry-run"),
                            ["Cwd"] = EncodeTwice("/synthetic/workspace"),
                        },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 4,
                source = "SYSTEM",
                type = "GENERIC",
                status = "DONE",
                created_at = "2026-06-21T13:32:22Z",
                content = "synthetic command output",
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 6,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-06-21T13:33:05Z",
                tool_calls = new[]
                {
                    new
                    {
                        name = "view_file",
                        args = new Dictionary<string, string>
                        {
                            ["AbsolutePath"] = EncodeTwice("/synthetic/workspace/src/sample.ts"),
                            ["StartLine"] = EncodeTwice(12),
                        },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 7,
                source = "SYSTEM",
                type = "GENERIC",
                status = "DONE",
                created_at = "2026-06-21T13:33:06Z",
                content = "synthetic file contents",
            }),
        ]) + "\n";

        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(
            harnessId: "antigravity",
            sourceKey: "/home/dev/.gemini/antigravity/brain/fixture-segments/.system_generated/logs/transcript.jsonl",
            format: "antigravity-jsonl",
            rawText: jsonl);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("fixture-segments", session.SessionKey);
        Assert.Equal("antigravity", session.HarnessId);
        Assert.Equal("fixture-segments", session.NativeSessionId);

        List<CollectedRecord> llmRecords = batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke).ToList();
        List<CollectedRecord> toolRecords = batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke).ToList();

        Assert.Equal(3, llmRecords.Count);
        Assert.Equal(2, toolRecords.Count);

        CollectedRecord firstLlm = llmRecords[0];
        Assert.Equal("synth:antigravity:fixture-segments:step-1", firstLlm.RecordId);
        Assert.Equal(RecordStatus.Ok, firstLlm.Status);
        Assert.Equal(2, firstLlm.Parts.Count);
        Assert.Equal(ContentBucket.UserMessage, firstLlm.Parts[0].Bucket);
        Assert.Equal("Please inspect the repository status", firstLlm.Parts[0].PlainText);
        Assert.Equal("user", firstLlm.Parts[0].Role);
        Assert.Equal(ContentBucket.ConversationHistory, firstLlm.Parts[1].Bucket);
        Assert.Equal("Checking status now.", firstLlm.Parts[1].PlainText);
        Assert.Equal("assistant", firstLlm.Parts[1].Role);
        Assert.Empty(firstLlm.Tokens);

        CollectedRecord runCommand = toolRecords[0];
        Assert.Equal("run_command", runCommand.Name);
        Assert.Equal(llmRecords[1].RecordId, runCommand.ParentRecordId);
        Assert.Equal(RecordStatus.Ok, runCommand.Status);
        Assert.Equal(1000L, runCommand.ReportedDurationMs);
        Assert.NotNull(runCommand.Tool);
        Assert.Equal("run_command", runCommand.Tool.ToolName);
        Assert.NotNull(runCommand.Tool.Input);
        Assert.Null(runCommand.Tool.Input.PlainText);
        Assert.Equal(64, runCommand.Tool.Input.Sha256Fingerprint?.Length);
        string expectedRunArgsJson = "{\"CommandLine\":\"synthetic-command --dry-run\",\"Cwd\":\"/synthetic/workspace\"}";
        Assert.Equal(Sha256Hex(expectedRunArgsJson), runCommand.Tool.Input.Sha256Fingerprint);
        Assert.NotNull(runCommand.Tool.Result);
        Assert.Equal("synthetic command output", runCommand.Tool.Result.PlainText);
        Assert.True(runCommand.Tool.Succeeded);

        CollectedRecord viewFile = toolRecords[1];
        Assert.Equal("view_file", viewFile.Name);
        Assert.Equal(llmRecords[2].RecordId, viewFile.ParentRecordId);
        Assert.NotNull(viewFile.Tool?.Input);
        Assert.Null(viewFile.Tool.Input.PlainText);
        string expectedViewArgsJson = "{\"AbsolutePath\":\"/synthetic/workspace/src/sample.ts\",\"StartLine\":12}";
        Assert.Equal(Sha256Hex(expectedViewArgsJson), viewFile.Tool.Input.Sha256Fingerprint);
        Assert.Equal("synthetic file contents", viewFile.Tool.Result?.PlainText);
    }

    [Fact]
    public void Map_TranscriptMultiToolStep_InfersSkillsFromViewFileOnlyAndLeavesSharedGenericUnpaired()
    {
        string jsonl = string.Join('\n',
        [
            JsonSerializer.Serialize(new
            {
                step_index = 2,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-06-21T13:32:10Z",
                tool_calls = new object[]
                {
                    new
                    {
                        name = "view_file",
                        args = new Dictionary<string, string>
                        {
                            ["AbsolutePath"] = EncodeTwice("/home/u/.gemini/config/skills/test-dev/SKILL.md"),
                        },
                    },
                    new
                    {
                        name = "view_file",
                        args = new Dictionary<string, string>
                        {
                            ["AbsolutePath"] = EncodeTwice("C:\\x\\skills\\test-dev\\SKILL.md"),
                        },
                    },
                    new
                    {
                        name = "view_file",
                        args = new Dictionary<string, string>
                        {
                            ["AbsolutePath"] = EncodeTwice("C:\\x\\skills\\beta\\SKILL.md"),
                        },
                    },
                    new
                    {
                        name = "grep_search",
                        args = new Dictionary<string, string>
                        {
                            ["SearchPath"] = EncodeTwice("/x/skills/gamma/SKILL.md"),
                            ["Query"] = EncodeTwice("needle"),
                        },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 3,
                source = "SYSTEM",
                type = "GENERIC",
                status = "DONE",
                created_at = "2026-06-21T13:32:12Z",
                content = "shared output that cannot be attributed",
            }),
        ]) + "\n";

        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(
            harnessId: "antigravity-cli",
            sourceKey: "/home/dev/.gemini/antigravity-cli/brain/conv-skills/.system_generated/logs/transcript.jsonl",
            format: "antigravity-jsonl",
            rawText: jsonl);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("antigravity-cli", session.HarnessId);
        Assert.Equal("conv-skills", session.SessionKey);

        CollectedRecord parent = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal("antigravity-cli", parent.HarnessId);
        Assert.Equal(["test-dev", "beta"], parent.SkillsInvoked);

        List<CollectedRecord> tools = batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke).ToList();
        Assert.Equal(4, tools.Count);
        Assert.All(tools, static tool =>
        {
            Assert.Null(tool.Tool?.Result);
            Assert.Equal(RecordStatus.Unset, tool.Status);
            Assert.Null(tool.Tool?.Succeeded);
        });
    }

    [Fact]
    public void Map_TranscriptMalformedLinesAndJsonContent_WithholdsJsonDocumentsAndRecordsSafeProblems()
    {
        string jsonl = string.Join('\n',
        [
            "{not valid json",
            "42",
            JsonSerializer.Serialize(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                status = "DONE",
                created_at = "2026-06-21T13:32:10Z",
                content = "{\"secret\":true,\"token\":\"abc\"}",
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "ERROR",
                created_at = "2026-06-21T13:32:11Z",
                tool_calls = new[]
                {
                    new
                    {
                        name = "mcp__kw-docs__docs_answer",
                        args = new Dictionary<string, string>
                        {
                            ["query"] = EncodeTwice("field register"),
                        },
                    },
                },
            }),
        ]) + "\n";

        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(
            harnessId: "antigravity",
            sourceKey: "/home/dev/.gemini/antigravity/brain/conv-json/.system_generated/logs/transcript.jsonl",
            format: "antigravity-jsonl",
            rawText: jsonl);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Equal(2, batch.Problems.Count);
        Assert.All(batch.Problems, static p =>
        {
            Assert.Equal(AntigravityTranscriptMapper.MalformedJsonCode, p.Code);
            Assert.False(JsonDocumentText.IsDocument(p.Message));
        });

        CollectedRecord parent = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Equal(RecordStatus.Error, parent.Status);
        CollectedContentPart userPart = Assert.Single(parent.Parts);
        Assert.Null(userPart.PlainText);
        Assert.NotNull(userPart.Withheld);
        Assert.True(userPart.Withheld.Utf8Bytes > 0);

        CollectedRecord tool = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("kw-docs", tool.Tool?.McpServerName);
        Assert.Equal("docs_answer", tool.Tool?.McpToolName);
        Assert.Null(tool.Tool?.Input?.PlainText);
    }

    [Fact]
    public void Map_StatusLineJsonl_ForAntigravityCli_CollapsesRunsDeltasMonotonicAndResetsNonMonotonic()
    {
        static string PayloadLine(string convId, long input, long output, long cacheRead = 0, string? at = null)
        {
            Dictionary<string, object?> obj = new()
            {
                ["conversation_id"] = convId,
                ["session_id"] = "session-1",
                ["cwd"] = "/workspace/secret-project",
                ["model"] = new Dictionary<string, object?>
                {
                    ["id"] = "Gemini 3.5 Flash (High)",
                    ["display_name"] = "Gemini 3.5 Flash (High)",
                },
                ["context_window"] = new Dictionary<string, object?>
                {
                    ["context_window_size"] = 1_000_000,
                    ["current_usage"] = new Dictionary<string, object?>
                    {
                        ["input_tokens"] = input,
                        ["output_tokens"] = output,
                        ["cache_creation_input_tokens"] = 0,
                        ["cache_read_input_tokens"] = cacheRead,
                    },
                },
            };

            if (at is not null)
            {
                obj["at"] = at;
            }

            return JsonSerializer.Serialize(obj);
        }

        string jsonl = string.Join('\n',
        [
            PayloadLine("statusline-runs", 100, 10, 0, "2026-11-19T10:00:00.000Z"),
            PayloadLine("statusline-runs", 200, 20, 0, "2026-11-19T10:00:01.000Z"),
            PayloadLine("statusline-runs", 200, 20, 0, "2026-11-19T10:00:02.000Z"),
            PayloadLine("statusline-runs", 300, 30, 50, "2026-11-19T10:00:03.000Z"),
            PayloadLine("statusline-reset", 1000, 100, 0, "2026-11-19T10:01:00.000Z"),
            PayloadLine("statusline-reset", 1000, 100, 0, "2026-11-19T10:01:01.000Z"),
            PayloadLine("statusline-reset", 200, 30, 500, "2026-11-19T10:01:02.000Z"),
        ]) + "\n";

        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(
            harnessId: "antigravity",
            sourceKey: "/home/dev/.kyberdash/cache/antigravity-statusline.jsonl",
            format: "antigravity-statusline-jsonl",
            rawText: jsonl);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal(2, batch.Sessions.Count);
        Assert.All(batch.Sessions, static s => Assert.Equal("antigravity-cli", s.HarnessId));

        Assert.Equal(4, batch.Records.Count);
        Assert.All(batch.Records, static r =>
        {
            Assert.Equal("antigravity-cli", r.HarnessId);
            Assert.Equal(RecordOp.LlmInvoke, r.Op);
            Assert.Equal("Gemini 3.5 Flash (High)", r.Model);
            Assert.DoesNotContain(r.Tokens, static t => t.TokenClass == TokenClass.Reasoning);
        });

        List<CollectedRecord> runsRecords = batch.Records.Where(static r => r.SessionKey == "statusline-runs").ToList();
        Assert.Equal(2, runsRecords.Count);
        Assert.Equal(200L, runsRecords[0].Tokens.Single(static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(20L, runsRecords[0].Tokens.Single(static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(0L, runsRecords[0].Tokens.Single(static t => t.TokenClass == TokenClass.CacheRead).Count);

        Assert.Equal(100L, runsRecords[1].Tokens.Single(static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(10L, runsRecords[1].Tokens.Single(static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(50L, runsRecords[1].Tokens.Single(static t => t.TokenClass == TokenClass.CacheRead).Count);

        List<CollectedRecord> resetRecords = batch.Records.Where(static r => r.SessionKey == "statusline-reset").ToList();
        Assert.Equal(2, resetRecords.Count);
        Assert.Equal(1000L, resetRecords[0].Tokens.Single(static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(100L, resetRecords[0].Tokens.Single(static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(200L, resetRecords[1].Tokens.Single(static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(30L, resetRecords[1].Tokens.Single(static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(500L, resetRecords[1].Tokens.Single(static t => t.TokenClass == TokenClass.CacheRead).Count);

        // Unmapped cwd must not be leaked into any session or record field.
        string serialized = JsonSerializer.Serialize(batch);
        Assert.DoesNotContain("/workspace/secret-project", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_StatusLineJsonl_SupportsLegacyStatusLineEventShapeAndWithholdsJsonDocuments()
    {
        string jsonl = string.Join('\n',
        [
            "{not-json",
            "{\"conversation_id\":\"{\\\"nested\\\":1}\",\"model\":\"Gemini 3.5 Flash\",\"context_window\":{\"current_usage\":{\"input_tokens\":10,\"output_tokens\":2}}}",
            "{\"conversation_id\":\"conv-unknown-model\",\"model\":\"unknown\",\"context_window\":{\"current_usage\":{\"input_tokens\":10,\"output_tokens\":2}}}",
            "{\"conversation_id\":\"conv-zero\",\"model\":\"Gemini 3.5 Flash\",\"context_window\":{\"current_usage\":{\"input_tokens\":0,\"output_tokens\":0}}}",
            "{\"conversation_id\":\"conv-json-model\",\"model\":\"{\\\"id\\\":\\\"m\\\"}\",\"context_window\":{\"current_usage\":{\"input_tokens\":10,\"output_tokens\":2}}}",
            "{\"at\":\"2026-11-19T10:05:00.000Z\",\"conversationId\":\"conv-legacy\",\"sessionId\":\"sess-legacy\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"inputTokens\":42,\"outputTokens\":8,\"cacheCreationInputTokens\":3,\"cacheReadInputTokens\":11}}",
        ]) + "\n";

        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(
            harnessId: "antigravity",
            sourceKey: "/home/dev/.kyberdash/cache/antigravity-statusline.jsonl",
            format: "antigravity-statusline-jsonl",
            rawText: jsonl);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(AntigravityTranscriptMapper.MalformedJsonCode, problem.Code);
        Assert.False(JsonDocumentText.IsDocument(problem.Message));

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("conv-legacy", session.SessionKey);
        Assert.Equal("antigravity-cli", session.HarnessId);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("conv-legacy", record.SessionKey);
        Assert.Equal("claude-sonnet-4-6", record.Model);
        Assert.Equal(42L, record.Tokens.Single(static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(8L, record.Tokens.Single(static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(3L, record.Tokens.Single(static t => t.TokenClass == TokenClass.CacheWrite).Count);
        Assert.Equal(11L, record.Tokens.Single(static t => t.TokenClass == TokenClass.CacheRead).Count);
    }

    [Fact]
    public void SegmentTranscript_AndHelpers_MatchTranscriptSegmentationContract()
    {
        Assert.Equal("agy-step:5", AntigravityTranscriptMapper.AgyStepId(5));
        Assert.Equal(5L, AntigravityTranscriptMapper.TryParseAgyStepIndex("agy-step:5"));
        Assert.Null(AntigravityTranscriptMapper.TryParseAgyStepIndex("agy-step:abc"));
        Assert.Null(AntigravityTranscriptMapper.TryParseAgyStepIndex(null));

        Assert.Equal("docs", AntigravityTranscriptMapper.SkillNameFromPath("C:\\x\\skills\\docs\\SKILL.md"));
        Assert.Null(AntigravityTranscriptMapper.SkillNameFromPath("/x/skills/docs/README.md"));
        Assert.Null(AntigravityTranscriptMapper.SkillNameFromPath("/x/skills/../SKILL.md"));

        string transcript = string.Join('\n',
        [
            JsonSerializer.Serialize(new
            {
                step_index = 3,
                type = "PLANNER_RESPONSE",
                tool_calls = new[]
                {
                    new { name = "run_command", args = new Dictionary<string, string> { ["CommandLine"] = EncodeTwice("echo 1") } },
                },
            }),
            JsonSerializer.Serialize(new { step_index = 4, type = "GENERIC", content = "1" }),
            JsonSerializer.Serialize(new
            {
                step_index = 6,
                type = "PLANNER_RESPONSE",
                tool_calls = new[]
                {
                    new { name = "view_file", args = new Dictionary<string, string> { ["AbsolutePath"] = EncodeTwice("/repo/a.cs") } },
                },
            }),
            JsonSerializer.Serialize(new { step_index = 7, type = "GENERIC", content = "code" }),
        ]);

        IReadOnlyList<AntigravityTranscriptStep> parsed = AntigravityTranscriptMapper.ParseTranscriptSteps(transcript);
        Assert.Equal(4, parsed.Count);

        IReadOnlyList<AntigravityTranscriptSegment> segments = AntigravityTranscriptMapper.SegmentTranscript(parsed, [2L, 5L]);
        Assert.Equal(2, segments.Count);
        Assert.True(segments[0].Covered);
        Assert.Equal(["run_command"], segments[0].Steps.SelectMany(static s => s.ToolCalls.Select(static c => c.ToolName)));
        Assert.Equal("1", segments[0].Result);
        Assert.True(segments[1].Covered);
        Assert.Equal(["view_file"], segments[1].Steps.SelectMany(static s => s.ToolCalls.Select(static c => c.ToolName)));
        Assert.Equal("code", segments[1].Result);
    }

    [Fact]
    public void Map_TranscriptAndStatusLineBatches_WriteCleanlyToInMemoryRelationalStore()
    {
        AntigravityTranscriptMapper mapper = new();
        InMemoryRelationalStore store = new();

        string transcriptJsonl = string.Join('\n',
        [
            JsonSerializer.Serialize(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                status = "DONE",
                created_at = "2026-11-19T10:00:00Z",
                content = "Read the skill file",
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-11-19T10:00:01Z",
                content = "Reading it now.",
                tool_calls = new[]
                {
                    new
                    {
                        name = "view_file",
                        args = new Dictionary<string, string>
                        {
                            ["AbsolutePath"] = EncodeTwice("/home/u/.gemini/config/skills/csharp-dev/SKILL.md"),
                        },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 2,
                source = "SYSTEM",
                type = "GENERIC",
                status = "DONE",
                created_at = "2026-11-19T10:00:02Z",
                content = "skill body",
            }),
        ]) + "\n";

        string statusLineJsonl = string.Join('\n',
        [
            JsonSerializer.Serialize(new
            {
                at = "2026-11-19T10:05:00.000Z",
                conversation_id = "conv-store-sl",
                model = new { id = "Gemini 3.5 Flash (High)" },
                context_window = new
                {
                    current_usage = new
                    {
                        input_tokens = 500,
                        output_tokens = 40,
                        cache_creation_input_tokens = 0,
                        cache_read_input_tokens = 100,
                    },
                },
            }),
        ]) + "\n";

        CollectedBatch transcriptBatch = mapper.Map(
            CreateUnit(
                "antigravity",
                "/home/dev/.gemini/antigravity/brain/conv-store-tr/.system_generated/logs/transcript.jsonl",
                "antigravity-jsonl",
                transcriptJsonl),
            ImportedAtUtc);

        CollectedBatch statusBatch = mapper.Map(
            CreateUnit(
                "antigravity",
                "/home/dev/.kyberdash/cache/antigravity-statusline.jsonl",
                "antigravity-statusline-jsonl",
                statusLineJsonl),
            ImportedAtUtc);

        store.WriteBatch(transcriptBatch);
        store.WriteBatch(statusBatch);

        Assert.Equal(3, store.CountRecords());
        Assert.Equal(2, store.Sessions().Count);
    }

    [Fact]
    public void Map_TranscriptPartsCountUnderTheLabelledStandInWhenRecordsCarryNoModel()
    {
        string jsonl = string.Join('\n',
        [
            JsonSerializer.Serialize(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                status = "DONE",
                created_at = "2026-06-21T13:32:10Z",
                content = DivergentJsonText,
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-06-21T13:32:11Z",
                content = "Checking status now.",
            }),
        ]) + "\n";

        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(
            harnessId: "antigravity",
            sourceKey: "/home/dev/.gemini/antigravity/brain/conv-tok/.system_generated/logs/transcript.jsonl",
            format: "antigravity-jsonl",
            rawText: jsonl);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        CollectedRecord parent = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        Assert.Null(parent.Model);
        CollectedContentPart userPart = Assert.Single(parent.Parts, static p => p.Bucket == ContentBucket.UserMessage);
        Assert.Null(userPart.PlainText);
        Assert.NotNull(userPart.Withheld);
        Assert.Equal(
            LocalTextTokenizer.Shared.Count(DivergentJsonText, null).Count,
            userPart.Withheld!.MeasuredTokens);
    }

    private static NativeSourceUnit CreateUnit(
        string harnessId,
        string sourceKey,
        string format,
        string? rawText) =>
        new(
            Unit: new SourceUnit(
                HarnessId: harnessId,
                SourceKey: sourceKey,
                ProviderId: "google",
                ParserId: format == "antigravity-statusline-jsonl" ? "antigravity-statusline-reader" : "antigravity-reader",
                ParserContractVersion: "1",
                Format: format,
                SourceRootLabel: format == "antigravity-statusline-jsonl" ? "cache" : "~/.gemini/antigravity",
                RevisionToken: "1:100"),
            RawText: rawText,
            SizeBytes: rawText is null ? 0L : Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: ImportedAtUtc);

    private static string EncodeTwice(object value) =>
        JsonSerializer.Serialize(JsonSerializer.Serialize(value));

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
