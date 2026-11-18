using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Hooks;

namespace KyberWeave.Application.Hooks;

/// <summary>Routes an edited file to its lint and test commands and runs them inside a time budget.</summary>
/// <remarks>
/// The hook fails open: any internal error, unreadable config, or overrun of the hook's own time returns exit code 0,
/// so a broken hook never blocks the agent. Only a failing blocking check returns exit code 2.
/// </remarks>
public sealed class EditHookService(
    IFileSystem fileSystem,
    IHooksConfigSource configSource,
    ITestClassResolver testClassResolver,
    IProcessRunner processRunner,
    TimeProvider timeProvider,
    IPathResolver paths,
    IProcessEnvironment environment) : IEditHookService
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<HookResult> RunAsync(HookEditEvent edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        try
        {
            return await RunCoreAsync(edit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new HookResult(0, "kyber-weave-hooks: stopped before finishing; the edit is not blocked.");
        }
        catch (Exception ex)
        {
            return new HookResult(0, $"kyber-weave-hooks: skipped after an internal error ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private async Task<HookResult> RunCoreAsync(HookEditEvent edit, CancellationToken cancellationToken)
    {
        string baseDirectory = string.IsNullOrWhiteSpace(edit.Cwd) ? environment.CurrentDirectory : edit.Cwd;
        string fullPath = paths.GetFullPath(edit.FilePath, baseDirectory);
        string sessionDirectory = string.IsNullOrWhiteSpace(edit.ProjectDirectory) ? baseDirectory : paths.GetFullPath(edit.ProjectDirectory, baseDirectory);
        string? fileDirectory = Path.GetDirectoryName(fullPath);
        if (fileDirectory is null || !PathContainment.IsWithin(sessionDirectory, fullPath))
        {
            return HookResult.Silent;
        }

        string? root = ProjectLocator.FindRepositoryRoot(fileSystem, fileDirectory);
        if (root is null || !PathContainment.IsWithin(sessionDirectory, root) || !fileSystem.FileExists(fullPath))
        {
            return HookResult.Silent;
        }

        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal))
        {
            return HookResult.Silent;
        }

        HooksConfig config = configSource.Load(root);
        IReadOnlyList<HookRule> matching = HookRouter.MatchingRules(config, relative);
        if (matching.Count == 0)
        {
            return HookResult.Silent;
        }

        TestClassResolution tests = matching.Any(r => r.Kind == HookRuleKind.Test)
            ? testClassResolver.Resolve(root, relative)
            : TestClassResolution.None;
        ProjectFacts facts = new(
            root,
            ProjectLocator.FindMarker(fileSystem, root, fileDirectory),
            ProjectLocator.FindProject(fileSystem, root, fileDirectory),
            tests.ProjectPath,
            tests.Classes);

        List<string> notes = [.. config.Warnings];
        IReadOnlyList<PlannedCommand> plan = HookRouter.Plan(matching, relative, facts, notes);
        if (plan.Count == 0 && notes.Count == config.Warnings.Count)
        {
            return HookResult.Silent;
        }

        TimeSpan budget = TimeSpan.FromSeconds(config.TimeBudgetSeconds);
        using CancellationTokenSource limitTimer = new(budget + Grace, timeProvider);
        using CancellationTokenSource hookLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limitTimer.Token);

        if (tests.Note is not null)
        {
            notes.Add(tests.Note);
        }

        List<RuleOutcome> outcomes = [];
        try
        {
            await RunPlanAsync(plan, config, root, budget, outcomes, notes, hookLimit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limitTimer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            notes.Add("the hook's own time limit was reached; the remaining check(s) were skipped for time.");
        }

        string feedback = HookFeedbackFormatter.Format(relative, outcomes, notes, config.MaxOutputLines, config.MaxOutputBytes);
        bool blocked = outcomes.Any(o => o.Failed && o.Blocking);
        return new HookResult(blocked ? 2 : 0, feedback);
    }

    private async Task RunPlanAsync(
        IReadOnlyList<PlannedCommand> plan,
        HooksConfig config,
        string root,
        TimeSpan budget,
        List<RuleOutcome> outcomes,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        long start = timeProvider.GetTimestamp();
        for (int i = 0; i < plan.Count; i++)
        {
            PlannedCommand command = plan[i];
            TimeSpan remaining = budget - timeProvider.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                notes.Add($"time budget of {config.TimeBudgetSeconds}s used up; {plan.Count - i} check(s) skipped.");
                break;
            }

            TimeSpan timeout = TimeSpan.FromSeconds(command.TimeoutSeconds);
            try
            {
                ProcessRunResult result = await processRunner
                    .RunAsync(command.Argv, root, timeout < remaining ? timeout : remaining, cancellationToken)
                    .ConfigureAwait(false);
                outcomes.Add(new RuleOutcome(command.RuleId, command.Kind, command.Blocking, result.ExitCode, result.TimedOut, result.Output));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string cause = ex.InnerException is { } inner ? $"; caused by {inner.GetType().Name}: {inner.Message}" : string.Empty;
                notes.Add($"rule '{command.RuleId}' could not run ({ex.GetType().Name}: {ex.Message}{cause}).");
                continue;
            }

            if (config.StopOnFirstBlocking && outcomes[^1].Failed && command.Blocking)
            {
                break;
            }
        }
    }
}
