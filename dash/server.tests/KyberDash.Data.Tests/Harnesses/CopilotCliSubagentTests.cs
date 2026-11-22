using System.Globalization;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.CopilotCli;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CopilotCliMapper"/> sub-agent correlation: <c>subagent.started</c>,
/// <c>subagent.completed</c> and <c>subagent.failed</c> map into <c>agent.invoke</c> DTOs parented
/// to the spawning <c>tool.invoke</c> (DD-8), without inferring links by time window.
/// </summary>
public class CopilotCliSubagentTests
{
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_SubagentStartedAndCompleted_EmitsAgentInvokeParentedToSpawningToolCall()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-sub-1","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-1","content":"Delegating research.","toolRequests":[{"toolCallId":"call_delegate","name":"delegate_task","arguments":{"task":"research"}}]}}""",
                """{"type":"tool.execution_start","id":"e3","parentId":"e2","timestamp":"2026-11-19T10:00:01.100Z","data":{"toolCallId":"call_delegate","toolName":"delegate_task"}}""",
                """{"type":"tool.execution_complete","id":"e4","parentId":"e3","timestamp":"2026-11-19T10:00:01.300Z","data":{"toolCallId":"call_delegate","toolName":"delegate_task","success":true,"result":{"content":"delegated"}}}""",
                """{"type":"subagent.started","id":"e5","timestamp":"2026-11-19T10:00:01.350Z","data":{"agentName":"researcher","toolCallId":"call_delegate"}}""",
                """{"type":"subagent.completed","id":"e6","timestamp":"2026-11-19T10:00:02.000Z","data":{"agentName":"researcher","toolCallId":"call_delegate"}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-sub-1", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);

        CollectedRecord toolRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        CollectedRecord agentRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.AgentInvoke);

        Assert.Equal("copilot-cli", agentRecord.HarnessId);
        Assert.Equal(RecordKind.Internal, agentRecord.Kind);
        Assert.Equal("researcher", agentRecord.Name);
        Assert.Equal(toolRecord.RecordId, agentRecord.ParentRecordId);
        Assert.Equal(toolRecord.SessionKey, agentRecord.SessionKey);
        Assert.Equal(toolRecord.TraceId, agentRecord.TraceId);
        Assert.Equal(RecordStatus.Ok, agentRecord.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:01.350Z", CultureInfo.InvariantCulture), agentRecord.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:02.000Z", CultureInfo.InvariantCulture), agentRecord.EndedAt);
        Assert.Equal(650L, agentRecord.ReportedDurationMs);
        Assert.Null(agentRecord.Tool);
        Assert.Empty(agentRecord.Parts);

        Assert.NotNull(agentRecord.Agent);
        Assert.Equal("researcher", agentRecord.Agent.AgentName);
        Assert.Equal("call_delegate", agentRecord.Agent.SpawningCallId);
        Assert.True(agentRecord.Agent.Succeeded);
        Assert.Null(agentRecord.Agent.Error);

        // The spawn join key is the tool call id; the sub-agent run carries no
        // proven agent-instance join, so none is recorded.
        Assert.Null(agentRecord.AgentInstanceId);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CollectedRecord? storedAgent = store.GetRecord(agentRecord.RecordId);
        Assert.NotNull(storedAgent);
        Assert.Equal(RecordOp.AgentInvoke, storedAgent.Op);
        Assert.Equal(toolRecord.RecordId, storedAgent.ParentRecordId);

        StoredAgentInvocation storedInvocation = Assert.Single(store.AgentInvocations(spanId: agentRecord.RecordId));
        Assert.Equal(agentRecord.RecordId, storedInvocation.SpanId);
        Assert.Equal("researcher", storedInvocation.Invocation.AgentName);
        Assert.Equal("call_delegate", storedInvocation.Invocation.SpawningCallId);
        Assert.True(storedInvocation.Invocation.Succeeded);
    }

    [Fact]
    public void Map_SubagentStartedTwice_YieldsDeterministicAgentRecordIds()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-sub-det","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-1","content":"Delegating.","toolRequests":[{"toolCallId":"call_det","name":"delegate_task"}]}}""",
                """{"type":"tool.execution_start","id":"e3","timestamp":"2026-11-19T10:00:01.100Z","data":{"toolCallId":"call_det","toolName":"delegate_task"}}""",
                """{"type":"subagent.started","id":"e4","timestamp":"2026-11-19T10:00:01.150Z","data":{"agentName":"researcher","toolCallId":"call_det"}}""",
                """{"type":"subagent.completed","id":"e5","timestamp":"2026-11-19T10:00:02.000Z","data":{"agentName":"researcher","toolCallId":"call_det"}}""",
            ]) + "\n";

        CollectedBatch first = new CopilotCliMapper().Map(MakeUnit("sess-sub-det", jsonl), BaselineUtc);
        CollectedBatch second = new CopilotCliMapper().Map(MakeUnit("sess-sub-det", jsonl), BaselineUtc);

        string firstId = Assert.Single(first.Records, static r => r.Op == RecordOp.AgentInvoke).RecordId;
        string secondId = Assert.Single(second.Records, static r => r.Op == RecordOp.AgentInvoke).RecordId;
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void Map_SubagentFailed_SetsErrorOutcomeAndSignals()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-sub-fail","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-1","content":"Delegating risky work.","toolRequests":[{"toolCallId":"call_risky","name":"delegate_task"}]}}""",
                """{"type":"tool.execution_start","id":"e3","timestamp":"2026-11-19T10:00:01.100Z","data":{"toolCallId":"call_risky","toolName":"delegate_task"}}""",
                """{"type":"tool.execution_complete","id":"e4","timestamp":"2026-11-19T10:00:03.000Z","data":{"toolCallId":"call_risky","toolName":"delegate_task","success":true,"result":{"content":"done"}}}""",
                """{"type":"subagent.started","id":"e5","timestamp":"2026-11-19T10:00:01.150Z","data":{"agentName":"risky-worker","toolCallId":"call_risky"}}""",
                """{"type":"subagent.failed","id":"e6","timestamp":"2026-11-19T10:00:02.500Z","data":{"agentName":"risky-worker","toolCallId":"call_risky","error":{"message":"Subagent timed out"}}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-sub-fail", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        CollectedRecord toolRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        CollectedRecord agentRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.AgentInvoke);

        Assert.Equal(toolRecord.RecordId, agentRecord.ParentRecordId);
        Assert.Equal(RecordStatus.Error, agentRecord.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:02.500Z", CultureInfo.InvariantCulture), agentRecord.EndedAt);
        Assert.NotNull(agentRecord.Agent);
        Assert.Equal("risky-worker", agentRecord.Agent.AgentName);
        Assert.Equal("call_risky", agentRecord.Agent.SpawningCallId);
        Assert.False(agentRecord.Agent.Succeeded);
        Assert.Equal("Subagent timed out", agentRecord.Agent.Error);
        Assert.Equal("Subagent timed out", agentRecord.Signals.ErrorMessage);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        StoredAgentInvocation storedInvocation = Assert.Single(store.AgentInvocations(spanId: agentRecord.RecordId));
        Assert.False(storedInvocation.Invocation.Succeeded);
        Assert.Equal("Subagent timed out", storedInvocation.Invocation.Error);
    }

    [Fact]
    public void Map_SubagentRunWithoutTerminalEvent_LeavesOutcomeUnset()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-sub-open","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-1","content":"Delegating.","toolRequests":[{"toolCallId":"call_open","name":"delegate_task"}]}}""",
                """{"type":"tool.execution_start","id":"e3","timestamp":"2026-11-19T10:00:01.100Z","data":{"toolCallId":"call_open","toolName":"delegate_task"}}""",
                """{"type":"subagent.started","id":"e4","timestamp":"2026-11-19T10:00:01.150Z","data":{"agentName":"researcher","toolCallId":"call_open"}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-sub-open", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        CollectedRecord toolRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        CollectedRecord agentRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.AgentInvoke);

        Assert.Equal(toolRecord.RecordId, agentRecord.ParentRecordId);
        Assert.Equal(RecordStatus.Unset, agentRecord.Status);
        Assert.Null(agentRecord.EndedAt);
        Assert.Null(agentRecord.ReportedDurationMs);
        Assert.NotNull(agentRecord.Agent);
        Assert.Equal("researcher", agentRecord.Agent.AgentName);
        Assert.Null(agentRecord.Agent.Succeeded);
        Assert.Null(agentRecord.Agent.Error);
    }

    [Fact]
    public void Map_SubagentEventsWithoutProvenJoin_EmitNoAgentInvokeAndInferNoLinks()
    {
        string jsonl = string.Join(
            '\n',
            [
                """{"type":"session.start","id":"e1","timestamp":"2026-11-19T10:00:00.000Z","data":{"sessionId":"sess-sub-orphan","selectedModel":"gpt-5"}}""",
                """{"type":"assistant.message","id":"e2","timestamp":"2026-11-19T10:00:01.000Z","data":{"messageId":"msg-root","content":"Root turn."}}""",
                """{"type":"subagent.selected","id":"e3","timestamp":"2026-11-19T10:00:01.500Z","data":{"agentName":"researcher"}}""",
                """{"type":"subagent.started","id":"e4","timestamp":"2026-11-19T10:00:02.000Z","data":{"agentName":"researcher","toolCallId":"call_missing"}}""",
                """{"type":"assistant.message","id":"e5","agentId":"researcher","timestamp":"2026-11-19T10:00:02.500Z","data":{"messageId":"msg-sub","content":"Subagent turn."}}""",
                """{"type":"subagent.completed","id":"e6","timestamp":"2026-11-19T10:00:03.000Z","data":{"agentName":"researcher","toolCallId":"call_never_started"}}""",
            ]) + "\n";

        NativeSourceUnit unit = MakeUnit("sess-sub-orphan", jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        // No spawning tool.invoke exists for either toolCallId, and the orphan
        // completion has no started run: DD-8 forbids inventing either link.
        Assert.DoesNotContain(batch.Records, static r => r.Op == RecordOp.AgentInvoke);
        Assert.DoesNotContain(batch.Records, static r => r.Op == RecordOp.ToolInvoke);

        List<CollectedRecord> llmRecords = [.. batch.Records.Where(static r => r.Op == RecordOp.LlmInvoke)];
        Assert.Equal(2, llmRecords.Count);

        CollectedRecord subagentTurn = Assert.Single(llmRecords, static r => r.AgentInstanceId == "researcher");
        Assert.Null(subagentTurn.ParentRecordId);

        CollectedRecord rootTurn = Assert.Single(llmRecords, static r => r.AgentInstanceId is null);
        Assert.Null(rootTurn.ParentRecordId);
    }

    private static NativeSourceUnit MakeUnit(string sessionId, string rawText)
    {
        string path = Path.Combine(
            Path.GetPathRoot(Environment.CurrentDirectory) ?? "/",
            "fake-home",
            ".copilot",
            "session-state",
            sessionId,
            "events.jsonl");

        SourceUnit sourceUnit = new(
            HarnessId: "copilot-cli",
            SourceKey: Path.GetFullPath(path),
            ProviderId: "copilot",
            ParserId: "copilot-cli-reader",
            ParserContractVersion: "1",
            Format: "copilot-cli-jsonl",
            SourceRootLabel: sessionId,
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
