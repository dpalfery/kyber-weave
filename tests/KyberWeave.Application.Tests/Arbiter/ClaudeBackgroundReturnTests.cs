using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A Claude background dispatch's hand-back pairs with its own dispatch's pre event although every hook event is a
/// separate process: the dispatch is followed through the Arbiter's saved store, never through process memory, and a
/// stop event naming an agent id closes only that agent's dispatch.
/// </summary>
/// <remarks>
/// Each event runs through a fresh hook command and a fresh engine over the in-memory host, so the only state the
/// events share is the host's store and ledger, as it is between two real hook processes.
/// </remarks>
public sealed class ClaudeBackgroundReturnTests : IDisposable
{
    private const string Plan = "docs/plans/x.md";

    private const string Session = "session-claude-1";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    private readonly FakeArbiterProvider _provider = new()
    {
        DefaultAnswer = ArbiterStep1BatchResult.NotEvaluated,
        ScoreDefaultAnswer = true,
        Confidence = 0.95,
        Model = "stub",
    };

    public void Dispose() => _host.Dispose();

    private sealed class NoGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    /// <summary>Stands in for the plan: every task is free and declares its own file.</summary>
    private sealed class PlanFacts : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification)
        {
            if (!classification.Headers.TryGetValue("TASK", out string? task))
            {
                return facts;
            }

            return facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "standard", ArbiterFactLabel.Derived)
                .With("plan.task", task, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { "src/" + task + ".cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", new List<string>(), ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
        }
    }

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = new ArbiterConfig { Enabled = true, Rules = ArbiterConfigDefaults.Product.Rules },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static string Prompt(string task) =>
        $"KYBER-ARBITER: true\nPLAN_FILE: {Plan}\nTASK: {task}\n\nWork on {task}.";

    private static string Report(string task) => $"Finished {task}.\nSTATUS: PASS\nRESULT: PASS\n";

    private ArbiterHookDecisionEngine Engine() => _host.Engine(
        providerFactory: _ => _provider,
        gitFactsFactory: _ => new NoGitFacts(),
        planReaderFactory: _ => new PlanFacts(),
        homeDirectory: () => _host.RepoRoot);

    /// <summary>Runs one Claude hook event as its own process would: a fresh command over a fresh engine.</summary>
    private async Task<(string Stdout, string Log)> Send(object payload)
    {
        using StringWriter stdout = new();
        using StringWriter log = new();
        HookCommand command = _host.HookCommand(
            ArbiterTestServices.Registry(Engine()),
            configLoader: _ => Config());
        int exit = await command.RunAsync("claude", null, JsonSerializer.Serialize(payload), stdout, log);
        Assert.Equal(0, exit);
        return (stdout.ToString(), log.ToString());
    }

    private async Task<(string Stdout, string Log)> Dispatch(string task, string toolUseId) => await Send(new
    {
        hook_event_name = "PreToolUse",
        session_id = Session,
        cwd = _host.RepoRoot,
        agent_type = "conductor",
        tool_name = "Agent",
        tool_input = new { description = task, prompt = Prompt(task), subagent_type = "dev-easy", run_in_background = true },
        tool_use_id = toolUseId,
    });

    private async Task<(string Stdout, string Log)> Launched(string task, string toolUseId, string agentId) => await Send(new
    {
        hook_event_name = "PostToolUse",
        session_id = Session,
        cwd = _host.RepoRoot,
        agent_type = "conductor",
        tool_name = "Agent",
        tool_input = new { description = task, prompt = Prompt(task), subagent_type = "dev-easy", run_in_background = true },
        tool_use_id = toolUseId,
        tool_response = new { status = "async_launched", agentId, content = Array.Empty<string>() },
    });

    private async Task<(string Stdout, string Log)> Handback(string agentId, string message) => await Send(new
    {
        hook_event_name = "PreToolUse",
        session_id = Session,
        cwd = _host.RepoRoot,
        agent_id = agentId,
        agent_type = "dev-easy",
        tool_name = "SubagentHandback",
        tool_input = new { message },
        tool_use_id = "toolu-handback-" + agentId,
    });

    private async Task<(string Stdout, string Log)> Completed(string task, string toolUseId, string agentId) => await Send(new
    {
        hook_event_name = "PostToolUse",
        session_id = Session,
        cwd = _host.RepoRoot,
        agent_type = "conductor",
        tool_name = "Agent",
        tool_input = new { description = task, prompt = Prompt(task), subagent_type = "dev-easy" },
        tool_use_id = toolUseId,
        tool_response = new
        {
            status = "completed",
            agentId,
            content = new[] { new { type = "text", text = Report(task) } },
        },
    });

    private async Task<(string Stdout, string Log)> Stop(string? agentId)
    {
        Dictionary<string, object?> payload = new()
        {
            ["hook_event_name"] = "SubagentStop",
            ["session_id"] = Session,
            ["cwd"] = _host.RepoRoot,
            ["agent_type"] = "dev-easy",
        };
        if (agentId is not null)
        {
            payload["agent_id"] = agentId;
        }

        return await Send(payload);
    }

    /// <summary>Moves the host clock on, so a later event is measurably later than the rows already written.</summary>
    private void Advance() => _host.Clock.Advance(TimeSpan.FromSeconds(1));

    /// <summary>Dispatches and launches one background agent, with the clock moving on after each event.</summary>
    private async Task Start(string task, string toolUseId, string agentId)
    {
        await Dispatch(task, toolUseId);
        Advance();
        await Launched(task, toolUseId, agentId);
        Advance();
    }

    private IReadOnlyList<ArbiterLedgerEvent> Events() => _host.Ledger.ReadAll();

    private ArbiterLedgerEvent PreOf(string toolUseId) =>
        Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre && e.CallId == toolUseId);

    private IReadOnlyList<ArbiterLedgerEvent> Posts() =>
        [.. Events().Where(e => e.Phase == ArbiterLedgerPhases.Post)];

    [Fact]
    public async Task HandbacksOutOfDispatchOrder_EachPairWithTheirOwnDispatch()
    {
        await Start("T2", "toolu-1", "agent-1");
        await Start("T5", "toolu-2", "agent-2");
        await Start("T6", "toolu-3", "agent-3");

        await Handback("agent-2", Report("T5"));
        Advance();
        await Handback("agent-3", Report("T6"));
        Advance();
        await Handback("agent-1", Report("T2"));

        IReadOnlyList<ArbiterLedgerEvent> posts = Posts();
        Assert.Equal(3, posts.Count);
        foreach (string toolUseId in new[] { "toolu-1", "toolu-2", "toolu-3" })
        {
            ArbiterLedgerEvent pre = PreOf(toolUseId);
            ArbiterLedgerEvent post = Assert.Single(posts, p => p.PreId == pre.Id);
            Assert.Equal(Session, post.Session);
        }

        Assert.Empty(_host.Ledger.InFlightPaths());
    }

    [Fact]
    public async Task HandbackWithNoSavedRecord_IsRecordedUnpairedAllowedAndLogsWhatItLookedUp()
    {
        await Dispatch("T2", "toolu-1");
        Advance();

        (string stdout, string log) = await Handback("agent-nobody", Report("T2"));

        Assert.Equal(string.Empty, stdout);
        ArbiterLedgerEvent post = Assert.Single(Posts());
        Assert.Null(post.PreId);
        Assert.Contains(
            $"Arbiter: no followed background dispatch for agent 'agent-nobody' in session '{Session}'",
            log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandbackWhoseAgentIdDiffersFromTheLaunch_IsRecordedUnpaired()
    {
        await Start("T2", "toolu-1", "agent-1");

        (string stdout, string log) = await Handback("agent-other", Report("T2"));

        Assert.Equal(string.Empty, stdout);
        Assert.Null(Assert.Single(Posts()).PreId);
        Assert.NotEmpty(_host.Ledger.InFlightPaths());
        Assert.Contains(
            $"Arbiter: no followed background dispatch for agent 'agent-other' in session '{Session}'",
            log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncLaunchWithoutAnAgentId_IsNotTrackedAndLogsWhy()
    {
        await Dispatch("T2", "toolu-1");
        Advance();

        (_, string log) = await Send(new
        {
            hook_event_name = "PostToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            agent_type = "conductor",
            tool_name = "Agent",
            tool_input = new { description = "T2", prompt = Prompt("T2"), subagent_type = "dev-easy", run_in_background = true },
            tool_use_id = "toolu-1",
            tool_response = new { status = "async_launched", content = Array.Empty<string>() },
        });

        Assert.Contains(
            $"Arbiter: background launch of agent '(none)' in session '{Session}' is not recorded",
            log,
            StringComparison.Ordinal);

        await Handback("agent-1", Report("T2"));
        Assert.Null(Assert.Single(Posts()).PreId);
    }

    [Fact]
    public async Task ForegroundCompletedTwiceForABackgroundAgent_IsRecordedOnceAndPaired()
    {
        await Start("T2", "toolu-1", "agent-1");
        ArbiterLedgerEvent pre = PreOf("toolu-1");

        await Completed("T2", "toolu-1", "agent-1");
        Advance();
        await Completed("T2", "toolu-1", "agent-1");

        ArbiterLedgerEvent post = Assert.Single(Posts());
        Assert.Equal(pre.Id, post.PreId);
    }

    [Fact]
    public async Task StopForAFollowedAgentWhosePreIsAlreadyPaired_LogsThatNothingIsClosed()
    {
        await Start("T2", "toolu-1", "agent-1");
        await Handback("agent-1", Report("T2"));
        Advance();

        (_, string log) = await Stop("agent-1");

        Assert.Contains(
            "Arbiter: the SubagentStop of agent 'agent-1' follows a saved dispatch, " +
            "but that dispatch is not an open pre of this session, so nothing is closed.",
            log,
            StringComparison.Ordinal);
        Assert.Single(Posts());
    }

    [Fact]
    public async Task TheSameHandbackTwice_IsRecordedOnce()
    {
        await Start("T2", "toolu-1", "agent-1");

        await Handback("agent-1", Report("T2"));
        Advance();
        await Handback("agent-1", Report("T2"));

        Assert.Single(Posts());
    }

    [Fact]
    public async Task Handback_ThenForegroundPostCompleted_IsNotRecordedTwice()
    {
        await Start("T2", "toolu-1", "agent-1");

        (string firstStdout, _) = await Handback("agent-1", Report("T2"));
        Advance();
        (string secondStdout, _) = await Completed("T2", "toolu-1", "agent-1");

        Assert.Single(Posts());
        Assert.Equal(string.Empty, firstStdout);
        Assert.Equal(string.Empty, secondStdout);
    }

    [Fact]
    public async Task AsyncLaunchWhoseCallIdMatchesNoPre_SavesNothing_AndALaterHandbackIsUnpaired()
    {
        await Dispatch("T2", "toolu-1");
        Advance();

        // The prompt differs too: a launch that gives a call id no pre carries still pairs by prompt digest.
        (_, string log) = await Launched("T9", "toolu-unknown", "agent-1");
        Advance();
        await Handback("agent-1", Report("T2"));

        string pairing = log.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("Pairing keys compared", StringComparison.Ordinal));
        Assert.Contains("call-id 'toolu-unknown'", pairing, StringComparison.Ordinal);
        Assert.Contains("unpaired pre", pairing, StringComparison.Ordinal);
        Assert.Null(Assert.Single(Posts()).PreId);
    }

    [Fact]
    public async Task StopNamingAnAgentId_ClosesExactlyThatAgentsDispatch()
    {
        await Start("T2", "toolu-1", "agent-1");
        await Start("T5", "toolu-2", "agent-2");
        ArbiterLedgerEvent second = PreOf("toolu-2");

        await Stop("agent-2");

        ArbiterLedgerEvent recovered = Assert.Single(Posts());
        Assert.Equal(second.Id, recovered.PreId);
        Assert.Equal("SubagentStop", recovered.Recovered);
        Assert.Equal(second.Headers, recovered.Headers);
    }

    [Fact]
    public async Task StopForAnAgentWhoseHandbackWasRecorded_WritesNoRowAndLeavesTheSiblingOpen()
    {
        await Start("T2", "toolu-1", "agent-1");
        await Start("T5", "toolu-2", "agent-2");
        await Handback("agent-1", Report("T2"));
        Advance();

        await Stop("agent-1");

        ArbiterLedgerEvent post = Assert.Single(Posts());
        Assert.Null(post.Recovered);
        Assert.Equal(PreOf("toolu-1").Id, post.PreId);
    }

    [Fact]
    public async Task StopWithNoAgentId_ClosesNoneOfSeveralOpenDispatches()
    {
        await Start("T2", "toolu-1", "agent-1");
        await Start("T5", "toolu-2", "agent-2");
        await Start("T6", "toolu-3", "agent-3");

        await Stop(null);

        Assert.Empty(Posts());
    }

    [Fact]
    public async Task HandbackWithASavedRecord_IsEvaluatedWithTheDispatchsCallerTargetAndTriggerFamily()
    {
        await Start("T2", "toolu-1", "agent-1");

        await Handback("agent-1", Report("T2"));

        ArbiterLedgerEvent post = Assert.Single(Posts());
        Assert.Equal("conductor", post.Caller);
        Assert.Equal("dev-easy", post.Target);
        Assert.Equal("delegate.returned", post.Trigger);
    }

    private async Task<(string Stdout, string Log)> TaskStop(string taskId) => await Send(new
    {
        hook_event_name = "PostToolUse",
        session_id = Session,
        cwd = _host.RepoRoot,
        tool_name = "TaskStop",
        tool_input = new { task_id = taskId },
        tool_response = new { message = "Successfully stopped task: " + taskId, task_id = taskId, task_type = "local_agent" },
        tool_use_id = "toolu-stop-" + taskId,
    });

    /// <summary>
    /// A stopped background agent's turn never ends, so neither a hand-back nor a stop event closes its dispatch. The
    /// dispatching agent's <c>TaskStop</c> names it by its agent id, and that closes exactly its dispatch.
    /// </summary>
    [Fact]
    public async Task TaskStopOfAFollowedAgent_ClosesExactlyThatAgentsDispatch()
    {
        await Start("T2", "toolu-1", "agent-1");
        await Start("T5", "toolu-2", "agent-2");

        (string stdout, _) = await TaskStop("agent-2");

        Assert.Equal(string.Empty, stdout);
        ArbiterLedgerEvent closed = Assert.Single(Posts());
        Assert.Equal(PreOf("toolu-2").Id, closed.PreId);
        Assert.Equal("TaskStop", closed.Recovered);
    }

    [Fact]
    public async Task TaskStopOfAnAgentThatAlreadyReturned_WritesNoRow()
    {
        await Start("T2", "toolu-1", "agent-1");
        await Handback("agent-1", Report("T2"));
        Advance();

        await TaskStop("agent-1");

        Assert.Null(Assert.Single(Posts()).Recovered);
    }

    [Fact]
    public async Task TaskStopOfATaskTheArbiterDoesNotFollow_WritesNothing()
    {
        await Start("T2", "toolu-1", "agent-1");

        await TaskStop("shell-9");

        Assert.Empty(Posts());
    }

    /// <summary>
    /// On the main thread the dispatch and launch events name no agent, and the launch reports the prompt the hook
    /// expanded, with its routing headers gone. The pre event resolved the conductor from those headers, so the
    /// return is judged with them too and classifies as a delegate return rather than an investigation.
    /// </summary>
    [Fact]
    public async Task AMainThreadBackgroundReturn_IsJudgedWithItsDispatchsHeaders()
    {
        await Send(new
        {
            hook_event_name = "PreToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            tool_name = "Agent",
            tool_input = new { description = "T2", prompt = Prompt("T2"), subagent_type = "dev-easy", run_in_background = true },
            tool_use_id = "toolu-1",
        });
        Advance();
        await Send(new
        {
            hook_event_name = "PostToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            tool_name = "Agent",
            tool_input = new { description = "T2", prompt = "<task>\nWork on T2.\n</task>", subagent_type = "dev-easy", run_in_background = true },
            tool_use_id = "toolu-1",
            tool_response = new { status = "async_launched", agentId = "agent-1", content = Array.Empty<string>() },
        });
        Advance();
        Assert.Equal("conductor", PreOf("toolu-1").Caller);

        await Handback("agent-1", Report("T2"));

        ArbiterLedgerEvent post = Assert.Single(Posts());
        Assert.Equal(PreOf("toolu-1").Id, post.PreId);
        Assert.Equal("conductor", post.Caller);
        Assert.Equal("delegate.returned", post.Trigger);
    }

    /// <summary>
    /// The log store reports an unreadable ledger as data, not as an exception. A return whose headers cannot be read
    /// back for that reason is judged from its saved prompt, and the log says why.
    /// </summary>
    [Fact]
    public async Task ABackgroundReturnWhoseLedgerCannotBeRead_LogsThatItsHeadersWereNotRestored()
    {
        await Dispatch("T2", "toolu-1");
        Advance();
        await Send(new
        {
            hook_event_name = "PostToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            tool_name = "Agent",
            tool_input = new { description = "T2", prompt = "<task>\nWork on T2.\n</task>", subagent_type = "dev-easy", run_in_background = true },
            tool_use_id = "toolu-1",
            tool_response = new { status = "async_launched", agentId = "agent-1", content = Array.Empty<string>() },
        });
        Advance();
        _host.State.Log.UnreadablePaths.Add(_host.Ledger.FilePath);

        (_, string log) = await Handback("agent-1", "<task>done</task>");

        Assert.Contains("could not be read back", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pre event whose line is not a record is dropped from the read. A followed return whose pre event is missing
    /// while the ledger holds a malformed line is judged from its saved prompt, and the log says why.
    /// </summary>
    [Fact]
    public async Task ABackgroundReturnWhosePreLineIsMalformed_LogsThatItsHeadersWereNotRestored()
    {
        string directory = _host.Ledger.DirectoryPath;
        _host.Get<IBackgroundDispatchStore>().Save(
            directory,
            new BackgroundDispatchRecord(
                new BackgroundDispatch(ClaudeHookAdapter.Token, Session, "agent-1", "unidentified", "dev-easy", "<task>\nWork on T2.\n</task>", "toolu-1"),
                "pre-lost-to-a-torn-line"));
        await _host.State.Log.AppendLineAsync(directory, InFlightLedger.FileName, "{\"id\":\"pre-lost-to-a-torn", CancellationToken.None);

        (_, string log) = await Handback("agent-1", "<task>done</task>");

        Assert.Contains("could not be read back", log, StringComparison.Ordinal);
        Assert.Contains("not a record", log, StringComparison.Ordinal);
    }

    /// <summary>An engine that follows one agent and answers its return with a note.</summary>
    private sealed class NotingEngine : IHookDecisionEngine, IBackgroundDispatchEngine
    {
        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public void TrackBackgroundDispatch(BackgroundDispatch dispatch, HookContext context)
        {
        }

        public BackgroundDispatchRecord? FindBackgroundDispatch(string sessionId, string agentId, HookContext context) =>
            new(new BackgroundDispatch("claude", sessionId, agentId, "conductor", "dev-easy", "prompt"), "pre-1");

        public HookOutcome DecideBackgroundReturn(
            string harness, string sessionId, string agentId, string response, KyberWeaveConfig config, HookContext context) =>
            new(HookOutcomeKind.PostAnnotation, "ARBITER NOTE");

        public IReadOnlyList<string> TakePendingNotes(string sessionId, string caller, HookContext context) => [];
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
        /// <inheritdoc/>
        public Task<HookOutcome> DecideBackgroundReturnAsync(
            string harness,
            string sessionId,
            string agentId,
            string response,
            KyberWeaveConfig config,
            HookContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecideBackgroundReturn(harness, sessionId, agentId, response, config, context));
    }

    [Fact]
    public async Task HandbackWhoseRulesAddANote_StillRendersAsAllowWithTheNoteAfterOneBlankLine()
    {
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(new NotingEngine()), _ => Config(), () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        string stdin = JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            agent_id = "agent-1",
            agent_type = "dev-easy",
            tool_name = "SubagentHandback",
            tool_input = new { message = "Done." },
            tool_use_id = "toolu-handback-1",
        });

        Assert.Equal(0, await command.RunAsync("claude", null, stdin, stdout, log));

        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        Assert.Equal("Done.\n\nARBITER NOTE", output.GetProperty("updatedInput").GetProperty("message").GetString());
    }
}
