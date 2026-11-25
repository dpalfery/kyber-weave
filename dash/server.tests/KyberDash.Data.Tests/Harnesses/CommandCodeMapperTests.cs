using System.Globalization;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.CommandCode;
using KyberDash.Data.Tokenization;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CommandCodeMapper"/> porting <c>dash/src/providers/commandcode.ts</c> into
/// typed static DTOs and canonical relational <see cref="CollectedBatch"/> records.
/// </summary>
public class CommandCodeMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 4, 14, 10, 5, 0, TimeSpan.Zero);

    /// <summary>A JSON document whose token counts differ between cl100k_base and the o200k_base stand-in.</summary>
    private const string DivergentJsonText = "{\"summary\":\"こんにちは世界、今日はいい天気ですね。\"}";

    [Fact]
    public void Map_ExtractsUsageBearingAssistantMessageWithFreshInputMath()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"version\":3,\"id\":\"sess-cc\",\"timestamp\":\"2026-04-14T10:00:00.000Z\",\"cwd\":\"/Users/test/myproject\"}",
            "{\"type\":\"message\",\"id\":\"msg-user-1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"implement the feature\"}]}}",
            "{\"type\":\"message\",\"id\":\"msg-asst-1\",\"timestamp\":\"2026-04-14T10:00:30.000Z\",\"model\":\"claude-opus-4-6\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]},\"usage\":{\"inputTokens\":1500,\"outputTokens\":200,\"cacheReadTokens\":1000,\"cacheWriteTokens\":100,\"costUsd\":0.012}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/myproject/sess-cc.jsonl", jsonl);
        CommandCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal("commandcode", mapper.HarnessId);
        Assert.Empty(batch.Problems);
        Assert.Equal("commandcode", batch.Source.HarnessId);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-cc", session.SessionKey);
        Assert.Equal("sess-cc", session.NativeSessionId);
        Assert.Equal("commandcode", session.HarnessId);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/Users/test/myproject", session.Workspace.WorkingDirectory);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal("commandcode:claude-opus-4-6", record.Name);
        Assert.Equal("claude-opus-4-6", record.Model);
        Assert.Equal("synth:commandcode:sess-cc:msg-asst-1", record.RecordId);
        Assert.Equal("synth:commandcode:sess-cc", record.TraceId);
        Assert.Equal(DateTimeOffset.Parse("2026-04-14T10:00:30.000Z", CultureInfo.InvariantCulture), record.StartedAt);

        // AI SDK totalUsage inputTokens includes both cache classes: fresh input is recovered
        // by subtracting them before the call is yielded.
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 400);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 1000);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite && t.Count == 100);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 200);
        Assert.Equal(1500, record.ReportedInputTokens);
        Assert.Equal(200, record.ReportedOutputTokens);
        Assert.NotNull(record.ReportedCost);
        Assert.Equal(0.012, record.ReportedCost.Amount, 6);
        Assert.Equal("USD", record.ReportedCost.Currency);

        Assert.Equal(2, record.Parts.Count);
        Assert.Equal(ContentBucket.UserMessage, record.Parts[0].Bucket);
        Assert.Equal("user", record.Parts[0].Role);
        Assert.Equal("implement the feature", record.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, record.Parts[1].Bucket);
        Assert.Equal("Done.", record.Parts[1].PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords("commandcode"));
        Assert.Equal(4, store.TokenCounts(spanId: record.RecordId).Count);
    }

    [Fact]
    public void Map_CountsPartsUnderTheMessageModelOwnTokenizer()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-tok\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"model\":\"gpt-4\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"summary\\\":\\\"こんにちは世界、今日はいい天気ですね。\\\"}\"}]},\"usage\":{\"inputTokens\":100,\"outputTokens\":20}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/proj/sess-tok.jsonl", jsonl);
        CollectedBatch batch = new CommandCodeMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("gpt-4", record.Model);
        CollectedContentPart part = Assert.Single(record.Parts);
        Assert.Null(part.PlainText);
        Assert.NotNull(part.Withheld);
        Assert.Equal(
            LocalTextTokenizer.Shared.Count(DivergentJsonText, "gpt-4").Count,
            part.Withheld!.MeasuredTokens);
    }

    [Fact]
    public void Map_MapsToolUseBlocksToChildToolInvokesWithNormalizedNames()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-tools\",\"timestamp\":\"2026-04-14T10:00:00.000Z\",\"cwd\":\"/work/repo\"}",
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"model\":\"model-x\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"read_file\",\"input\":{\"path\":\"a.ts\"}},{\"type\":\"tool_use\",\"name\":\"shell_command\",\"input\":{\"command\":\"git status\"}},{\"type\":\"tool_use\",\"name\":\"activate_skill\",\"input\":{\"name\":\"code-review\"}},{\"type\":\"tool_use\",\"name\":\"agent\",\"input\":{\"subagent_type\":\"explorer\"}},{\"type\":\"tool_use\",\"name\":\"custom_widget\",\"input\":{}}]},\"usage\":{\"inputTokens\":900,\"outputTokens\":50}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/repo/sess-tools.jsonl", jsonl);
        CommandCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal(6, batch.Records.Count);
        CollectedRecord parent = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, parent.Op);
        Assert.Equal(["code-review"], parent.SkillsInvoked);

        IReadOnlyList<CollectedRecord> toolRecords = batch.Records.Skip(1).ToList();
        Assert.All(toolRecords, r =>
        {
            Assert.Equal(RecordOp.ToolInvoke, r.Op);
            Assert.Equal(parent.RecordId, r.ParentRecordId);
            Assert.Equal(parent.TraceId, r.TraceId);
            Assert.Equal(parent.StartedAt, r.StartedAt);
            Assert.NotNull(r.Tool);
        });

        Assert.Equal(
            ["Read", "Bash", "activate_skill", "Agent", "custom_widget"],
            toolRecords.Select(static r => r.Tool!.ToolName).ToArray());

        CollectedRecord readCall = toolRecords[0];
        Assert.Equal("call-0", readCall.Tool!.CallId);
        Assert.NotNull(readCall.Tool.Input);

        // Object inputs are withheld to size + fingerprint like every other harness mapper.
        Assert.Null(readCall.Tool.Input.PlainText);
        Assert.True(readCall.Tool.Input.Utf8Bytes > 0);
        Assert.NotNull(readCall.Tool.Input.Sha256Fingerprint);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(6, store.CountRecords("commandcode"));
    }

    [Fact]
    public void Map_SkipsZeroTokenUsageLessAndTimestamplessMessagesAndDedupesAnchors()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-skip\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"message\",\"id\":\"m-zero\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}}",
            "{\"type\":\"message\",\"id\":\"m-nousage\",\"timestamp\":\"2026-04-14T10:00:20.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]}}",
            "{\"type\":\"message\",\"id\":\"m-dup\",\"timestamp\":\"2026-04-14T10:00:30.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":100,\"outputTokens\":10}}",
            "{\"type\":\"message\",\"id\":\"m-dup\",\"timestamp\":\"2026-04-14T10:00:30.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":100,\"outputTokens\":10}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/proj/sess-skip.jsonl", jsonl);
        CommandCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedRecord kept = Assert.Single(batch.Records);
        Assert.Equal("synth:commandcode:sess-skip:m-dup", kept.RecordId);

        string noTimestampJsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-no-ts\"}",
            "{\"type\":\"message\",\"id\":\"a1\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":10,\"outputTokens\":5}}",
        ]) + "\n";

        CollectedBatch skippedBatch = mapper.Map(
            MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/proj/sess-no-ts.jsonl", noTimestampJsonl),
            BaselineUtc);
        Assert.Empty(skippedBatch.Records);
        Assert.Single(skippedBatch.Sessions);
    }

    [Fact]
    public void Map_TracksSessionModelAcrossModelChangeAndFallsBackToUnknown()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-model\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"model_change\",\"model\":\"acme/large-1\"}",
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":100,\"outputTokens\":20}}",
            "{\"type\":\"message\",\"id\":\"m2\",\"timestamp\":\"2026-04-14T10:00:20.000Z\",\"model\":\"other/small-2\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":150,\"outputTokens\":30}}",
            "{\"type\":\"model_change\",\"model\":\"\"}",
            "{\"type\":\"message\",\"id\":\"m3\",\"timestamp\":\"2026-04-14T10:00:30.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":50,\"outputTokens\":10}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/proj/sess-model.jsonl", jsonl);
        CommandCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal(3, batch.Records.Count);
        Assert.Equal("acme/large-1", batch.Records[0].Model);
        Assert.Equal("other/small-2", batch.Records[1].Model);
        Assert.Equal("acme/large-1", batch.Records[2].Model);

        string noModelJsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-default-model\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":100,\"outputTokens\":20}}",
        ]) + "\n";

        CollectedBatch defaultBatch = mapper.Map(
            MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/proj/default.jsonl", noModelJsonl),
            BaselineUtc);
        CollectedRecord defaultRecord = Assert.Single(defaultBatch.Records);
        Assert.Equal("unknown", defaultRecord.Model);
    }

    [Fact]
    public void Map_HandlesStringContentJoinsUserTextsAndReportsMalformedLines()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-str\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "not json at all",
            "{\"type\":\"message\",\"id\":\"u1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"user\",\"content\":\"first prompt\"}}",
            "{\"type\":\"message\",\"id\":\"u2\",\"timestamp\":\"2026-04-14T10:00:12.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"second\"},{\"type\":\"text\",\"text\":\"prompt\"}]}}",
            "{\"type\":\"message\",\"id\":\"a1\",\"timestamp\":\"2026-04-14T10:00:20.000Z\",\"message\":{\"role\":\"assistant\",\"content\":\"Acknowledged.\",\"other\":\"ignored\"},\"usage\":{\"inputTokens\":500,\"outputTokens\":80}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/proj/sess-str.jsonl", jsonl);
        CommandCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal(CommandCodeMapper.MalformedLineCode, problem.Code);
        Assert.Equal("commandcode", problem.HarnessId);
        Assert.Equal("sess-str", problem.SessionKey);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(2, record.Parts.Count);

        // Like commandcode.ts, each user message replaces (not appends) the pending prompt.
        Assert.Equal("second prompt", record.Parts[0].PlainText);
        Assert.Equal(ContentBucket.UserMessage, record.Parts[0].Bucket);
        Assert.Equal("Acknowledged.", record.Parts[1].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, record.Parts[1].Bucket);
    }

    [Fact]
    public void Map_TreatsZeroCostAsAbsentAndFallsBackToFileStemSessionId()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[]},\"usage\":{\"inputTokens\":400,\"outputTokens\":50,\"cost\":{\"invalid\":\"shape\"},\"costUsd\":0}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("commandcode", "/home/u/.commandcode/projects/proj/fallback-name.jsonl", jsonl);
        CommandCodeMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("fallback-name", session.SessionKey);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Null(record.ReportedCost);
        Assert.Equal("synth:commandcode:fallback-name:m1", record.RecordId);
        Assert.Equal(DateTimeOffset.Parse("2026-04-14T10:00:10.000Z", CultureInfo.InvariantCulture), record.StartedAt);
    }

    private static NativeSourceUnit MakeNativeUnit(string harnessId, string path, string rawText)
    {
        SourceUnit sourceUnit = new(
            HarnessId: harnessId,
            SourceKey: Path.GetFullPath(path),
            ProviderId: harnessId,
            ParserId: "commandcode-reader",
            ParserContractVersion: "1",
            Format: "commandcode-jsonl",
            SourceRootLabel: Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(
            Unit: sourceUnit,
            RawText: rawText,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: BaselineUtc);
    }
}
