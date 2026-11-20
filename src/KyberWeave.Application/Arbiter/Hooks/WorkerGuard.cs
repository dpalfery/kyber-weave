using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The guard on a Squad subagent's own tool calls, from machine facts only. It refuses a write into, or a shell
/// command that would run in, the repository's canonical checkout while a live run works in a worktree of its own
/// (<c>KW-ARB-CHECKOUT-001</c>), ends a developer dispatch whose current turn has run past its budget of
/// tool calls or minutes (<c>KW-ARB-BUDGET-001</c>), and refuses a read-only dispatch's command that the
/// declared technology folders' <c>worker.command</c> rules do not allow.
/// </summary>
/// <remarks>
/// It runs on every tool call a harness reports, so it starts no process and calls no model: the run records,
/// the checkout's git files and one append-only counter file per subagent are all it reads for the write, command
/// and budget refusals. The command allow-list is the one place it reads the project's configuration and evaluates
/// step 0, and only for a capability profile that may run commands and may not write files; there it also reads
/// the ledger's dispatch headers, to learn the technology the read-only agent's dispatch named. A root agent (a
/// person's own session, or the conductor) is never guarded, and neither is an agent outside the Squad
/// catalog. Any error allows, and the log says why (ADR 0072).
/// </remarks>
/// <param name="budgets">The per-subagent budget files.</param>
/// <param name="checkouts">Tells whether a write or a shell command lands in a canonical checkout while a run works elsewhere.</param>
/// <param name="hostConfig">Loads the project's configuration, for the budget it sets and whether it turns the Arbiter off.</param>
/// <param name="process">The operator's home.</param>
/// <param name="clock">The clock the budget window is measured on.</param>
/// <param name="state">Opens the ledgers whose dispatch headers the command allow-list reads.</param>
/// <param name="files">Probes for the <c>.git</c> entry that marks a repository root.</param>
public sealed class WorkerGuard(
    IWorkerBudgetStore budgets,
    IRunCheckout checkouts,
    IHostConfigLoader hostConfig,
    IProcessEnvironment process,
    TimeProvider clock,
    IArbiterStateFactory state,
    IFileSystem files) : IWorkerGuard
{
    /// <summary>The rule id a stray write is refused under.</summary>
    public const string CheckoutRuleId = WorkerGuardRules.CheckoutRuleId;

    /// <summary>The rule id an exhausted budget is refused under.</summary>
    public const string BudgetRuleId = WorkerGuardRules.BudgetRuleId;

    /// <summary>The status line a worker whose budget ran out returns.</summary>
    public const string BudgetExceededStatus = WorkerGuardRules.BudgetExceededStatus;

    /// <summary>The Copilot CLI harness token.</summary>
    public const string CopilotCliHarness = WorkerGuardRules.CopilotCliHarness;

    /// <summary>The Claude Code harness token.</summary>
    public const string ClaudeHarness = WorkerGuardRules.ClaudeHarness;

    /// <summary>The closing line a seat stopped by its budget writes, naming what it did not examine.</summary>
    public const string SeatBudgetLine = WorkerGuardRules.SeatBudgetLine;

    /// <summary>Whether the guard could refuse this event, decided from the event alone, so a process builds nothing for an event it would allow.</summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <returns>False only when the guard would allow without reading a file.</returns>
    public static bool NeedsDecision(string harness, string stdin) => WorkerGuardRules.NeedsDecision(harness, stdin);

    /// <inheritdoc/>
    public int Run(
        string harness,
        string stdin,
        TextWriter stdout,
        TextWriter log,
        string? homeDirectory = null,
        Func<DateTimeOffset>? now = null,
        Func<string, ArbiterDispatchBudgetConfig?>? budgetLoader = null,
        Func<string, ArbiterDispatchBudgetConfig?>? seatBudgetLoader = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            string home = homeDirectory ?? process.HomeDirectory;
            if (WorkerGuardEventReader.TurnEndOf(harness, stdin) is { } ended)
            {
                EndTurn(harness, ended, home);
                return 0;
            }

            WorkerGuardRules.ToolCall? call = WorkerGuardEventReader.Read(harness, stdin);
            if (call is null)
            {
                return 0;
            }

            bool seat = string.Equals(call.Agent.CapabilityProfile, WorkerGuardRules.SeatProfile, StringComparison.Ordinal);
            string? reason = BudgetRefusal(
                                 call,
                                 home,
                                 (now ?? (() => clock.GetUtcNow()))(),
                                 seat ? seatBudgetLoader ?? LoadSeatBudget : budgetLoader ?? LoadBudget)
                             ?? WriteRefusal(call, home)
                             ?? ShellCheckoutRefusal(call, home)
                             ?? CommandRefusal(call, home);
            if (reason is not null)
            {
                stdout.Write(WorkerGuardRules.Deny(harness, reason));
            }
        }
        catch (Exception ex)
        {
            TryLog(log, $"{HookCommand.FailClosedCode}: worker guard could not decide ({WorkerGuardRules.OneLine(ex.Message)}); the tool call continues.");
        }

        return 0;
    }

    /// <summary>
    /// Closes the budget window of a subagent whose turn ended. The budget measures one turn of work, so the time
    /// the agent then sits idle is not counted, and a turn the dispatching agent starts later (a rework sent to the
    /// same agent) begins a fresh window with its first tool call.
    /// </summary>
    private void EndTurn(string harness, string agentId, string home)
    {
        string key = WorkerGuardRules.KeyOf(harness, agentId);
        if (budgets.Exists(home, key))
        {
            budgets.Delete(home, key);
        }
    }

    /// <summary>
    /// The stray-write refusal, unless the project turns the Arbiter off. The configuration is read only when
    /// there is something to refuse, so an ordinary write costs no YAML parse.
    /// </summary>
    private string? WriteRefusal(WorkerGuardRules.ToolCall call, string home)
    {
        string? refusal = call.WritePath is null
            ? null
            : checkouts.StrayWriteRefusal(call.WritePath, call.Cwd ?? string.Empty, home);
        return refusal is not null && ArbiterTurnedOff(call.Cwd) ? null : refusal;
    }

    private bool ArbiterTurnedOff(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return false;
        }

        HostConfigLoadResult loaded = hostConfig.TryLoad(workingDirectory);
        return loaded.Success && loaded.Config?.Arbiter.ExplicitlyEnabled == false;
    }

    /// <summary>
    /// The canonical-checkout refusal for a shell command, or null. A command is refused when it would run in a
    /// repository's canonical checkout: its working directory is inside one, or it steps into one with a
    /// <c>cd</c>, <c>pushd</c> or <c>git -C</c>. The working directory is judged only when some step of the command
    /// actually runs in it (<see cref="WorkerGuardRules.RunsInWorkingDirectory" />): a <c>cd</c> or <c>pushd</c> that
    /// names a placeable directory moves the shell, a <c>git</c> step with <c>-C</c> names its own, a subshell's
    /// change ends at its closing <c>)</c>, a pipeline stage starts where the pipeline did, and a step after
    /// <c>||</c> may start where the previous step did. Every directory the command enters is judged either way.
    /// Only a live run of that repository that works in a worktree of its own makes it stray, and a read through a
    /// view or grep tool carries no command, so it is never judged here. The configuration is read only when there
    /// is something to refuse.
    /// </summary>
    private string? ShellCheckoutRefusal(WorkerGuardRules.ToolCall call, string home)
    {
        if (call.Command is not { Length: > 0 })
        {
            return null;
        }

        string? refusal = WorkerGuardRules.RunsInWorkingDirectory(call.Command)
            ? CheckoutRefusalAt(call.Cwd, workingDirectory: null, home)
            : null;
        if (refusal is null)
        {
            foreach (string directory in WorkerGuardRules.EnteredDirectories(call.Command))
            {
                refusal = CheckoutRefusalAt(directory, call.Cwd, home);
                if (refusal is not null)
                {
                    break;
                }
            }
        }

        return refusal is not null && ArbiterTurnedOff(call.Cwd) ? null : refusal;
    }

    /// <summary>The canonical-checkout refusal for one path, or null when it is outside every canonical checkout or no run is live there.</summary>
    private string? CheckoutRefusalAt(string? path, string? workingDirectory, string home)
    {
        string? absolute = AbsolutePath(path, workingDirectory);
        if (absolute is null || checkouts.CanonicalCheckoutContaining(absolute) is not { } checkout)
        {
            return null;
        }

        return checkouts.LiveSeparateRuns(home, checkout) is { Count: > 0 } runs
            ? WorkerGuardRules.StrayCommandRefusal(checkout, runs[0].Worktree)
            : null;
    }

    /// <summary>
    /// The absolute form of a path the command names, resolved against the call's working directory, or null when
    /// neither names a directory the guard can place.
    /// </summary>
    private static string? AbsolutePath(string? path, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string combined = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(workingDirectory)
            ? path
            : Path.Combine(workingDirectory, path);
        return Path.IsPathRooted(combined) ? Path.GetFullPath(combined) : null;
    }

    /// <summary>
    /// The refusal a technology's command allow-list makes for a read-only dispatch's command, or null. The
    /// configuration is read only for a profile the list judges, so an ordinary developer command costs no YAML parse.
    /// </summary>
    /// <remarks>
    /// A judged command with no working directory is refused rather than allowed: the technology's list lives under
    /// the working directory, so without one the guard cannot read what the command was allowed to run. An
    /// unreadable configuration is refused for the same reason: a list the guard cannot read is one it cannot apply,
    /// so a load failure never widens what a read-only dispatch may run. The event naming no directory, and the
    /// failed load, are themselves machine facts, which is what ADR 0072 permits a refusal on. The dispatch's
    /// technology scopes the list: a dispatch that names none, has returned, or cannot be told apart from a parallel
    /// one falls back to every declared technology's list, so a refusal is never lost.
    /// </remarks>
    private string? CommandRefusal(WorkerGuardRules.ToolCall call, string home)
    {
        if (!WorkerGuardRules.IsCommandJudged(call))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(call.Cwd))
        {
            return "The worker guard refused this command: the event named no working directory, so the " +
                "technology's allowed command forms could not be read. Nothing ran. Run it again from the " +
                "repository the list lives in, or report what you could not inspect.";
        }

        HostConfigLoadResult loaded = hostConfig.TryLoad(call.Cwd);
        if (!loaded.Success || loaded.Config is null)
        {
            return "The worker guard refused this command: the repository configuration at '" + call.Cwd +
                "' could not be read, so the technology's allowed command forms are unknown. Nothing ran. " +
                "Fix the configuration, or report what you could not inspect.";
        }

        if (loaded.Config.Arbiter.ExplicitlyEnabled == false)
        {
            return null;
        }

        string? technology = null;
        try
        {
            technology = ArbiterLedgerQueries.DispatchTechnology(
                LedgerEvents(call.Cwd, home), call.Agent.Name, call.SessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable ledger leaves the dispatch unknown, so every list applies.
        }

        // A dispatch can name a technology the host does not declare: a typo, or a folder it never installed. That
        // names no list, so every declared list must still bind the agent.
        if (technology is not null
            && !loaded.Config.Ontology.Technologies.Contains(technology, StringComparer.Ordinal))
        {
            technology = null;
        }

        IReadOnlyCollection<string>? scope = technology is null ? null : [technology];
        return WorkerGuardRules.CommandRefusal(
            loaded.Config.Arbiter.Rules,
            call,
            call.Command!,
            scope,
            loaded.Config.Arbiter.RuleFolderParents);
    }

    /// <summary>
    /// The ledger events that can name this call's dispatch: the project's and the user-level project ledger's,
    /// when they are readable. Anything unreadable leaves the dispatch unknown, so every list applies.
    /// </summary>
    private IReadOnlyList<ArbiterLedgerEvent> LedgerEvents(string cwd, string home)
    {
        List<ArbiterLedgerEvent> events = [];
        foreach (string directory in LedgerDirectories(cwd, home))
        {
            try
            {
                events.AddRange(state.OpenLedger(directory).ReadAll());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A ledger that cannot be read answers no dispatch, which is the fail-closed reading.
            }
        }

        return [.. events.OrderBy(e => e.At)];
    }

    /// <summary>The project and user-level directories one repository's ledger may sit in.</summary>
    private IReadOnlyList<string> LedgerDirectories(string cwd, string home)
    {
        string root = RepositoryRootOf(cwd);
        List<string> directories = [Path.Combine(root, "artifacts", "arbiter")];
        if (!string.IsNullOrWhiteSpace(home))
        {
            directories.Add(Path.Combine(
                home, ".kyber-weave", "arbiter", "projects", ArbiterHookDecisionEngine.ProjectRootKey(root, process.CurrentDirectory)));
        }

        return [.. directories.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The nearest ancestor holding <c>.git</c>, or the working directory when no ancestor does.</summary>
    private string RepositoryRootOf(string cwd)
    {
        string full = Path.GetFullPath(cwd, process.CurrentDirectory);
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
        {
            string git = Path.Combine(directory.FullName, ".git");
            if (files.DirectoryExists(git) || files.FileExists(git))
            {
                return directory.FullName;
            }
        }

        return full;
    }

    /// <summary>
    /// Counts this call against the subagent's budget and refuses it once the count or the elapsed time is past
    /// the limit. The first call writes the header (start time and limits); every call appends one timestamped
    /// line, marked with whether the call started a process, so parallel hook processes never lose a count. The
    /// per-call limit judges a call that started a process (a shell command) only, because only such a call can hang.
    /// </summary>
    /// <remarks>
    /// A harness may resume the same subagent for a rework, under the same id. Where the harness reports the end
    /// of a turn (Copilot CLI's <c>subagentStop</c>), <see cref="EndTurn"/> closes the window there. Elsewhere a
    /// window ends when the subagent has been idle longer than the budget's minutes, or for a minute after it ran
    /// past its budget (the worker returned): the resumed dispatch starts a fresh window.
    /// </remarks>
    private string? BudgetRefusal(
        WorkerGuardRules.ToolCall call,
        string home,
        DateTimeOffset now,
        Func<string, ArbiterDispatchBudgetConfig?> budgetLoader)
    {
        bool seat = string.Equals(call.Agent.CapabilityProfile, WorkerGuardRules.SeatProfile, StringComparison.Ordinal);
        if (!seat && !WorkerGuardRules.IsBudgeted(call.Agent.CapabilityProfile))
        {
            return null;
        }

        budgets.EnsureDirectory(home);
        string key = WorkerGuardRules.KeyOf(call.Harness, call.AgentId);
        string[] earlier = budgets.ReadLines(home, key);
        if (earlier.Length > 0 && WorkerGuardRules.WindowEnded(earlier, now))
        {
            budgets.Delete(home, key);
            earlier = [];
        }

        // The guard sees only the start of each call, so the gap since the previous one is how long that call (and
        // the model's turn after it) ran. Only a call that started a process, or waited on one, can hang, so the
        // per-call limit is judged on that call's marker. Read before this call's line is appended.
        (DateTimeOffset? previousCall, bool previousStartedProcess) =
            earlier.Length > 1 && WorkerGuardRules.Window.Read(earlier) is { } before
                ? (before.LastCall, before.LastCallStartedProcess)
                : ((DateTimeOffset?)null, false);

        if (!budgets.Exists(home, key))
        {
            ArbiterDispatchBudgetConfig? budget = budgetLoader(call.Cwd ?? string.Empty);
            if (budget is null)
            {
                return null;
            }

            budgets.Sweep(home, now, WorkerGuardRules.BudgetRetention);
            budgets.TryCreate(home, key, WorkerGuardRules.HeaderLine(now, budget, call.Agent.Name));
        }

        budgets.Append(home, key, WorkerGuardRules.CallLine(now, startedProcess: call.StartedProcess));

        string[] lines = budgets.ReadLines(home, key);
        if (WorkerGuardRules.Window.Read(lines) is not { } window)
        {
            return null;
        }

        TimeSpan elapsed = now - window.Start;
        if (!window.IsOver(now))
        {
            return previousCall is { } previous && previousStartedProcess && now - previous > TimeSpan.FromMinutes(window.CallMinutes)
                ? WorkerGuardRules.StalledCallRefusal(now - previous, window.CallMinutes)
                : null;
        }

        return seat && call.WritePath is not null
            ? null
            : WorkerGuardRules.ExhaustedRefusal(seat, writesSeatFile: false, window.Calls, elapsed, window.MaxCalls, window.Minutes);
    }

    /// <summary>
    /// The project's budget, or the shipped one; null when the project turns the Arbiter off. The per-call limit is
    /// raised to cover the longest task gate the project declares, so a gate the repository allows to run is never
    /// taken for a hung process.
    /// </summary>
    private ArbiterDispatchBudgetConfig? LoadBudget(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ArbiterDispatchBudgetConfig.Defaults;
        }

        HostConfigLoadResult loaded = hostConfig.TryLoad(workingDirectory);
        if (!loaded.Success || loaded.Config is null)
        {
            return ArbiterDispatchBudgetConfig.Defaults;
        }

        if (loaded.Config.Arbiter.ExplicitlyEnabled == false)
        {
            return null;
        }

        ArbiterDispatchBudgetConfig budget = loaded.Config.Arbiter.DispatchBudget;
        int callMinutes = WorkerGuardRules.EffectiveCallMinutes(budget, loaded.Config.Review);
        return callMinutes == budget.CallMinutes ? budget : budget with { CallMinutes = callMinutes };
    }

    /// <summary>The project's seat budget, or the shipped one; null when the project turns the Arbiter off.</summary>
    private ArbiterDispatchBudgetConfig? LoadSeatBudget(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ArbiterDispatchBudgetConfig.SeatDefaults;
        }

        HostConfigLoadResult loaded = hostConfig.TryLoad(workingDirectory);
        if (!loaded.Success || loaded.Config is null)
        {
            return ArbiterDispatchBudgetConfig.SeatDefaults;
        }

        return loaded.Config.Arbiter.ExplicitlyEnabled == false ? null : loaded.Config.Arbiter.SeatBudget;
    }

    private static void TryLog(TextWriter log, string message)
    {
        try
        {
            log.WriteLine(message);
        }
        catch (Exception)
        {
            // The log is best effort.
        }
    }
}
