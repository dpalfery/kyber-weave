using System.Security.Cryptography;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Droid;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="DroidSessionMapper"/> verifying pure Factory Droid session JSONL
/// and companion <c>.settings.json</c> mapping, companion totals as session usage with assistant
/// calls left not measured, model prefix stripping,
/// absent child rows, tool call pairing, JSON document withholding, and internal session filtering.
/// </summary>
public class DroidSessionMapperTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_ThrowsArgumentNullException_WhenUnitIsNull()
    {
        DroidSessionMapper mapper = new();

        Assert.Throws<ArgumentNullException>(() => mapper.Map(null!, BaselineUtc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    [InlineData("{\"type\":\"session_start\",\"id\":\"sess-empty\",\"cwd\":\"/work/repo\"}\n{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}}")]
    [InlineData("{\"type\":\"event\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Not a session start\"}]}}")]
    public void Map_ReturnsEmptySessionsAndRecords_WhenRawTextIsEmptyMissingSessionStartOrWithoutAssistantCalls(string? rawText)
    {
        DroidSessionMapper mapper = new();
        NativeSourceUnit unit = CreateUnit("/home/dev/.factory/sessions/repo/empty.jsonl", rawText);

        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal("droid", mapper.HarnessId);
        Assert.Equal("droid", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);
        Assert.Equal("/home/dev/.factory/sessions/repo/empty.jsonl", batch.Source.Locator);
        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_SkipsInternalHousekeepingSession_WhenWorkingDirectoryIsFactoryRoot()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session_start\",\"id\":\"sess-internal\",\"title\":\"Housekeeping\",\"cwd\":\"/home/dev/.factory/\"}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Clean cache\"}]}}",
            "{\"type\":\"message\",\"id\":\"msg-1\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Cleaned.\"}]}}",
        ]) + "\n";

        string settingsJson = """
            {
              "model": "claude-sonnet-4-5-20250929",
              "tokenUsage": {
                "inputTokens": 50,
                "outputTokens": 10
              }
            }
            """;

        DroidSessionMapper mapper = new();
        CollectedBatch batch = mapper.Map(
            CreateUnit("/home/dev/.factory/sessions/factory/housekeeping.jsonl", jsonl, settingsJson),
            BaselineUtc);

        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_KeepsCompanionTokenUsageAsSessionUsageAndLeavesAssistantCallsNotMeasured()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session_start\",\"id\":\"sess-droid-1\",\"title\":\"  Build Droid mapper  \",\"cwd\":\"/home/dev/projects/kyber-weave\"}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Inspect the repo\"}]}}",
            "{\"type\":\"message\",\"id\":\"a-1\",\"timestamp\":\"2026-11-19T10:00:02.500Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Scanning files\"},{\"type\":\"text\",\"text\":\"First turn done.\"}]}}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:10.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Summarize findings\"}]}}",
            "{\"type\":\"message\",\"id\":\"a-2\",\"timestamp\":\"2026-11-19T10:00:14.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Summary ready.\"}]}}",
        ]) + "\n";

        string settingsJson = """
            {
              "model": "custom:GLM-5.1-[Proxy]-0",
              "tokenUsage": {
                "inputTokens": 101,
                "outputTokens": 41,
                "cacheCreationTokens": 10,
                "cacheReadTokens": 203,
                "thinkingTokens": 7
              }
            }
            """;

        DroidSessionMapper mapper = new();
        CollectedBatch batch = mapper.Map(
            CreateUnit("/home/dev/.factory/sessions/kyber-weave/sess-droid-1.jsonl", jsonl, settingsJson),
            BaselineUtc);

        Assert.Empty(batch.Problems);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-droid-1", session.SessionKey);
        Assert.Equal("droid", session.HarnessId);
        Assert.Equal("sess-droid-1", session.NativeSessionId);
        Assert.Equal("Build Droid mapper", session.Title);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/home/dev/projects/kyber-weave", session.Workspace!.WorkingDirectory);
        Assert.Null(session.Workspace.Repository);
        Assert.Null(session.Workspace.Branch);

        Assert.Equal(5, session.ReportedUsage.Count);
        Assert.Contains(new CollectedModelUsage(0, "GLM-5.1", TokenClass.Input, 101L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "GLM-5.1", TokenClass.Output, 41L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "GLM-5.1", TokenClass.CacheWrite, 10L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "GLM-5.1", TokenClass.CacheRead, 203L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "GLM-5.1", TokenClass.Reasoning, 7L), session.ReportedUsage);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord first = batch.Records[0];
        Assert.Equal("synth:droid:sess-droid-1:a-1", first.RecordId);
        Assert.Equal("droid", first.HarnessId);
        Assert.Equal(RecordOp.LlmInvoke, first.Op);
        Assert.Equal(RecordKind.Internal, first.Kind);
        Assert.Equal("droid:GLM-5.1", first.Name);
        Assert.Equal("GLM-5.1", first.Model);
        Assert.Equal(RecordStatus.Ok, first.Status);
        Assert.Equal("sess-droid-1", first.SessionKey);
        Assert.Null(first.ParentRecordId);
        Assert.Equal("synth:droid:sess-droid-1", first.TraceId);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 10, 0, 2, 500, TimeSpan.Zero), first.StartedAt);
        Assert.Equal(2500L, first.ReportedDurationMs);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 10, 0, 5, 0, TimeSpan.Zero), first.EndedAt);
        Assert.Null(first.ReportedInputTokens);
        Assert.Null(first.ReportedOutputTokens);
        Assert.Empty(first.Tokens);

        Assert.Equal(3, first.Parts.Count);
        Assert.Equal(ContentBucket.UserMessage, first.Parts[0].Bucket);
        Assert.Equal("Inspect the repo", first.Parts[0].PlainText);
        Assert.Equal("user", first.Parts[0].Role);
        Assert.Equal(ContentBucket.Reasoning, first.Parts[1].Bucket);
        Assert.Equal("Scanning files", first.Parts[1].PlainText);
        Assert.Equal("assistant", first.Parts[1].Role);
        Assert.Equal(ContentBucket.ConversationHistory, first.Parts[2].Bucket);
        Assert.Equal("First turn done.", first.Parts[2].PlainText);
        Assert.Equal("assistant", first.Parts[2].Role);

        CollectedRecord second = batch.Records[1];
        Assert.Equal("synth:droid:sess-droid-1:a-2", second.RecordId);
        Assert.Equal(4000L, second.ReportedDurationMs);
        Assert.Empty(second.Tokens);
        Assert.Null(second.ReportedInputTokens);
        Assert.Null(second.ReportedOutputTokens);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Equal(2, store.CountRecords());
        Assert.Equal(5, store.SessionModelUsages("sess-droid-1").Count);
        Assert.Empty(store.TokenCounts(spanId: first.RecordId));
        Assert.Empty(store.TokenCounts(spanId: second.RecordId));
    }

    [Fact]
    public void Map_OmitsAbsentChildRows_WhenCompanionSettingsOrOptionalMetadataAreAbsent()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session_start\"}",
            "{\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":\"Plain scalar prompt\"}}",
            "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"content\":\"Plain scalar reply\"}}",
        ]) + "\n";

        DroidSessionMapper mapper = new();
        CollectedBatch batch = mapper.Map(
            CreateUnit("/home/dev/.factory/sessions/repo/stem-only.jsonl", jsonl, companionText: null),
            BaselineUtc);

        Assert.Empty(batch.Problems);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("stem-only", session.SessionKey);
        Assert.Equal("stem-only", session.NativeSessionId);
        Assert.Null(session.Title);
        Assert.Null(session.Workspace);
        Assert.Empty(session.ReportedUsage);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("synth:droid:stem-only:msg-0", record.RecordId);
        Assert.Equal("droid:unknown", record.Name);
        Assert.Null(record.Model);
        Assert.Equal(BaselineUtc, record.StartedAt);
        Assert.Null(record.EndedAt);
        Assert.Null(record.ReportedDurationMs);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedCost);
        Assert.Null(record.Tool);
        Assert.Empty(record.SkillsInvoked);
        Assert.Null(record.Signals.CorrectionRule);
        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);

        Assert.Equal(2, record.Parts.Count);
        Assert.Equal(ContentBucket.UserMessage, record.Parts[0].Bucket);
        Assert.Equal("Plain scalar prompt", record.Parts[0].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, record.Parts[1].Bucket);
        Assert.Equal("Plain scalar reply", record.Parts[1].PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Empty(store.SessionModelUsages("stem-only"));
        Assert.Empty(store.TokenCounts(spanId: record.RecordId));
        StoredSessionModelUsage[] emptyUsages = [.. store.SessionModelUsages("stem-only")];
        Assert.Empty(emptyUsages);
        CollectedSession? loadedSession = store.GetSession("stem-only");
        Assert.NotNull(loadedSession);
        Assert.Null(loadedSession!.Title);
        Assert.Null(loadedSession.Workspace);
    }

    [Fact]
    public void Map_OmitsMissingTokenClasses_WhenSettingsOnlyReportsSubsetOfCounters()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session_start\",\"id\":\"sess-partial\",\"cwd\":\"/work/app\"}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}}",
        ]) + "\n";

        string settingsJson = """
            {
              "model": "claude-sonnet-4-5-20250929",
              "tokenUsage": {
                "inputTokens": 40,
                "outputTokens": 12
              }
            }
            """;

        DroidSessionMapper mapper = new();
        CollectedBatch batch = mapper.Map(
            CreateUnit("/home/dev/.factory/sessions/app/sess-partial.jsonl", jsonl, settingsJson),
            BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal(2, session.ReportedUsage.Count);
        Assert.DoesNotContain(session.ReportedUsage, static u => u.TokenClass == TokenClass.CacheRead);
        Assert.DoesNotContain(session.ReportedUsage, static u => u.TokenClass == TokenClass.CacheWrite);
        Assert.DoesNotContain(session.ReportedUsage, static u => u.TokenClass == TokenClass.Reasoning);

        Assert.Contains(new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Input, 40L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Output, 12L), session.ReportedUsage);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("claude-sonnet-4-5", record.Model);
        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedDurationMs);
        Assert.Null(record.EndedAt);
    }

    [Fact]
    public void Map_ReportsNoUsageAndNoRecordCounts_WhenCompanionHasNoTokenUsage()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session_start\",\"id\":\"sess-no-usage\",\"cwd\":\"/work/app\"}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"Plan the fix\"}}",
            "{\"type\":\"message\",\"id\":\"a-1\",\"timestamp\":\"2026-11-19T10:00:02.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Planned.\"}]}}",
            "{\"type\":\"message\",\"id\":\"a-2\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Applied.\"}]}}",
        ]) + "\n";

        string settingsJson = """
            {
              "model": "claude-sonnet-4-5-20250929"
            }
            """;

        DroidSessionMapper mapper = new();
        CollectedBatch batch = mapper.Map(
            CreateUnit("/home/dev/.factory/sessions/app/sess-no-usage.jsonl", jsonl, settingsJson),
            BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Empty(session.ReportedUsage);
        Assert.Equal(2, batch.Records.Count);
        Assert.All(batch.Records, static record =>
        {
            Assert.Equal("claude-sonnet-4-5", record.Model);
            Assert.Empty(record.Tokens);
            Assert.Null(record.ReportedInputTokens);
            Assert.Null(record.ReportedOutputTokens);
        });
    }

    [Fact]
    public void Map_FiltersSystemReminders_DetectsCorrection_AndMapsToolUseAndToolResultChildSpans()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session_start\",\"id\":\"sess-tools\",\"cwd\":\"/work/repo\"}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<system-reminder>ignore me</system-reminder>\"}]}}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"No, don't edit that file; run the tests instead\"}]}}",
            "{\"type\":\"message\",\"id\":\"a-thinking\",\"timestamp\":\"2026-11-19T10:00:06.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Planning tool call\"}]}}",
            "{\"type\":\"message\",\"id\":\"a-tools\",\"timestamp\":\"2026-11-19T10:00:08.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tu-exec\",\"name\":\"Execute\",\"input\":{\"command\":\"dotnet test\\necho done\"}},{\"type\":\"tool_use\",\"id\":\"tu-task\",\"name\":\"Task\",\"input\":{\"prompt\":\"inspect\"}},{\"type\":\"tool_use\",\"id\":\"tu-mcp\",\"name\":\"mcp__codegraph__explore\",\"input\":{\"query\":\"Droid\"}},{\"type\":\"tool_use\",\"id\":\"tu-skill\",\"name\":\"Skill\",\"input\":{\"skill\":\"csharp-dev\"}}]}}",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:09.500Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu-exec\",\"is_error\":true,\"content\":\"Build failed with exit code 1\"},{\"type\":\"tool_result\",\"tool_use_id\":\"tu-mcp\",\"is_error\":false,\"content\":\"Found symbol\"}]}}",
        ]) + "\n";

        DroidSessionMapper mapper = new();
        CollectedBatch batch = mapper.Map(
            CreateUnit("/home/dev/.factory/sessions/repo/sess-tools.jsonl", jsonl),
            BaselineUtc);

        Assert.Empty(batch.Problems);
        Assert.Equal(5, batch.Records.Count);

        CollectedRecord parent = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, parent.Op);
        Assert.Equal("synth:droid:sess-tools:a-tools", parent.RecordId);
        Assert.Equal(3000L, parent.ReportedDurationMs);
        Assert.Equal("prefix_negation", parent.Signals.CorrectionRule);
        Assert.Equal(["csharp-dev"], parent.SkillsInvoked);
        Assert.DoesNotContain(parent.Parts, static p => p.PlainText is not null && p.PlainText.Contains("system-reminder", StringComparison.Ordinal));
        Assert.Contains(parent.Parts, static p => p.Bucket == ContentBucket.UserMessage && p.PlainText == "No, don't edit that file; run the tests instead");
        Assert.Contains(parent.Parts, static p => p.Bucket == ContentBucket.Reasoning && p.PlainText == "Planning tool call");

        CollectedRecord bashChild = batch.Records[1];
        Assert.Equal(RecordOp.ToolInvoke, bashChild.Op);
        Assert.Equal(parent.RecordId, bashChild.ParentRecordId);
        Assert.Equal("Bash", bashChild.Name);
        Assert.Equal(RecordStatus.Error, bashChild.Status);
        Assert.Equal(1500L, bashChild.ReportedDurationMs);
        Assert.NotNull(bashChild.Tool);
        Assert.Equal("tu-exec", bashChild.Tool!.CallId);
        Assert.Equal("Bash", bashChild.Tool.ToolName);
        Assert.Null(bashChild.Tool.Input!.PlainText);
        Assert.Equal(Sha256Hex("{\"command\":\"dotnet test\\necho done\"}"), bashChild.Tool.Input.Sha256Fingerprint);
        Assert.False(bashChild.Tool.Succeeded);
        Assert.NotNull(bashChild.Tool.Error);
        Assert.Equal("Build failed with exit code 1", bashChild.Tool.Error!.Message);
        Assert.Equal("Build failed with exit code 1", bashChild.Tool.Result!.PlainText);
        CollectedContentPart bashResultPart = Assert.Single(bashChild.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, bashResultPart.Bucket);
        Assert.Equal("Build failed with exit code 1", bashResultPart.PlainText);

        CollectedRecord agentChild = batch.Records[2];
        Assert.Equal(RecordOp.ToolInvoke, agentChild.Op);
        Assert.Equal("Agent", agentChild.Name);
        Assert.Equal(RecordStatus.Unset, agentChild.Status);
        Assert.NotNull(agentChild.Tool);
        Assert.Null(agentChild.Tool!.Succeeded);
        Assert.Null(agentChild.Tool.Result);
        Assert.Empty(agentChild.Parts);

        CollectedRecord mcpChild = batch.Records[3];
        Assert.Equal(RecordOp.ToolInvoke, mcpChild.Op);
        Assert.Equal("mcp__codegraph__explore", mcpChild.Name);
        Assert.Equal(RecordStatus.Ok, mcpChild.Status);
        Assert.NotNull(mcpChild.Tool);
        Assert.Equal("codegraph", mcpChild.Tool!.McpServerName);
        Assert.Equal("explore", mcpChild.Tool.McpToolName);
        Assert.True(mcpChild.Tool.Succeeded);
        Assert.Null(mcpChild.Tool.Error);

        CollectedRecord skillChild = batch.Records[4];
        Assert.Equal(RecordOp.ToolInvoke, skillChild.Op);
        Assert.Equal("Skill", skillChild.Name);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(5, store.CountRecords());
    }

    [Fact]
    public void Map_WithholdsJsonDocumentsAndReportsMalformedJsonlAndSettingsAsWarnings()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session_start\",\"id\":\"sess-bad\",\"cwd\":\"/work/repo\"}",
            "{ malformed line",
            "42",
            "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"secret\\\":\\\"payload\\\"}\"}]}}",
            "{\"type\":\"message\",\"id\":\"a-1\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Handled safely.\"}]}}",
        ]) + "\n";

        DroidSessionMapper mapper = new();
        CollectedBatch batch = mapper.Map(
            CreateUnit("/home/dev/.factory/sessions/repo/sess-bad.jsonl", jsonl, companionText: "{broken settings"),
            BaselineUtc);

        Assert.Equal(3, batch.Problems.Count);
        Assert.All(batch.Problems, static p => Assert.Equal("warning", p.Severity));
        Assert.Contains(batch.Problems, static p => p.Code == DroidSessionMapper.MalformedLineCode);
        Assert.Contains(batch.Problems, static p => p.Code == DroidSessionMapper.MalformedSettingsCode);

        CollectedRecord record = Assert.Single(batch.Records);
        CollectedContentPart userPart = Assert.Single(record.Parts, static p => p.Bucket == ContentBucket.UserMessage);
        Assert.Null(userPart.PlainText);
        Assert.NotNull(userPart.Withheld);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords());
    }

    [Theory]
    [InlineData("custom:GLM-5.1-[Proxy]-0", "GLM-5.1")]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("gpt-5.2", "gpt-5.2")]
    [InlineData("custom:", null)]
    public void StripModelPrefix_NormalizesDroidModelWrappers(string rawModel, string? expected)
    {
        Assert.Equal(expected, DroidSessionMapper.StripModelPrefix(rawModel));
    }

    private static NativeSourceUnit CreateUnit(string sourceKey, string? rawText, string? companionText = null)
    {
        SourceUnit unit = new(
            HarnessId: "droid",
            SourceKey: sourceKey,
            ProviderId: "factory",
            ParserId: "droid-reader",
            ParserContractVersion: "1",
            Format: "droid-jsonl",
            SourceRootLabel: "repo",
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: rawText is null ? 0 : Encoding.UTF8.GetByteCount(rawText),
            CompanionKey: companionText is null ? null : sourceKey.Replace(".jsonl", ".settings.json", StringComparison.Ordinal));

        return new NativeSourceUnit(
            Unit: unit,
            RawText: rawText,
            SizeBytes: unit.SizeBytes ?? 0,
            LastWriteUtc: BaselineUtc,
            CompanionText: companionText);
    }

    private static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }
}
