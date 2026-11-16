using System.Text.RegularExpressions;
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
public sealed partial class PlanTaskTargetService(IPlanTaskService tasks, ITaskGateSelectionService taskGates) : IPlanTaskTargetService
{
    /// <inheritdoc />
    public PlanTaskTargetMatch? Find(string root, KyberWeaveConfig config, string typedTask, out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(typedTask);

        IReadOnlyList<string> active = tasks.ActivePlans(root, config);
        if (active.Count != 1)
        {
            reason = active.Count == 0
                ? "no plan is Ready or In progress in this checkout"
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

    /// <inheritdoc />
    public PlanContractRow? ContractRow(string root, string plan, string? taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        try
        {
            string markdown = BoundedFile.ReadAllText(Path.Combine(root, plan.Replace('/', Path.DirectorySeparatorChar)));
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
