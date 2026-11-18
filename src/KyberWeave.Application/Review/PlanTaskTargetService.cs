using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>
/// Finds a task by the id a worker typed, and what its tests are, over the plans that are Ready or In progress.
/// </summary>
/// <param name="tasks">Reads the active plans and their task files.</param>
/// <param name="taskGates">Checks a path the way the task gate will take it.</param>
/// <param name="git">Lists the repository's worktrees, so the run's plan is found from a worker's checkout.</param>
/// <param name="files">Reads a plan and tells a worktree directory that exists from one that does not.</param>
/// <param name="paths">Resolves a worktree's spelling to the full path compared against the checkout.</param>
/// <param name="environment">Names the directory a relative path is resolved against.</param>
public sealed partial class PlanTaskTargetService(
    IPlanTaskService tasks,
    ITaskGateSelectionService taskGates,
    IGitRepositoryFacts git,
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IPlanTaskTargetService
{
    /// <inheritdoc />
    public PlanTaskTargetMatch? Find(string root, KyberWeaveConfig config, string typedTask, out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(typedTask);

        IReadOnlyList<string> active = tasks.ActivePlans(root, config);
        if (active.Count == 0 && FindInOtherWorktree(root, config, typedTask) is { } elsewhere)
        {
            reason = null;
            return elsewhere;
        }

        if (active.Count != 1)
        {
            reason = active.Count == 0
                ? "no plan is Ready or In progress in this checkout or any other worktree of this repository"
                : $"{active.Count} plans are Ready or In progress ({string.Join(", ", active)})";
            return null;
        }

        PlanTaskFileOutcome outcome = tasks.Run(root, config, active[0], PlanTaskFileMode.Check);
        if (outcome.File is null || outcome.Report.HasErrors)
        {
            reason = $"the task file for {active[0]} is missing or does not match the plan";
            return null;
        }

        if (PlanTaskPacket.FindTask(outcome.File, typedTask) is not { } entry)
        {
            reason = $"'{typedTask}' names no one task in {active[0]}";
            return null;
        }

        reason = null;
        return new PlanTaskTargetMatch(active[0], entry, ContractRow(root, active[0], entry.Id));
    }

    /// <summary>
    /// The task in a plan that is active in one other worktree of this repository, or null when none has it or more
    /// than one does. The conductor's plan and task file live in the run's worktree, while a worker runs its task gate
    /// in a worktree of its own; <c>docs packet</c> reads the plan from there, so the gate reads it the same way.
    /// </summary>
    private PlanTaskTargetMatch? FindInOtherWorktree(string root, KyberWeaveConfig config, string typedTask)
    {
        string here = Path.TrimEndingDirectorySeparator(paths.GetFullPath(root, environment.CurrentDirectory));
        List<PlanTaskTargetMatch> matches = [];
        foreach (string worktree in git.ListWorktrees(root))
        {
            string other = Path.TrimEndingDirectorySeparator(paths.GetFullPath(worktree, environment.CurrentDirectory));
            if (string.Equals(other, here, StringComparison.Ordinal) || !files.DirectoryExists(other))
                continue;

            IReadOnlyList<string> active = tasks.ActivePlans(other, config);
            if (active.Count != 1)
                continue;

            PlanTaskFileOutcome outcome = tasks.Run(other, config, active[0], PlanTaskFileMode.Check);
            if (outcome.File is null || outcome.Report.HasErrors)
                continue;

            if (PlanTaskPacket.FindTask(outcome.File, typedTask) is { } entry)
                matches.Add(new PlanTaskTargetMatch(active[0], entry, ContractRow(other, active[0], entry.Id)));
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <inheritdoc />
    public PlanContractRow? ContractRow(string root, string plan, string? taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        try
        {
            using Stream stream = files.OpenRead(Path.Combine(root, plan.Replace('/', Path.DirectorySeparatorChar)));
            string markdown = BoundedFileReader.ReadAllText(stream, BoundedFile.MaxBytes);
            return PlanDocumentParser.Parse(markdown).ContractRows
                .FirstOrDefault(row => string.Equals(row.TaskId, taskId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Files(string root, PlanTaskFileEntry entry, PlanContractRow? row = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string? contractFile = row?.TestProjectOrFile?.Trim().Trim('`').Trim();
        IEnumerable<string> candidates = entry.Files ?? [];
        if (!string.IsNullOrEmpty(contractFile) && Path.HasExtension(contractFile) && !contractFile.Contains(' ', StringComparison.Ordinal))
            candidates = candidates.Append(contractFile);

        return [.. candidates
            .Where(path => path.IndexOfAny(['*', '?', '[']) < 0 && taskGates.ValidatePath(root, path) is null)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DeclaredClasses(PlanContractRow? row)
    {
        List<string> classes = [];
        foreach (string? cell in new[] { row?.RunnerFilter, row?.TestProjectOrFile })
        {
            foreach (Match match in FilterClassRegex().Matches(cell ?? string.Empty))
            {
                if (!classes.Contains(match.Groups["name"].Value))
                    classes.Add(match.Groups["name"].Value);
            }
        }

        return classes;
    }

    [GeneratedRegex(@"FullyQualifiedName[~=](?<name>[A-Za-z_][\w.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex FilterClassRegex();
}
