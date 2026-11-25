using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.CopilotCli;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CopilotCliMapper"/> session shutdown usage snapshots
/// (<c>session.shutdown</c> <c>modelMetrics</c> into <c>session_model_usage</c>) and
/// workspace metadata merged from <c>session.context_changed</c> events.
/// </summary>
public class CopilotCliSessionUsageTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_MultipleShutdownSnapshots_ProduceOrderedDisjointSessionModelUsage()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-usage-1\",\"selectedModel\":\"gpt-5\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"data\":{\"messageId\":\"msg-1\",\"content\":\"First reply.\"}}",
            "{\"type\":\"session.shutdown\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:05:00.000Z\",\"data\":{\"sessionId\":\"sess-usage-1\",\"modelMetrics\":[{\"model\":\"gpt-5\",\"inputTokens\":100,\"outputTokens\":20,\"cacheReadTokens\":5,\"cacheWriteTokens\":2,\"reasoningTokens\":3},{\"model\":\"claude-sonnet-4.5\",\"inputTokens\":50,\"outputTokens\":10}]}}",
            "{\"type\":\"assistant.message\",\"id\":\"e4\",\"timestamp\":\"2026-11-19T10:06:00.000Z\",\"data\":{\"messageId\":\"msg-2\",\"content\":\"Second reply.\"}}",
            "{\"type\":\"session.shutdown\",\"id\":\"e5\",\"timestamp\":\"2026-11-19T10:07:00.000Z\",\"data\":{\"sessionId\":\"sess-usage-1\",\"modelMetrics\":[{\"model\":\"gpt-5\",\"inputTokens\":7,\"outputTokens\":1}]}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            CombineHome(".copilot/session-state/sess-usage-1/events.jsonl"),
            jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-usage-1", session.SessionKey);

        Assert.Equal(9, session.ReportedUsage.Count);

        IReadOnlyList<CollectedModelUsage> first = [.. session.ReportedUsage.Where(static u => u.SnapshotPosition == 0)];
        IReadOnlyList<CollectedModelUsage> second = [.. session.ReportedUsage.Where(static u => u.SnapshotPosition == 1)];
        Assert.Equal(7, first.Count);
        Assert.Equal(2, second.Count);

        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.Input, 93L), first);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.Output, 20L), first);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.CacheRead, 5L), first);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.CacheWrite, 2L), first);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.Reasoning, 3L), first);
        Assert.Contains(new CollectedModelUsage(0, "claude-sonnet-4.5", TokenClass.Input, 50L), first);
        Assert.Contains(new CollectedModelUsage(0, "claude-sonnet-4.5", TokenClass.Output, 10L), first);
        Assert.Contains(new CollectedModelUsage(1, "gpt-5", TokenClass.Input, 7L), second);
        Assert.Contains(new CollectedModelUsage(1, "gpt-5", TokenClass.Output, 1L), second);

        // Disjoint classes: no (snapshot, model, token class) triple repeats.
        Assert.Equal(
            session.ReportedUsage.Count,
            session.ReportedUsage.Select(static u => (u.SnapshotPosition, u.Model, u.TokenClass)).Distinct().Count());

        // Shutdown events emit usage snapshots, not telemetry records.
        Assert.Equal(2, batch.Records.Count);
        Assert.DoesNotContain(batch.Records, static r => r.Op == RecordOp.AgentInvoke);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Equal(9, store.SessionModelUsages("sess-usage-1").Count);
        StoredSessionModelUsage storedFirst = store.SessionModelUsages("sess-usage-1")[0];
        Assert.Equal(0, storedFirst.Usage.SnapshotPosition);
    }

    // modelMetrics inputTokens is cache-inclusive (input + cache read + cache write): the legacy
    // provider subtracted both cache classes before costing (copilot.ts:959-986 at fc80ddf9^, comment at
    // copilot.ts:204-205), and CopilotCliModelUsage documents InputTokens as the fresh count. The
    // stored Input row is therefore fresh input, so input plus cache is not counted twice.
    [Fact]
    public void Map_ShutdownInputIsCacheInclusive_StoresFreshInput()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-fresh-input\",\"selectedModel\":\"gpt-5\"}}",
            "{\"type\":\"session.shutdown\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:05:00.000Z\",\"data\":{\"sessionId\":\"sess-fresh-input\",\"modelMetrics\":[{\"model\":\"gpt-5\",\"inputTokens\":100,\"outputTokens\":5,\"cacheReadTokens\":30,\"cacheWriteTokens\":20},{\"model\":\"claude-sonnet-4.5\",\"inputTokens\":40,\"outputTokens\":2}]}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            CombineHome(".copilot/session-state/sess-fresh-input/events.jsonl"),
            jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.Input, 50L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.CacheRead, 30L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.CacheWrite, 20L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "claude-sonnet-4.5", TokenClass.Input, 40L), session.ReportedUsage);
    }

    // Cache classes larger than the reported input cannot come from one cache-inclusive count, so the
    // snapshot is inconsistent: the fresh input is not measured (no Input row for that model), while the
    // cache counts the CLI did report are kept.
    [Fact]
    public void Map_ShutdownCacheExceedsInput_StoresNoFreshInputForThatModel()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-bad-snapshot\",\"selectedModel\":\"gpt-5\"}}",
            "{\"type\":\"session.shutdown\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:05:00.000Z\",\"data\":{\"sessionId\":\"sess-bad-snapshot\",\"modelMetrics\":[{\"model\":\"gpt-5\",\"inputTokens\":50,\"outputTokens\":5,\"cacheReadTokens\":60,\"cacheWriteTokens\":0}]}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            CombineHome(".copilot/session-state/sess-bad-snapshot/events.jsonl"),
            jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.DoesNotContain(session.ReportedUsage, static u => u.TokenClass == TokenClass.Input);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.CacheRead, 60L), session.ReportedUsage);
        Assert.Contains(new CollectedModelUsage(0, "gpt-5", TokenClass.Output, 5L), session.ReportedUsage);
    }

    [Fact]
    public void Map_NoShutdown_YieldsNoSessionModelUsage()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-no-shutdown\",\"selectedModel\":\"gpt-5\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"data\":{\"messageId\":\"msg-1\",\"content\":\"Reply.\",\"inputTokens\":12,\"outputTokens\":4}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            CombineHome(".copilot/session-state/sess-no-shutdown/events.jsonl"),
            jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Empty(session.ReportedUsage);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Empty(store.SessionModelUsages("sess-no-shutdown"));
    }

    [Fact]
    public void Map_ContextChanged_PopulatesWorkspaceRepositoryAndBranch()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-ctx-1\",\"selectedModel\":\"gpt-5\",\"context\":{\"cwd\":\"/work/repo-a\",\"repository\":\"owner/repo-a\",\"branch\":\"main\"}}}",
            "{\"type\":\"assistant.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"data\":{\"messageId\":\"msg-1\",\"content\":\"Reply.\"}}",
            "{\"type\":\"session.context_changed\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:01:00.000Z\",\"data\":{\"context\":{\"cwd\":\"/work/repo-b\",\"repository\":\"owner/repo-b\",\"branch\":\"feat/copilot-cli\"}}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(
            CombineHome(".copilot/session-state/sess-ctx-1/events.jsonl"),
            jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/work/repo-b", session.Workspace.WorkingDirectory);
        Assert.Equal("owner/repo-b", session.Workspace.Repository);
        Assert.Equal("feat/copilot-cli", session.Workspace.Branch);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Empty(store.SessionModelUsages("sess-ctx-1"));
    }

    private static NativeSourceUnit MakeNativeUnit(string path, string rawText)
    {
        SourceUnit sourceUnit = new(
            HarnessId: "copilot-cli",
            SourceKey: Path.GetFullPath(path),
            ProviderId: "copilot",
            ParserId: "copilot-cli-reader",
            ParserContractVersion: "1",
            Format: "copilot-cli-jsonl",
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

    private static string CombineHome(string relative)
    {
        string homeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "fake-home");
        return Path.Combine(homeRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
