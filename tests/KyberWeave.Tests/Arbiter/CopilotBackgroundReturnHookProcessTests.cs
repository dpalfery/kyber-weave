using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Runs the hook commands the Copilot hook file installs (<c>kyber-weave-arbiter hook --harness copilot-cli
/// --scope global</c> and <c>kyber-weave-arbiter worker-guard --harness copilot-cli</c>) as child processes that read
/// the event on stdin, over the payloads of a live conductor run on Copilot CLI 1.0.112, trimmed.
/// </summary>
/// <remarks>
/// The first cut of background-return recording passed every test that called the hook command object and recorded
/// nothing live: the process asks a pass-through question before it builds anything, through an adapter that has no
/// decision engine, and that adapter answered that a turn's end passes through. Only the process shows that.
/// </remarks>
public sealed class CopilotBackgroundReturnHookProcessTests : IDisposable
{
    private const string Session = "af9f5f9f-a2f8-484f-83ba-69a7fc41f915";
    private const string BackendAgent = "baa94c73-10e2-4e4a-8a67-8b7051f84f2a";
    private const string FrontendAgent = "4628f338-6001-42b4-ab0d-ff565f4dbeef";
    private const string Plan = "docs/plans/2026-11-16-job-status-dashboard.md";

    private const string PlanText =
        """
        # Plan: job status dashboard

        **Status: Ready, 2026-11-16.**

        ## Tasks

        ### T1 — Status summary API
        - **Files:** `src/Api/JobController.cs`

        ### T2 — Status cards
        - **Files:** `web/src/JobStatusCards.tsx`

        ### T3 — Live verification
        - **Depends on:** T1, T2
        - **Files:** `tests/JobStatusLiveTests.cs`
        """;

    private const string BackendFirstTurn =
        "Discovery: codegraph 6, docs 4, direct reads 8; test-file reads supplied details omitted by the index.\n\n" +
        "STATUS: PASS  \nFILES: Eight T1 files and tests  \nTESTS: total=47 failed=0 passed=47  \n" +
        "COMMIT: 14ecc75b8cb3f53ccd52f8c75baa9b62bf1b70e2  \nREMOTE_HEAD: UNPUSHED  \n" +
        "TASK_GATE: PASS — agent=dev-medium gates=1 failed=none skipped=0 report=.agent_scratchpad/t1/task-gate.json runs=1";

    private const string BackendReworkTurn =
        "Artifact: .agent_scratchpad/T1/input-sites.md  \nInput sites: 1 traced, 7 files with none  \n\n" +
        "STATUS: PASS  \nFILES: src/Api/JobController.cs  \nTESTS: total=47 failed=0 passed=47  \n" +
        "COMMIT: 14ecc75b8cb3f53ccd52f8c75baa9b62bf1b70e2  \nREMOTE_HEAD: UNPUSHED  \n" +
        "TASK_GATE: PASS — agent=dev-medium gates=1 failed=none skipped=0 report=.agent_scratchpad/t1/task-gate.json runs=1";

    private const string FrontendTurn =
        "STATUS: READY_FOR_REVIEW\nARTIFACTS: Dashboard CSS, types, API service, overview, three status-card components.\n" +
        "SUMMARY: Added validated job-status summary cards with loading and error handling.\n" +
        "TASK_GATE: PASS — agent=dev-medium gates=4 failed=none skipped=0 report=.agent_scratchpad/t2/task-gate.json runs=4\n" +
        "COMMIT: fe9034b\nOPEN_QUESTIONS: none";

    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly TempDirectory _home = new();

    public CopilotBackgroundReturnHookProcessTests()
    {
        // The user-level hook decides under the user's configuration, as on the machine the run came from.
        Directory.CreateDirectory(Path.Combine(_home.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_home.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");
        Directory.CreateDirectory(Path.Combine(_probe.Root, "docs", "plans"));
        File.WriteAllText(Path.Combine(_probe.Root, Plan), PlanText);
        File.WriteAllText(Path.Combine(_probe.Root, "docs", "plans", "README.md"), "# Plans\n\n**Status: Ready**\n");
    }

    public void Dispose()
    {
        _probe.Dispose();
        _home.Dispose();
    }

    [Fact]
    public void TheInstalledHookCommand_RecordsEachBackgroundTurnAsItsTasksReturn_AndTheDependentTaskIsReady()
    {
        Hook(Pre("dev-medium", "T1"));
        Hook(Pre("dev-medium", "T2"));
        Hook(BackgroundStart("dev-medium", "T1", BackendAgent, "call_Kiv5uIqrjMOUoE5TQNeEZqLx"));
        Hook(BackgroundStart("dev-medium", "T2", FrontendAgent, "call_e2Tq0oDqJ2nUO6rVY7sC1kLm"));
        Assert.DoesNotContain(Ledger(), e => e.Phase == ArbiterLedgerPhases.Post);

        Hook(Stop(FrontendAgent, "dev-medium", FrontendTurn));
        Hook(Stop(BackendAgent, "dev-medium", BackendFirstTurn));
        Hook(Stop(BackendAgent, "dev-medium", BackendReworkTurn));

        IReadOnlyList<ArbiterLedgerEvent> ledger = Ledger();
        List<ArbiterLedgerEvent> posts = [.. ledger.Where(e => e.Phase == ArbiterLedgerPhases.Post)];
        Assert.Equal(3, posts.Count);
        Assert.All(posts, post => Assert.Equal("delegate.returned", post.Trigger));
        Assert.Equal(
            ["T2", "T1", "T1"],
            posts.Select(post => ArbiterLedgerQueries.DispatchHeadersFor(ledger, post.Id)?.Task));
        Assert.All(posts, post => Assert.Equal(Plan, ArbiterLedgerQueries.DispatchHeadersFor(ledger, post.Id)?.PlanFile));
        Assert.All(posts, post => Assert.Equal("PASS", post.Returns?.TaskGate));
        Assert.Equal(["T1", "T2"], ArbiterLedgerQueries.CompletedTasks(ledger, Plan));

        string answer = Hook(Pre("dev-medium", "T3"));

        ArbiterDecisionRecord decision = Decisions().Last(d => d.Trigger == "delegate");
        ArbiterDecisionRule ready = Assert.Single(decision.Rules, rule => rule.Id == "KW-ARB-READY-001");
        Assert.Equal("ready", ready.Answer);
        Assert.Equal(0, ready.Step);
        Assert.DoesNotContain("KW-ARB-READY-001", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstalledHookCommand_DoesNotAnswerATurnsEndBeforeItReachesTheDecisionEngine()
    {
        Assert.False(HookFastPath.IsPassThrough("copilot-cli", null, Stop(FrontendAgent, "dev-medium", FrontendTurn)));
    }

    [Fact]
    public void TheInstalledWorkerGuard_ClosesTheBudgetWindowWhenTheTurnEnds()
    {
        string budgets = Path.Combine(_home.Path, ".kyber-weave", "arbiter", "budgets");

        Run(["worker-guard", "--harness", "copilot-cli"], ToolCall(BackendAgent, "dev-medium"));
        Assert.Single(Directory.GetFiles(budgets, "*.log"));

        Run(["worker-guard", "--harness", "copilot-cli"], Stop(BackendAgent, "dev-medium", BackendFirstTurn));
        Assert.Empty(Directory.GetFiles(budgets, "*.log"));
    }

    [Fact]
    public void ADispatchAnEarlierRunNeverSawReturn_DoesNotHoldItsFilesForTheNextRun()
    {
        // A live case: run 5's two background workers never got a return row, and run 6, started later over the
        // same plan path and files, had its first two dispatches refused as overlapping them.
        StartRun("run-a");
        Hook(Pre("dev-medium", "T1"));
        AssertDispatched();
        Hook(BackgroundStart("dev-medium", "T1", BackendAgent, "call_Kiv5uIqrjMOUoE5TQNeEZqLx"));

        StartRun("run-b");
        string answer = Hook(Pre("dev-medium", "T1"));

        Assert.Equal("ready", ReadyAnswer());
        Assert.DoesNotContain("KW-ARB-READY-001", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void WithinOneRun_AnUnreturnedDispatchStillHoldsItsFiles()
    {
        StartRun("run-a");
        Hook(Pre("dev-medium", "T1"));
        AssertDispatched();
        Hook(BackgroundStart("dev-medium", "T1", BackendAgent, "call_Kiv5uIqrjMOUoE5TQNeEZqLx"));

        string answer = Hook(Pre("dev-medium", "T1"));

        Assert.Equal("overlaps-in-flight", ReadyAnswer());
        Assert.Contains("KW-ARB-READY-001", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void TasksAnEarlierRunCompleted_DoNotCompleteTheSameNamedTasksOfTheNextRun()
    {
        StartRun("run-a");
        ReturnBothDependencies(BackendAgent, FrontendAgent);
        Hook(Pre("dev-medium", "T3"));
        Assert.Equal("ready", ReadyAnswer());

        StartRun("run-b");
        Hook(Pre("dev-medium", "T3"));
        // The ledger no longer says ready, so the question goes to the model; with none configured it is unanswered.
        Assert.Equal(ArbiterStep1BatchResult.NotEvaluated, ReadyAnswer());

        // That dispatch ran (no model refused it) and ends blocked, so it holds no files when it is tried again.
        const string VerifierAgent = "33333333-9085-4edf-a169-44bd0cdfbffc";
        Hook(BackgroundStart("dev-medium", "T3", VerifierAgent, "call_Z6JSwlrA2mQeT1yUo8pLkNbV"));
        Hook(Stop(VerifierAgent, "dev-medium", "STATUS: BLOCKED\nBLOCKER: T1 and T2 are not committed yet."));

        ReturnBothDependencies("11111111-0e61-4a61-8785-43b058ab723a", "22222222-20dd-4012-a22b-041605f7a556");
        Hook(Pre("dev-medium", "T3"));
        Assert.Equal("ready", ReadyAnswer());
    }

    private void ReturnBothDependencies(string backendAgent, string frontendAgent)
    {
        Hook(Pre("dev-medium", "T1"));
        AssertDispatched();
        Hook(Pre("dev-medium", "T2"));
        AssertDispatched();
        Hook(BackgroundStart("dev-medium", "T1", backendAgent, "call_Kiv5uIqrjMOUoE5TQNeEZqLx"));
        Hook(BackgroundStart("dev-medium", "T2", frontendAgent, "call_e2Tq0oDqJ2nUO6rVY7sC1kLm"));
        Hook(Stop(frontendAgent, "dev-medium", FrontendTurn));
        Hook(Stop(backendAgent, "dev-medium", BackendFirstTurn));
    }

    /// <summary>The newest task dispatch was let through, so its worker ran and holds its files.</summary>
    private void AssertDispatched()
    {
        ArbiterDecisionRecord decision = Decisions().Last(d => d.Trigger == "delegate");
        Assert.True(
            decision.Outcome != "escalate",
            string.Join(", ", decision.Rules.Where(rule => rule.Effect == "escalate").Select(rule => $"{rule.Id}={rule.Answer}")));
    }

    /// <summary>
    /// Writes the record <c>kyber-weave run start</c> leaves for a run that works in the repository in place, on
    /// the branch it has checked out, so the run is live.
    /// </summary>
    private void StartRun(string name)
    {
        string head = File.ReadAllText(Path.Combine(_probe.Root, ".git", "HEAD")).Trim();
        string branch = head["ref: refs/heads/".Length..];
        string runs = Path.Combine(_home.Path, ".kyber-weave", "runs");
        Directory.CreateDirectory(runs);
        File.WriteAllText(
            Path.Combine(runs, name + ".json"),
            new JsonObject
            {
                ["BaseRef"] = "origin/main",
                ["Repository"] = ArbiterTestServices.Get<IGitRepositoryFacts>().GetCommonDirectory(_probe.Root),
                ["Branch"] = branch,
                ["Worktree"] = _probe.Root,
                ["StartedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            }.ToJsonString());
    }

    /// <summary>What the readiness rule answered on the newest task dispatch.</summary>
    private string ReadyAnswer() =>
        Assert.Single(Decisions().Last(d => d.Trigger == "delegate").Rules, rule => rule.Id == "KW-ARB-READY-001").Answer;

    private static string Prompt(string task) =>
        $"KYBER-ARBITER: true\nPLAN_FILE: {Plan}\nTASK: {task}\n\n{Body(task)}";

    private static string Body(string task) =>
        $"Worktree: /work/demo. Execute Task {task}. First run `kyber-weave docs packet {task}` in that worktree and follow the packet and delivery contract.";

    /// <summary>One <c>task</c> call as the <c>preToolUse</c> hook command reads it.</summary>
    private string Pre(string target, string task) => new JsonObject
    {
        ["sessionId"] = Session,
        ["timestamp"] = 1794841999000,
        ["cwd"] = _probe.Root,
        ["toolName"] = "task",
        ["toolArgs"] = new JsonObject
        {
            ["agent_type"] = target,
            ["name"] = "status",
            ["description"] = "Implement status summary",
            ["mode"] = "background",
            ["prompt"] = Prompt(task),
        },
    }.ToJsonString();

    /// <summary>The <c>postToolUse</c> of a background <c>task</c>, as logged: the result text is elided, the prompt has lost its headers.</summary>
    private string BackgroundStart(string target, string task, string agentId, string toolCallId) => new JsonObject
    {
        ["sessionId"] = Session,
        ["timestamp"] = 1794842000524,
        ["cwd"] = _probe.Root,
        ["toolCallId"] = toolCallId,
        ["toolName"] = "task",
        ["toolArgs"] = new JsonObject
        {
            ["agent_type"] = target,
            ["name"] = "status",
            ["description"] = "Implement status summary",
            ["mode"] = "background",
            ["prompt"] = Body(task),
        },
        ["toolResult"] = new JsonObject
        {
            ["textResultForLlm"] = "[copilot:elided textResultForLlm (468 bytes) - duplicated pre-hook tool-result field; canonical data remains in adjacent tool and skill events]",
            ["resultType"] = "success",
            ["toolTelemetry"] = new JsonObject
            {
                ["properties"] = new JsonObject { ["agent_type"] = "custom-agent", ["execution_mode"] = "background", ["resolved_model"] = "gpt-6.1-terra" },
                ["restrictedProperties"] = new JsonObject { ["agent_name"] = target, ["agent_id"] = agentId },
            },
            ["causalDependencies"] = new JsonArray(),
        },
    }.ToJsonString();

    /// <summary>A <c>subagentStop</c> event with the fields, and the field order, of the live one.</summary>
    private string Stop(string agentId, string agentType, string response) => new JsonObject
    {
        ["sessionId"] = Session,
        ["transcriptPath"] = $"/home/.copilot/session-state/{Session}/events.jsonl",
        ["agentName"] = agentType,
        ["agentType"] = agentType,
        ["agentId"] = agentId,
        ["stopReason"] = "end_turn",
        ["response"] = response,
        ["stop_hook_active"] = false,
        ["agentDescription"] = "Implements code for one task.",
        ["agentDisplayName"] = agentType,
        ["timestamp"] = 1794842215900,
        ["traceparent"] = "00-c8a0a463e6e1124828d51dda3249c7d4-3a03d26412ae39b7-01",
        ["parentAgentId"] = Session,
        ["cwd"] = _probe.Root,
    }.ToJsonString();

    /// <summary>A tool call from inside a subagent, as the worker guard reads it.</summary>
    private string ToolCall(string agentId, string agentType) => new JsonObject
    {
        ["sessionId"] = Session,
        ["cwd"] = _probe.Root,
        ["agentId"] = agentId,
        ["parentAgentId"] = Session,
        ["agentType"] = agentType,
        ["toolName"] = "bash",
        ["toolArgs"] = new JsonObject { ["command"] = "kyber-weave docs packet T1 --agent dev-medium" },
    }.ToJsonString();

    private string Hook(string stdin) => Run(["hook", "--harness", "copilot-cli", "--scope", "global"], stdin);

    /// <summary>Starts the arbiter host as the hook file does, with the operator's home pointed at a scratch one.</summary>
    private string Run(string[] arguments, string stdin)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = _probe.Root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "kyber-weave-arbiter.dll"));
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["HOME"] = _home.Path;
        startInfo.Environment["USERPROFILE"] = _home.Path;
        startInfo.Environment.Remove("XDG_CONFIG_HOME");

        ProcessResult result = ProcessRunner.Run(startInfo, stdin, TimeSpan.FromSeconds(60));

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.DoesNotContain(HookCommand.FailClosedCode, result.StandardError, StringComparison.Ordinal);
        return result.StandardOutput;
    }

    private string ArbiterDirectory =>
        Assert.Single(Directory.GetDirectories(Path.Combine(_home.Path, ".kyber-weave", "arbiter", "projects")));

    private IReadOnlyList<ArbiterLedgerEvent> Ledger() => new InFlightLedger(ArbiterDirectory).ReadAll();

    private IReadOnlyList<ArbiterDecisionRecord> Decisions() => new DecisionLog(ArbiterDirectory).ReadAll();
}
