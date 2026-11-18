using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>
/// Replays a Copilot CLI conductor run that dispatched its workers with <c>task</c> in <c>mode: "background"</c>.
/// The tool returns at once with only the agent's id, so that result is not the worker's return: the return is the
/// <c>subagentStop</c> event that ends each of the agent's turns. Payload shapes are trimmed from a live run on
/// Copilot CLI 1.0.112.
/// </summary>
public sealed class CopilotBackgroundReturnTests : IDisposable
{
    private const string Session = "aa1cf341-f8f2-4674-8050-1ec54f6dee55";
    private const string BackendAgent = "9a30af38-0e61-4a61-8785-43b058ab723a";
    private const string FrontendAgent = "9b059343-20dd-4012-a22b-041605f7a556";
    private const string Plan = "docs/plans/cards.md";

    private const string PassDigest =
        "Discovery: codegraph 6, docs 7, direct reads 3.\n\n" +
        "STATUS: PASS\n" +
        "FILES: src/JobStatusSummaryDto.cs\n" +
        "TESTS: total=49 failed=0 passed=49\n" +
        "COMMIT: 4fd4083\n" +
        "TASK_GATE: PASS — agent=dev-medium gates=1 failed=none skipped=0 report=.agent_scratchpad/t1/task-gate.json runs=4";

    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly TempDirectory _home = new();
    private readonly RecordingProvider _provider = new();

    public CopilotBackgroundReturnTests()
    {
        Directory.CreateDirectory(Path.Combine(_probe.Root, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_probe.Root, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n  provider:\n    kind: none\n");
    }

    public void Dispose()
    {
        _probe.Dispose();
        _home.Dispose();
    }

    [Fact]
    public void ABackgroundStart_RecordsNoReturn()
    {
        Run(Pre("dev-medium", "T1"));

        string stdout = Run(BackgroundStart("dev-medium", "T1", BackendAgent));

        Assert.Empty(stdout);
        Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Pre);
        Assert.DoesNotContain(Ledger(), e => e.Phase == ArbiterLedgerPhases.Post);
    }

    [Fact]
    public void ASyncDispatchMovedToTheBackground_RecordsNoReturnUntilItsTurnEnds()
    {
        Run(Pre("dev-medium", "T1"));

        Run(MovedToBackground("dev-medium", "T1", BackendAgent));
        Assert.DoesNotContain(Ledger(), e => e.Phase == ArbiterLedgerPhases.Post);

        Run(Stop(BackendAgent, "dev-medium", PassDigest));
        Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Post);
    }

    [Fact]
    public void TheEndOfABackgroundAgentsTurn_IsRecordedAsItsDispatchsReturn_AndTheReturnRulesRun()
    {
        Run(Pre("dev-medium", "T1"));
        Run(BackgroundStart("dev-medium", "T1", BackendAgent));

        Run(Stop(BackendAgent, "dev-medium", PassDigest));

        ArbiterLedgerEvent pre = Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Pre);
        ArbiterLedgerEvent post = Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Post);
        Assert.Equal(pre.Id, post.PreId);
        Assert.Equal("delegate.returned", post.Trigger);
        Assert.Equal("PASS", post.Returns?.PlannerStatus);
        Assert.Equal("PASS", post.Returns?.TaskGate);
        ArbiterDecisionRecord decision = Assert.Single(Decisions(), d => d.Trigger == "delegate.returned");
        Assert.Contains(decision.Rules, rule => rule.Id == "KW-ARB-GATE-REPORT-001");
    }

    [Fact]
    public void TheSameTurnDeliveredTwice_IsRecordedOnce()
    {
        Run(Pre("dev-medium", "T1"));
        Run(BackgroundStart("dev-medium", "T1", BackendAgent));

        Run(Stop(BackendAgent, "dev-medium", PassDigest));
        Run(Stop(BackendAgent, "dev-medium", PassDigest));

        Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Post);
    }

    [Fact]
    public void AReworkTurnOnTheSameAgent_RecordsANewReturnForTheSameTask()
    {
        Run(Pre("dev-medium", "T1"));
        Run(BackgroundStart("dev-medium", "T1", BackendAgent));
        Run(Stop(BackendAgent, "dev-medium", "Blocked on the migration.\n\nSTATUS: BLOCKED\nTASK_GATE: FAIL — agent=dev-medium gates=1 failed=build"));

        Run(Stop(BackendAgent, "dev-medium", PassDigest));

        ArbiterLedgerEvent pre = Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Pre);
        List<ArbiterLedgerEvent> posts = [.. Ledger().Where(e => e.Phase == ArbiterLedgerPhases.Post)];
        Assert.Equal(2, posts.Count);
        Assert.All(posts, post => Assert.Equal(pre.Id, post.PreId));
        Assert.Equal(["BLOCKED", "PASS"], posts.Select(post => post.Returns?.PlannerStatus));
    }

    [Fact]
    public void TheEndOfATurnOfAnAgentWithNoRecordedDispatch_RecordsNothingAndWritesNothing()
    {
        string stdout = Run(Stop("70213a24-9085-4edf-a169-44bd0cdfbffc", "research-agent", "SYNC DONE"));

        Assert.Empty(stdout);
        Assert.Empty(Ledger());
    }

    [Fact]
    public void ASynchronousDispatch_IsRecordedOnceFromItsToolResult_ThoughItsTurnEndAlsoFires()
    {
        Run(Pre("dev-medium", "T1"));

        // Copilot fires subagentStop for a synchronous agent too, before the task's postToolUse.
        Run(Stop(BackendAgent, "dev-medium", PassDigest));
        Run(SyncReturn("dev-medium", "T1", BackendAgent, PassDigest));

        ArbiterLedgerEvent pre = Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Pre);
        ArbiterLedgerEvent post = Assert.Single(Ledger(), e => e.Phase == ArbiterLedgerPhases.Post);
        Assert.Equal(pre.Id, post.PreId);
        Assert.Equal("PASS", post.Returns?.TaskGate);
    }

    [Fact]
    public void ATaskWhoseDependenciesReturnedPassingDigestsInTheBackground_IsReadyWithoutAskingTheModel()
    {
        Run(Pre("dev-medium", "T1"));
        Run(Pre("dev-medium", "T2"));
        Run(BackgroundStart("dev-medium", "T1", BackendAgent));
        Run(BackgroundStart("dev-medium", "T2", FrontendAgent));
        Run(Stop(FrontendAgent, "dev-medium", PassDigest.Replace("dev-medium", "dev-medium", StringComparison.Ordinal)));
        Run(Stop(BackendAgent, "dev-medium", PassDigest));
        _provider.Asked.Clear();

        Run(Pre("dev-medium", "T3"));

        ArbiterDecisionRecord decision = Decisions().Last(d => d.Trigger == "delegate");
        ArbiterDecisionRule ready = Assert.Single(decision.Rules, rule => rule.Id == "KW-ARB-READY-001");
        Assert.Equal("ready", ready.Answer);
        Assert.Equal(0, ready.Step);
        Assert.DoesNotContain("KW-ARB-READY-001", _provider.Asked);
    }

    [Fact]
    public void ATaskWhoseDependencyIsStillRunningInTheBackground_IsNotReadyAtStepZero()
    {
        Run(Pre("dev-medium", "T1"));
        Run(Pre("dev-medium", "T2"));
        Run(BackgroundStart("dev-medium", "T1", BackendAgent));
        Run(BackgroundStart("dev-medium", "T2", FrontendAgent));
        Run(Stop(BackendAgent, "dev-medium", PassDigest));

        Run(Pre("dev-medium", "T3"));

        ArbiterDecisionRecord decision = Decisions().Last(d => d.Trigger == "delegate");
        Assert.DoesNotContain(decision.Rules, rule => rule.Id == "KW-ARB-READY-001" && rule.Answer == "ready");
    }

    [Fact]
    public void ANoteOnABackgroundReturn_IsHandedToItsDispatcherOnce_OnItsNextTaskResult()
    {
        Run(Pre("dev-medium", "T1"));
        Run(BackgroundStart("dev-medium", "T1", BackendAgent));
        IBackgroundDispatchStore store = ArbiterTestServices.Get<IBackgroundDispatchStore>();
        BackgroundDispatchRecord record = Assert.Single(store.ForSession(ArbiterDirectory, Session));
        Assert.Equal("unidentified", record.Dispatch.Caller);
        store.Save(ArbiterDirectory, record with { PendingNote = "ARBITER_ESCALATION: the gate report disagrees" });
        Run(Pre("dev-medium", "T2"));

        string first = Run(BackgroundStart("dev-medium", "T2", FrontendAgent));
        string second = Run(SyncReturn("dev-medium", "T2", FrontendAgent, "still here"));

        using JsonDocument document = JsonDocument.Parse(first);
        string context = document.RootElement.GetProperty("additionalContext").GetString()!;
        Assert.Contains("background agent 'dev-medium'", context, StringComparison.Ordinal);
        Assert.Contains("ARBITER_ESCALATION: the gate report disagrees", context, StringComparison.Ordinal);
        Assert.DoesNotContain("ARBITER_ESCALATION", second, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkersDigest_CompletesItsTaskOnlyWhenItsGatePassedUnderAFinishedStatus()
    {
        Assert.Equal("PASS", ReturnMarkerParser.Parse(PassDigest, readAttestations: false)?.TaskGate);
        Assert.Equal(["T1"], Completed("STATUS: READY_FOR_REVIEW\nTASK_GATE: PASS — agent=dev-medium gates=1"));
        Assert.Empty(Completed("STATUS: BLOCKED\nTASK_GATE: PASS — agent=dev-medium gates=1"));
        Assert.Empty(Completed("STATUS: PASS\nTASK_GATE: FAIL — agent=dev-medium gates=1 failed=build"));
        Assert.Empty(Completed("STATUS: PASS\nTASK_GATE: NONE_DECLARED — agent=dev-medium gates=0"));
        Assert.Empty(Completed("STATUS: PASS\nRESULT: FAIL\nTASK_GATE: PASS — agent=dev-medium gates=1"));
    }

    private static IReadOnlyList<string> Completed(string report)
    {
        DateTimeOffset at = new(2026, 11, 16, 14, 20, 0, TimeSpan.Zero);
        ArbiterLedgerEvent pre = new("pre-1", at, ArbiterSources.Hook, "copilot-cli", null, ArbiterLedgerPhases.Pre)
        {
            Headers = new ArbiterLedgerHeaders(Plan, "T1"),
        };
        ArbiterLedgerEvent post = new("post-1", at.AddMinutes(5), ArbiterSources.Hook, "copilot-cli", null, ArbiterLedgerPhases.Post)
        {
            PreId = pre.Id,
            Returns = ReturnMarkerParser.Parse(report, readAttestations: false),
        };
        return ArbiterLedgerQueries.CompletedTasks([pre, post], Plan);
    }

    [Fact]
    public void ABackgroundDispatchWhoseTurnEndNeverArrives_HoldsItsFilesOnlyForTheUnreturnedHorizon()
    {
        DateTimeOffset at = new(2026, 11, 16, 14, 20, 0, TimeSpan.Zero);
        ArbiterLedgerEvent lost = new("pre-1", at, ArbiterSources.Hook, "copilot-cli", null, ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            TaskFiles = ["src/T1.cs"],
        };
        ArbiterLedgerEvent Next(TimeSpan later) =>
            new("pre-2", at + later, ArbiterSources.Hook, "copilot-cli", null, ArbiterLedgerPhases.Pre) { Trigger = "delegate" };

        Assert.Equal(["src/T1.cs"], ArbiterLedgerQueries.InFlightPaths([lost, Next(TimeSpan.FromMinutes(30))], "pre-2"));
        Assert.Empty(ArbiterLedgerQueries.InFlightPaths(
            [lost, Next(ArbiterLedgerQueries.UnreturnedDispatchHorizon + TimeSpan.FromMinutes(1))], "pre-2"));
    }

    private static string Prompt(string task) =>
        $"KYBER-ARBITER: true\nPLAN_FILE: {Plan}\nTASK: {task}\n\n{Body(task)}";

    private static string Body(string task) => $"Worktree: /work. Implement {task}.";

    private string Pre(string target, string task) => JsonSerializer.Serialize(new
    {
        sessionId = Session,
        timestamp = 1794838825449,
        cwd = _probe.Root,
        toolName = "task",
        toolArgs = new { agent_type = target, name = "dashboard", description = "Implement", mode = "background", prompt = Prompt(task) },
    });

    /// <summary>The <c>postToolUse</c> of a background <c>task</c>: the specialist's prompt arrives with its headers stripped.</summary>
    private string BackgroundStart(string target, string task, string agentId) => JsonSerializer.Serialize(new
    {
        sessionId = Session,
        timestamp = 1794838826647,
        cwd = _probe.Root,
        toolCallId = "call_f11hS7ERXOYZZTGqXZClIFnn",
        toolName = "task",
        toolArgs = new { agent_type = target, name = "dashboard", description = "Implement", mode = "background", prompt = Body(task) },
        toolResult = new
        {
            textResultForLlm = $"Agent started in background with agent_id: {agentId}. You'll be notified when it completes.",
            resultType = "success",
            toolTelemetry = new
            {
                properties = new { agent_type = "custom-agent", execution_mode = "background" },
                restrictedProperties = new { agent_name = target, agent_id = agentId },
            },
        },
    });

    /// <summary>A <c>mode: "sync"</c> dispatch Copilot moved to the background while the agent was still working.</summary>
    private string MovedToBackground(string target, string task, string agentId) => JsonSerializer.Serialize(new
    {
        sessionId = Session,
        timestamp = 1794838538871,
        cwd = _probe.Root,
        toolCallId = "call_i0SBHYpDWHrJZJG0KBw1WuZM",
        toolName = "task",
        toolArgs = new { agent_type = target, name = "dashboard", description = "Implement", mode = "sync", prompt = Body(task) },
        toolResult = new
        {
            textResultForLlm = $"Agent moved to background (agent_id: {agentId}). The agent is actively working (tool_calls_completed: 30). Use read_agent to check for results.",
            resultType = "success",
            toolTelemetry = new
            {
                properties = new { agent_type = "custom-agent", execution_mode = "sync", promotion_trigger = "cache_expiry" },
                restrictedProperties = new { agent_name = target, agent_id = agentId },
            },
        },
    });

    private string SyncReturn(string target, string task, string agentId, string response) => JsonSerializer.Serialize(new
    {
        sessionId = Session,
        timestamp = 1794839754704,
        cwd = _probe.Root,
        toolCallId = "call_rRpSJiTZQq8YboYXHqU7Q5MF",
        toolName = "task",
        toolArgs = new { agent_type = target, name = "dashboard", description = "Implement", mode = "sync", prompt = Body(task) },
        toolResult = new
        {
            textResultForLlm = response,
            resultType = "success",
            toolTelemetry = new
            {
                properties = new { agent_type = "custom-agent", execution_mode = "sync" },
                restrictedProperties = new { agent_name = target, agent_id = agentId },
            },
        },
    });

    private string Stop(string agentId, string agentType, string response) => JsonSerializer.Serialize(new
    {
        sessionId = Session,
        timestamp = 1794839734914,
        cwd = _probe.Root,
        transcriptPath = "/home/.copilot/session-state/" + Session + "/events.jsonl",
        agentId,
        agentType,
        agentName = agentType,
        agentDisplayName = agentType,
        response,
        stopReason = "end_turn",
        stop_hook_active = false,
        parentAgentId = Session,
    });

    private string Run(string stdin)
    {
        ArbiterHookDecisionEngine engine = ArbiterTestServices.Engine(
            providerFactory: _ => _provider,
            planReaderFactory: _ => new ThreeTaskPlan(),
            homeDirectory: () => _home.Path);
        HookCommand command = ArbiterTestServices.Command(ArbiterTestServices.Registry(engine), ArbiterTestServices.LoadHostConfig, () => "cli-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        Assert.Equal(0, command.Run("copilot-cli", null, stdin, stdout, log, homeDirectory: _home.Path));
        Assert.DoesNotContain(HookCommand.FailClosedCode, log.ToString(), StringComparison.Ordinal);
        return stdout.ToString();
    }

    private string ArbiterDirectory => Path.Combine(_probe.Root, "artifacts", "arbiter");

    private IReadOnlyList<ArbiterLedgerEvent> Ledger() => new InFlightLedger(ArbiterDirectory).ReadAll();

    private IReadOnlyList<ArbiterDecisionRecord> Decisions() => new DecisionLog(ArbiterDirectory).ReadAll();

    /// <summary>Stands in for the plan: T1 and T2 are free and own different files, T3 depends on both.</summary>
    private sealed class ThreeTaskPlan : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification)
        {
            if (!classification.Headers.TryGetValue("TASK", out string? task))
            {
                return facts;
            }

            List<string> dependsOn = task == "T3" ? ["T1", "T2"] : [];
            return facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "test-first", ArbiterFactLabel.Derived)
                .With("plan.task", task, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { $"src/{task}.cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", dependsOn, ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
        }
    }

    /// <summary>Answers nothing and remembers every rule it was asked about.</summary>
    private sealed class RecordingProvider : IArbiterProvider
    {
        public List<string> Asked { get; } = [];

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Asked.AddRange(rules.Select(rule => rule.Id));
            return Task.FromResult(new ArbiterStep1BatchResult(
                rules.ToDictionary(rule => rule.Id, _ => ArbiterStep1BatchResult.NotEvaluated, StringComparer.Ordinal),
                "stub",
                null,
                true,
                rules.ToDictionary(
                    rule => rule.Id,
                    rule => new ArbiterStep1Score(null, 0.95, rule.Ask?.ConfidenceAtLeast),
                    StringComparer.Ordinal)));
        }
    }
}
