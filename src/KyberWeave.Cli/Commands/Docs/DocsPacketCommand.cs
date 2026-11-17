using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console.Cli;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Prints one task's packet for the worker that was dispatched for it, so a conductor passes a task id and nothing
/// else, and a worker, which may not open a plan, still gets the whole brief.
/// </summary>
public sealed class DocsPacketCommand(IPlanTaskService tasks, IPacketHostFacts hostFacts, IFileSystem files, IProcessEnvironment environment) : Command<DocsPacketSettings>
{
    /// <summary>The plan could not be chosen: none is Ready or In progress, or several are.</summary>
    public const string PlanNotChosen = "KW-DOC-TASKS-CLI-002";

    /// <summary>The task id matches no task, or more than one.</summary>
    public const string TaskNotFound = "KW-DOC-TASKS-CLI-003";

    /// <summary>The task file is missing or no longer matches its plan, so the packet would hand out stale work.</summary>
    public const string TaskFileStale = "KW-DOC-TASKS-CLI-004";

    /// <summary>The prior-results file could not be read, or lies outside the run's scratch area.</summary>
    public const string PriorResultsUnreadable = "KW-DOC-TASKS-CLI-005";

    /// <summary>
    /// Prints the packet of the named task: the built prompt, then the task's resolved test command
    /// (<see cref="PacketTestCommand" />). The prompt has <c>{worktree}</c> filled in and the prior-results slot
    /// filled from <c>--prior-results</c>, or labelled as empty. The plan comes from <c>--plan</c>, else from the one Ready or In progress plan under the planning
    /// folders. When this checkout has none, the repository's other git worktrees are searched for the one active
    /// plan that has the task, so a worker whose shell is not in its worktree still gets its packet. The task file is checked against the plan first, so a stale file is refused with the fix.
    /// Exits 0 after printing; exits 1 with the reason and the next command otherwise.
    /// </summary>
    public override int Execute(CommandContext context, DocsPacketSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        string current = Path.GetFullPath(settings.WorkingDirectory ?? environment.CurrentDirectory);
        string root = settings.Plan is null ? ArbiterRepoRoot.Find(current) : PlanArguments.RepositoryRootFor(settings.Plan, current);
        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            return Fail(report);
        }

        string? plan = ChoosePlan(root, config, settings, current, report);
        if (plan is null && settings.Plan is null && tasks.ActivePlans(root, config).Count == 0)
        {
            plan = FindInOtherWorktrees(ref root, config, settings, current, report);
        }

        if (plan is null)
        {
            return Fail(report);
        }

        PlanTaskFileOutcome outcome = tasks.Run(root, config, plan, PlanTaskFileMode.Check);
        if (outcome.File is null || outcome.Report.HasErrors)
        {
            report.AddRange(outcome.Report.Items);
            report.Add(new Diagnostic(
                TaskFileStale,
                Severity.Error,
                $"No packet printed: the task file for {plan} is missing or does not match the plan.",
                "docs packet",
                plan,
                Hint: $"Run 'kyber-weave docs tasks {plan} --write', then run this command again. If it still fails, report this output unchanged."));
            return Fail(report);
        }

        PlanTaskFileEntry? entry = PlanTaskPacket.FindTask(outcome.File, settings.Task);
        if (entry is null)
        {
            string ids = string.Join(", ", (outcome.File.Tasks ?? []).Select(task => task.Id));
            report.Add(new Diagnostic(
                TaskNotFound,
                Severity.Error,
                $"'{settings.Task}' names no one task in {plan}. Its tasks are: {ids}.",
                "docs packet",
                plan,
                Hint: "Use the task id from the dispatch, for example T1."));
            return Fail(report);
        }

        if (settings.PriorResults is not null)
        {
            // Checked here so a refusal is reported under the packet's own code, before any prompt is built;
            // the service applies the same rule when it reads the file for the <task> part.
            if (!TaskPromptService.IsScratchFile(root, settings.PriorResults))
            {
                report.Add(new Diagnostic(
                    PriorResultsUnreadable,
                    Severity.Error,
                    $"The prior-results file '{settings.PriorResults}' is outside this run's scratch area. " +
                    $"Write it under {TaskPromptService.ScratchFolder} in the checkout, name it relative to that folder, or leave it out.",
                    "docs packet",
                    Hint: $"Keep the file under {TaskPromptService.ScratchFolder} in this checkout."));
                return Fail(report);
            }

            try
            {
                _ = files.ReadAllText(Path.GetFullPath(settings.PriorResults, root));
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                report.Add(new Diagnostic(
                    PriorResultsUnreadable,
                    Severity.Error,
                    $"The prior-results file '{settings.PriorResults}' could not be read: {ex.Message}",
                    "docs packet",
                    Hint: "Pass a file that exists, or leave --prior-results out."));
                return Fail(report);
            }
        }

        // The prompt is built at print time, never written into the task file, so the digest is unaffected.
        DocumentIndex index = DocumentIndex.Build(
            new DocumentLoader(root, config.Ontology).Load(),
            CodeGraphResolvers.ForRepository(root).Resolver);
        TaskPromptService prompts = new(tasks, hostFacts, files, index.StandardsFor);
        TaskPromptResult prompt = prompts.Build(
            new TaskPromptRequest(root, settings.Task, plan, settings.Agent, settings.PriorResults),
            config);

        if (!prompt.Built)
        {
            report.Add(new Diagnostic(
                TaskFileStale,
                Severity.Error,
                prompt.Refusal ?? $"No packet printed: the prompt for {settings.Task} could not be built.",
                "docs packet",
                plan,
                Hint: $"Run 'kyber-weave docs tasks {plan} --write', then run this command again. If it still fails, report this output unchanged."));
            return Fail(report);
        }

        Console.Out.WriteLine(prompt.Prompt);

        // The test command is a packet extra, not part of the prompt the hook inserts, so it is printed here.
        if (PacketTestCommand.Render(
                root,
                config,
                prompt.Agent,
                entry,
                PlanTaskTarget.ContractRow(root, plan, entry.Id),
                OperatingSystem.IsWindows()) is { } tests)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(tests);
        }

        return 0;
    }

    private string? ChoosePlan(string root, KyberWeaveConfig config, DocsPacketSettings settings, string current, DiagnosticReport report)
    {
        if (settings.Plan is not null)
        {
            return PlanArguments.TryResolve(root, config, settings.Plan, current, "docs packet", report, out string relative)
                ? relative
                : null;
        }

        IReadOnlyList<string> active = tasks.ActivePlans(root, config);
        if (active.Count == 1)
        {
            return active[0];
        }

        if (active.Count == 0)
        {
            // Not reported here: the caller still searches the repository's other worktrees.
            return null;
        }

        report.Add(new Diagnostic(
            PlanNotChosen,
            Severity.Error,
            $"{active.Count} plans are Ready or In progress: {string.Join(", ", active)}.",
            "docs packet",
            Hint: "Run this command again with --plan <one of those plans>."));
        return null;
    }

    private string? FindInOtherWorktrees(ref string root, KyberWeaveConfig config, DocsPacketSettings settings, string current, DiagnosticReport report)
    {
        string here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        List<(string Worktree, string Plan)> matches = [];
        foreach (string worktree in (settings.ListWorktrees ?? (dir => new GitFacts().ListWorktrees(dir)))(root))
        {
            string other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
            if (string.Equals(other, here, StringComparison.Ordinal) || !files.DirectoryExists(other))
            {
                continue;
            }

            foreach (string candidate in tasks.ActivePlans(other, config))
            {
                PlanTaskFileOutcome outcome = tasks.Run(other, config, candidate, PlanTaskFileMode.Check);
                if (outcome.File is not null && PlanTaskPacket.FindTask(outcome.File, settings.Task) is not null)
                {
                    matches.Add((other, candidate));
                }
            }
        }

        if (matches.Count == 1)
        {
            root = matches[0].Worktree;
            return matches[0].Plan;
        }

        string searched = $"No plan is Ready or In progress under the planning folders of {here} (the directory searched was {current}).";
        if (matches.Count == 0)
        {
            report.Add(new Diagnostic(
                PlanNotChosen,
                Severity.Error,
                $"{searched} No other git worktree of this repository has an active plan with task '{settings.Task}' either, so there is no packet to print.",
                "docs packet",
                Hint: "Report this to the conductor; do not look for the plan yourself."));
            return null;
        }

        string forms = string.Join("; ", matches.Select(match => $"cd {match.Worktree} && kyber-weave docs packet {settings.Task}"));
        report.Add(new Diagnostic(
            PlanNotChosen,
            Severity.Error,
            $"{searched} {matches.Count} other worktrees have an active plan with task '{settings.Task}': {string.Join(", ", matches.Select(match => match.Worktree))}.",
            "docs packet",
            Hint: $"Run the command from the worktree named in the dispatch: {forms}."));
        return null;
    }

    private static int Fail(DiagnosticReport report)
    {
        ReportRenderer.Render(report, OutputFormat.Lines, "docs packet", "Task packet");
        return 1;
    }
}
