using System.Globalization;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Pi;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="PiMapper"/> porting <c>dash/src/providers/pi.ts</c> into
/// typed static DTOs and canonical relational <see cref="CollectedBatch"/> records.
/// </summary>
public class PiMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 4, 14, 10, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Map_ExtractsTokenUsageMetadataAndReportedCostFromAssistantMessage()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"version\":3,\"id\":\"sess-abc\",\"timestamp\":\"2026-04-14T10:00:00.000Z\",\"cwd\":\"/Users/test/myproject\"}",
            "{\"type\":\"message\",\"id\":\"msg-user-1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"implement the feature\"}]}}",
            "{\"type\":\"message\",\"id\":\"msg-asst-1\",\"timestamp\":\"2026-04-14T10:00:30.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Implemented the feature.\"}],\"model\":\"gpt-5.4\",\"responseId\":\"resp-abc\",\"usage\":{\"input\":2000,\"output\":400,\"cacheRead\":5000,\"cacheWrite\":100,\"cost\":{\"total\":0.0055}}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/--Users-test-myproject--/session.jsonl", jsonl);
        PiMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal("pi", mapper.HarnessId);
        Assert.Empty(batch.Problems);
        Assert.Equal("pi", batch.Source.HarnessId);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-abc", session.SessionKey);
        Assert.Equal("sess-abc", session.NativeSessionId);
        Assert.Equal("pi", session.HarnessId);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/Users/test/myproject", session.Workspace.WorkingDirectory);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(RecordOp.LlmInvoke, record.Op);
        Assert.Equal("pi:gpt-5.4", record.Name);
        Assert.Equal("gpt-5.4", record.Model);
        Assert.Equal("synth:pi:sess-abc:resp-abc", record.RecordId);
        Assert.Equal("synth:pi:sess-abc", record.TraceId);
        Assert.Equal(DateTimeOffset.Parse("2026-04-14T10:00:30.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(7100, record.ReportedInputTokens);
        Assert.Equal(400, record.ReportedOutputTokens);
        Assert.NotNull(record.ReportedCost);
        Assert.Equal(0.0055, record.ReportedCost.Amount, 6);
        Assert.Equal("USD", record.ReportedCost.Currency);

        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 2000);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 400);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheRead && t.Count == 5000);
        Assert.Contains(record.Tokens, static t => t.TokenClass == TokenClass.CacheWrite && t.Count == 100);

        Assert.Equal(2, record.Parts.Count);
        Assert.Equal(ContentBucket.UserMessage, record.Parts[0].Bucket);
        Assert.Equal("user", record.Parts[0].Role);
        Assert.Equal("implement the feature", record.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, record.Parts[1].Bucket);
        Assert.Equal("assistant", record.Parts[1].Role);
        Assert.Equal("Implemented the feature.", record.Parts[1].PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords("pi"));
        Assert.Equal(4, store.TokenCounts(spanId: record.RecordId).Count);
    }

    [Fact]
    public void Map_HandlesStringContentOnUserAndAssistantMessages()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-str\",\"timestamp\":\"2026-04-14T10:00:00.000Z\",\"cwd\":\"/Users/test/myproject\"}",
            "{\"type\":\"message\",\"id\":\"msg-user-str\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"user\",\"content\":\"test message from file watcher\"}}",
            "{\"type\":\"message\",\"id\":\"msg-asst-str\",\"timestamp\":\"2026-04-14T10:00:20.000Z\",\"message\":{\"role\":\"assistant\",\"content\":\"Acknowledged.\",\"responseId\":\"resp-str\",\"usage\":{\"input\":500,\"output\":80}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/proj/session.jsonl", jsonl);
        PiMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal(2, record.Parts.Count);
        Assert.Equal(ContentBucket.UserMessage, record.Parts[0].Bucket);
        Assert.Equal("test message from file watcher", record.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, record.Parts[1].Bucket);
        Assert.Equal("Acknowledged.", record.Parts[1].PlainText);
    }

    [Fact]
    public void Map_ResolvesModelAcrossModelChangeAndBareMessageModel()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-model\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"model_change\",\"model\":\"openai-codex/gpt-5.6-terra\"}",
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"model\":\"gpt-5.6-terra\",\"responseId\":\"r1\",\"usage\":{\"input\":100,\"output\":20}}}",
            "{\"type\":\"message\",\"id\":\"m2\",\"timestamp\":\"2026-04-14T10:00:20.000Z\",\"message\":{\"role\":\"assistant\",\"model\":\"anthropic/claude-opus-4\",\"responseId\":\"r2\",\"usage\":{\"input\":150,\"output\":30}}}",
            "{\"type\":\"model_change\",\"model\":\"\"}",
            "{\"type\":\"message\",\"id\":\"m3\",\"timestamp\":\"2026-04-14T10:00:30.000Z\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r3\",\"usage\":{\"input\":50,\"output\":10}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/proj/session.jsonl", jsonl);
        PiMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal(3, batch.Records.Count);
        Assert.Equal("openai-codex/gpt-5.6-terra", batch.Records[0].Model);
        Assert.Equal("anthropic/claude-opus-4", batch.Records[1].Model);
        Assert.Equal("openai-codex/gpt-5.6-terra", batch.Records[2].Model);

        string noModelJsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-default-model\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r1\",\"usage\":{\"input\":100,\"output\":20}}}",
        ]) + "\n";

        CollectedBatch defaultBatch = mapper.Map(
            MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/proj/default.jsonl", noModelJsonl),
            BaselineUtc);
        CollectedRecord defaultRecord = Assert.Single(defaultBatch.Records);
        Assert.Equal("gpt-5", defaultRecord.Model);
    }

    [Fact]
    public void Map_ClassifiesSkillLoadsFromReadToolCallsAndNormalizesToolNames()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-skills\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"message\",\"id\":\"m1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"model\":\"gpt-5.4\",\"responseId\":\"r1\",\"content\":[{\"type\":\"toolCall\",\"id\":\"c1\",\"name\":\"read\",\"arguments\":{\"path\":\"/home/u/.pi/agent/skills/systematic-debugging/SKILL.md\"}},{\"type\":\"toolCall\",\"id\":\"c2\",\"name\":\"read\",\"arguments\":{\"path\":\"skill://commit-workflow\"}},{\"type\":\"toolCall\",\"id\":\"c3\",\"name\":\"read\",\"arguments\":{\"file_path\":\"C:\\\\repo\\\\.agents\\\\skills\\\\deep-research\\\\SKILL.md\"}},{\"type\":\"toolCall\",\"id\":\"c4\",\"name\":\"read\",\"arguments\":{\"path\":\"/home/u/project/src/skill-loader.ts\"}},{\"type\":\"toolCall\",\"id\":\"c5\",\"name\":\"edit\",\"arguments\":{\"path\":\"/home/u/project/a.ts\"}},{\"type\":\"toolCall\",\"id\":\"c6\",\"name\":\"bash\",\"arguments\":{\"command\":\"git status\"}},{\"type\":\"toolCall\",\"id\":\"c7\",\"name\":\"dispatch_agent\",\"arguments\":{}},{\"type\":\"toolCall\",\"id\":\"c8\",\"name\":\"custom_tool\",\"arguments\":{}}],\"usage\":{\"input\":1000,\"output\":200}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/proj/skills.jsonl", jsonl);
        PiMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal(9, batch.Records.Count);
        CollectedRecord parent = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, parent.Op);
        Assert.Equal(["systematic-debugging", "commit-workflow", "deep-research"], parent.SkillsInvoked);

        IReadOnlyList<CollectedRecord> toolRecords = batch.Records.Skip(1).ToList();
        Assert.All(toolRecords, r =>
        {
            Assert.Equal(RecordOp.ToolInvoke, r.Op);
            Assert.Equal(parent.RecordId, r.ParentRecordId);
            Assert.NotNull(r.Tool);
        });

        Assert.Equal(
            ["Skill", "Skill", "Skill", "Read", "Edit", "Bash", "Agent", "custom_tool"],
            toolRecords.Select(static r => r.Tool!.ToolName).ToArray());

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(9, store.CountRecords("pi"));
    }

    [Fact]
    public void Map_SkipsZeroTokenAssistantMessagesDeduplicatesByAnchorAndTreatsZeroCostAsAbsent()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-filter\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"message\",\"id\":\"m-zero\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"responseId\":\"resp-zero\",\"usage\":{\"input\":0,\"output\":0}}}",
            "{\"type\":\"message\",\"id\":\"m-1\",\"timestamp\":\"2026-04-14T10:00:20.000Z\",\"message\":{\"role\":\"assistant\",\"model\":\"grok-code\",\"responseId\":\"resp-dup\",\"usage\":{\"input\":400,\"output\":50,\"cost\":{\"total\":0}}}}",
            "{\"type\":\"message\",\"id\":\"m-2\",\"timestamp\":\"2026-04-14T10:00:25.000Z\",\"message\":{\"role\":\"assistant\",\"model\":\"grok-code\",\"responseId\":\"resp-dup\",\"usage\":{\"input\":400,\"output\":50,\"cost\":{\"total\":0.01}}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("omp", "/home/u/.omp/agent/sessions/proj/session.jsonl", jsonl);
        PiMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal("omp", batch.Source.HarnessId);
        CollectedRecord kept = Assert.Single(batch.Records);
        Assert.Equal("omp", kept.HarnessId);
        Assert.Equal("synth:omp:sess-filter:resp-dup", kept.RecordId);
        Assert.Null(kept.ReportedCost);
    }

    [Fact]
    public void Map_SupportsOmpTitleSlotAndTimestampFallbackChain()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"title\",\"v\":1,\"title\":\"Fix the bug\",\"updatedAt\":\"2026-04-14T10:00:00.000Z\"}",
            "{\"type\":\"session\",\"id\":\"sess-ts\",\"timestamp\":\"2026-04-14T10:00:00.000Z\",\"cwd\":\"/work/repo\"}",
            "{\"type\":\"message\",\"id\":\"u1\",\"message\":{\"role\":\"user\",\"content\":\"first\",\"attribution\":{\"timestamp\":1776023210000}}}",
            "{\"type\":\"message\",\"id\":\"a1\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r1\",\"usage\":{\"input\":10,\"output\":5}}}",
            "{\"type\":\"message\",\"id\":\"a2\",\"timestamp\":\"2026-04-14T10:01:00.000Z\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r2\",\"usage\":{\"input\":20,\"output\":5}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("omp", "/home/u/.omp/agent/sessions/proj/omp.jsonl", jsonl);
        PiMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("Fix the bug", session.Title);

        Assert.Equal(2, batch.Records.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1776023210000), batch.Records[0].StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-04-14T10:01:00.000Z", CultureInfo.InvariantCulture), batch.Records[1].StartedAt);

        string noTimestampJsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-no-ts\"}",
            "{\"type\":\"message\",\"id\":\"a1\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r1\",\"usage\":{\"input\":10,\"output\":5}}}",
        ]) + "\n";

        CollectedBatch skippedBatch = mapper.Map(
            MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/proj/no-ts.jsonl", noTimestampJsonl),
            BaselineUtc);
        Assert.Empty(skippedBatch.Records);
    }

    [Fact]
    public void Map_DetectsUserCorrectionWithholdsJsonPartsAndRecordsMalformedLineProblem()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session\",\"id\":\"sess-corr\",\"timestamp\":\"2026-04-14T10:00:00.000Z\"}",
            "{not valid json",
            "{\"type\":\"message\",\"id\":\"u1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"user\",\"content\":\"No, do not modify that file.\"}}",
            "{\"type\":\"message\",\"id\":\"a1\",\"timestamp\":\"2026-04-14T10:00:20.000Z\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r1\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"secret\\\":true}\"}],\"usage\":{\"input\":100,\"output\":20}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/proj/corr.jsonl", jsonl);
        PiMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal("PI_MALFORMED_LINE", problem.Code);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("prefix_negation", record.Signals.CorrectionRule);
        CollectedContentPart assistantPart = Assert.Single(record.Parts, static p => p.Bucket == ContentBucket.ConversationHistory);
        Assert.Null(assistantPart.PlainText);
        Assert.NotNull(assistantPart.Withheld);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords("pi"));
    }

    [Fact]
    public void Parse_PopulatesTypedStaticDtosAndHandlesEmptyInput()
    {
        PiMapper mapper = new();
        CollectedBatch empty = mapper.Map(
            MakeNativeUnit("pi", "/home/u/.pi/agent/sessions/proj/empty.jsonl", "   \n"),
            BaselineUtc);

        Assert.Empty(empty.Sessions);
        Assert.Empty(empty.Records);
        Assert.Empty(empty.Problems);

        string jsonl = string.Join('\n',
        [
            "{\"type\":\"title\",\"v\":1,\"title\":\"Typed DTO test\"}",
            "{\"type\":\"session\",\"id\":\"sess-dto\",\"timestamp\":\"2026-04-14T10:00:00.000Z\",\"cwd\":\"/work/app\"}",
            "{\"type\":\"model_change\",\"model\":\"gpt-5.4\"}",
            "{\"type\":\"message\",\"id\":\"a1\",\"timestamp\":\"2026-04-14T10:00:10.000Z\",\"message\":{\"role\":\"assistant\",\"responseId\":\"r1\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call-1\",\"name\":\"bash\",\"arguments\":{\"command\":\"dotnet test\"}}],\"usage\":{\"input\":12,\"output\":4}}}",
        ]) + "\n";

        PiDocument document = PiMapper.Parse(jsonl, "/home/u/.pi/agent/sessions/proj/empty.jsonl");
        Assert.Equal("sess-dto", document.SessionId);
        Assert.Equal("/work/app", document.WorkingDirectory);
        Assert.Equal("Typed DTO test", document.Title);
        Assert.Empty(document.ParseProblems);
        Assert.Equal(4, document.Entries.Count);

        PiEntry messageEntry = document.Entries[3];
        Assert.Equal("message", messageEntry.EntryType);
        Assert.NotNull(messageEntry.Message);
        Assert.Equal("assistant", messageEntry.Message.Role);
        PiContentBlock block = Assert.Single(messageEntry.Message.Blocks);
        Assert.Equal("toolCall", block.BlockType);
        Assert.NotNull(block.ToolCall);
        Assert.Equal("bash", block.ToolCall.Name);
        Assert.Equal("dotnet test", block.ToolCall.Command);
    }

    private static NativeSourceUnit MakeNativeUnit(string harnessId, string path, string rawText)
    {
        SourceUnit sourceUnit = new(
            HarnessId: harnessId,
            SourceKey: Path.GetFullPath(path),
            ProviderId: harnessId,
            ParserId: "pi-reader",
            ParserContractVersion: "1",
            Format: "pi-jsonl",
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
