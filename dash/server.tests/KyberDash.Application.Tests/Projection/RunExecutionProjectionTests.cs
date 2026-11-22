using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Verifies the relational <see cref="CanonSessionProjectorEngine"/> run grouping and execution
/// linkage projection: every projected session yields one typed <c>run*</c> row
/// (<c>derived</c>/<c>session_fallback</c>) and one root <c>execution*</c> row whose parent linkage
/// is the nullable typed <c>execution_parent</c> row, with no <c>parent_linkage_json</c> or
/// <c>payload</c> columns anywhere.
/// </summary>
/// <remarks>
/// Explicit run identifiers and parent-session attributes are not observable through
/// <see cref="IRelationalStoreReader"/> (a <see cref="CollectedRecord"/> carries no raw attribute
/// bag), so grouping is always derived and executions are roots carrying their session link,
/// agent name, and bounds as typed rows.
/// </remarks>
public class RunExecutionProjectionTests
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A projected session yields one typed run row and one root execution row with matching
    /// label, working directory, and bounds, and no parent-linkage child row.
    /// </summary>
    [Fact]
    public void ProjectIncremental_WritesTypedRunAndExecutionRows()
    {
        InMemoryRelationalStore store = new();
        CollectedRecord turn = Turn("turn-1", sessionKey: "sess-run", minuteOffset: 0, inputTokens: 500, outputTokens: 60);
        WriteSession(
            store,
            "sess-run",
            [turn],
            title: "Fix the login bug",
            workspace: new CollectedWorkspace(WorkingDirectory: "/repo/app", Repository: "myrepo", Branch: "main"));

        CanonSessionProjectorEngine engine = NewEngine(store);
        ProjectionPassReport report = engine.ProjectIncremental();

        Assert.Equal(1, report.Built);
        StoredSessionProjection? projection = ((IProjectionStore)store).GetSessionProjection("sess-run");
        Assert.NotNull(projection);

        StoredRun? run = ((IProjectionStore)store).GetRun("derived:claude-code:sess-run");
        Assert.NotNull(run);
        Assert.Equal("claude-code", run!.HarnessId);
        Assert.Equal("derived", run.GroupingBasis);
        Assert.Equal("session_fallback", run.GroupingRule);
        Assert.Equal(1, run.ExecutionCount);
        Assert.Equal(projection!.Label, run.Label);
        Assert.Equal("/repo/app", run.WorkingDirectory);
        Assert.Equal(projection!.StartedAtUtc, run.StartedAtUtc);
        Assert.Equal(projection!.EndedAtUtc, run.EndedAtUtc);

        Assert.Equal([run], ((IProjectionStore)store).ListRuns());
        Assert.Single(((IProjectionStore)store).ListRuns("claude-code"));
        Assert.Empty(((IProjectionStore)store).ListRuns("codex"));

        StoredExecution? execution = ((IProjectionStore)store).GetExecution("exec:sess-run");
        Assert.NotNull(execution);
        Assert.Equal(run.RunId, execution!.RunId);
        Assert.Equal("claude-code", execution.HarnessId);
        Assert.True(execution.IsRoot);
        Assert.Equal("sess-run", execution.SessionId);
        Assert.Null(execution.ParentExecutionId);
        Assert.Null(execution.AgentName);
        Assert.Equal(projection!.StartedAtUtc, execution.StartedAtUtc);
        Assert.Equal(projection!.EndedAtUtc, execution.EndedAtUtc);

        Assert.Equal([execution], ((IProjectionStore)store).ListExecutionsForRun(run.RunId));
        Assert.Equal([execution], ((IProjectionStore)store).ListExecutionsByHarness("claude-code"));

        Assert.Equal(1, store.CountRows("run"));
        Assert.Equal(1, store.CountRows("run_label"));
        Assert.Equal(1, store.CountRows("run_working_directory"));
        Assert.Equal(1, store.CountRows("run_bounds"));
        Assert.Equal(1, store.CountRows("execution"));
        Assert.Equal(1, store.CountRows("execution_session"));
        Assert.Equal(0, store.CountRows("execution_parent"));
        Assert.Equal(0, store.CountRows("execution_agent"));
        Assert.Equal(1, store.CountRows("execution_bounds"));
    }

    /// <summary>
    /// Sessions without a workspace working directory fall back to the repository name, and a
    /// session with neither stores no working-directory child row; a repeat pass upserts without
    /// duplicating run or execution rows.
    /// </summary>
    [Fact]
    public void ProjectIncremental_FallsBackToRepositoryAndUpsertsIdempotently()
    {
        InMemoryRelationalStore store = new();
        WriteSession(
            store,
            "sess-a",
            [Turn("turn-a", sessionKey: "sess-a", minuteOffset: 0, inputTokens: 300, outputTokens: 30)],
            workspace: new CollectedWorkspace(WorkingDirectory: null, Repository: "repo-a", Branch: null));
        WriteSession(
            store,
            "sess-b",
            [Turn("turn-b", sessionKey: "sess-b", minuteOffset: 0, inputTokens: 400, outputTokens: 40)]);

        CanonSessionProjectorEngine engine = NewEngine(store);
        Assert.Equal(2, engine.ProjectIncremental().Built);

        Assert.Equal("repo-a", ((IProjectionStore)store).GetRun("derived:claude-code:sess-a")!.WorkingDirectory);
        Assert.Null(((IProjectionStore)store).GetRun("derived:claude-code:sess-b")!.WorkingDirectory);
        Assert.Equal(1, store.CountRows("run_working_directory"));

        Assert.Equal(2, engine.ProjectIncremental().Built);
        Assert.Equal(2, store.CountRows("run"));
        Assert.Equal(2, ((IProjectionStore)store).ListRuns().Count);
        Assert.Equal(2, store.CountRows("execution"));
    }

    /// <summary>
    /// An agent invocation on the session's first record flows onto the execution as a typed
    /// <c>execution_agent</c> row.
    /// </summary>
    [Fact]
    public void ProjectIncremental_WritesTypedAgentNameOnExecution()
    {
        InMemoryRelationalStore store = new();
        CollectedRecord invoke = Turn(
            "agent-1",
            sessionKey: "sess-agent",
            minuteOffset: 0,
            name: "invoke_agent research-subagent",
            op: RecordOp.AgentInvoke,
            agent: new CollectedAgentInvocation("research-subagent", SpawningCallId: null, Succeeded: true, Error: null));
        CollectedRecord turn = Turn("turn-1", sessionKey: "sess-agent", minuteOffset: 1, inputTokens: 300, outputTokens: 30, parentSpanId: "agent-1");
        WriteSession(store, "sess-agent", [invoke, turn]);

        CanonSessionProjectorEngine engine = NewEngine(store);
        Assert.Equal(1, engine.ProjectIncremental().Built);

        StoredExecution? execution = ((IProjectionStore)store).GetExecution("exec:sess-agent");
        Assert.NotNull(execution);
        Assert.Equal("research-subagent", execution!.AgentName);
        Assert.Equal(1, store.CountRows("execution_agent"));
    }

    /// <summary>
    /// A later pass prunes the run and execution rows whose sessions disappeared from the
    /// relational store.
    /// </summary>
    [Fact]
    public void ProjectIncremental_PrunesRunsAndExecutionsForRemovedSessions()
    {
        InMemoryRelationalStore store = new();
        WriteSession(store, "sess-keep", [Turn("turn-a", sessionKey: "sess-keep", minuteOffset: 0, inputTokens: 300, outputTokens: 30, harnessId: "claude-code")], harnessId: "claude-code");
        WriteSession(store, "sess-gone", [Turn("turn-b", sessionKey: "sess-gone", minuteOffset: 0, inputTokens: 500, outputTokens: 50, harnessId: "codex")], harnessId: "codex");

        CanonSessionProjectorEngine engine = NewEngine(store);
        Assert.Equal(2, engine.ProjectIncremental().Built);
        Assert.NotNull(((IProjectionStore)store).GetRun("derived:codex:sess-gone"));

        StoredSource goneSource = store.Sources("codex").Single();
        ((IRelationalStoreWriter)store).DeleteSource(goneSource.SourceId);

        Assert.Equal(1, engine.ProjectIncremental().Built);
        Assert.Single(((IProjectionStore)store).ListRuns());
        Assert.Null(((IProjectionStore)store).GetRun("derived:codex:sess-gone"));
        Assert.Null(((IProjectionStore)store).GetExecution("exec:sess-gone"));
        Assert.Empty(((IProjectionStore)store).ListExecutionsForRun("derived:codex:sess-gone"));
        Assert.Equal(1, store.CountRows("run"));
        Assert.Equal(1, store.CountRows("execution"));
    }

    /// <summary>
    /// The persisted <c>run*</c> and <c>execution*</c> tables expose no JSON linkage or payload
    /// columns: parent linkage is only the nullable typed <c>execution_parent</c> row.
    /// </summary>
    [Fact]
    public void ProjectIncremental_StoresNoJsonLinkageOrPayloadColumns()
    {
        InMemoryRelationalStore store = new();
        WriteSession(store, "sess-typed", [Turn("turn-1", sessionKey: "sess-typed", minuteOffset: 0, inputTokens: 300, outputTokens: 30)]);

        CanonSessionProjectorEngine engine = NewEngine(store);
        Assert.Equal(1, engine.ProjectIncremental().Built);

        foreach (string tableName in new[] { "run", "run_label", "run_working_directory", "run_bounds", "execution", "execution_session", "execution_parent", "execution_agent", "execution_bounds" })
        {
            foreach (StoredFieldRow row in store.GetTableRows(tableName))
            {
                Assert.DoesNotContain(row.Cells, static cell => cell.ColumnName is "parent_linkage_json" or "payload");
            }
        }
    }

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository());

    private static void WriteSession(
        InMemoryRelationalStore store,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code",
        string? title = null,
        CollectedWorkspace? workspace = null)
    {
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: workspace,
                    Title: title,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        long inputTokens = 0,
        long cacheRead = 0,
        long outputTokens = 0,
        string? parentSpanId = null,
        string name = "chat.turn",
        RecordOp op = RecordOp.LlmInvoke,
        RecordStatus status = RecordStatus.Ok,
        string? model = "claude-sonnet-4-6",
        IReadOnlyList<CollectedContentPart>? parts = null,
        IReadOnlyList<CollectedToolDefinition>? definitions = null,
        long? declaredContextWindow = null,
        CollectedAgentInvocation? agent = null,
        string harnessId = "claude-code")
    {
        List<CollectedTokenCount> tokens =
        [
            new CollectedTokenCount(TokenClass.Input, inputTokens),
            new CollectedTokenCount(TokenClass.CacheRead, cacheRead),
            new CollectedTokenCount(TokenClass.CacheWrite, 0),
            new CollectedTokenCount(TokenClass.Output, outputTokens),
        ];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: op,
            Kind: RecordKind.Client,
            Name: name,
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: status,
            SessionKey: sessionKey,
            ParentRecordId: parentSpanId,
            TraceId: null,
            Model: model,
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: null,
            Parts: parts ?? [],
            Tool: null,
            Agent: agent,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: definitions ?? [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: declaredContextWindow),
            ReportedInputTokens: inputTokens + cacheRead,
            ReportedOutputTokens: outputTokens);
    }
}
