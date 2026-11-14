using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>One task of the active plan, with its Test contract row, as the packet and the task gate both read it.</summary>
/// <param name="Plan">The plan, repository-relative.</param>
/// <param name="Entry">The task's entry in the plan's task file.</param>
/// <param name="Row">The task's Test contract row, or null when the plan has none for it.</param>
public sealed record PlanTaskTargetMatch(string Plan, PlanTaskFileEntry Entry, PlanContractRow? Row);

/// <summary>
/// Finds a task by the id a worker typed, and what its tests are: the one lookup <c>docs packet</c> prints the test
/// command from and <c>review gates --task</c> runs it from, so the two never disagree.
/// </summary>
public static partial class PlanTaskTarget
{
    /// <summary>The task in the one Ready or In progress plan, or null with the reason when there is not exactly one match.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="typedTask">The task id as the worker typed it, for example <c>T1</c>.</param>
    /// <param name="reason">Why nothing was found; null when a task was found.</param>
    public static PlanTaskTargetMatch? Find(string root, KyberWeaveConfig config, string typedTask, out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(typedTask);

        IReadOnlyList<string> active = PlanTaskPacket.ActivePlans(root, config);
        if (active.Count != 1)
        {
            reason = active.Count == 0
                ? "no plan is Ready or In progress in this checkout"
                : $"{active.Count} plans are Ready or In progress ({string.Join(", ", active)})";
            return null;
        }

        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(root, config).Run(active[0], PlanTaskFileMode.Check);
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

    /// <summary>The task's Test contract row in <paramref name="plan"/>, or null when it has none or the plan cannot be read.</summary>
    public static PlanContractRow? ContractRow(string root, string plan, string? taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan);
        try
        {
            string markdown = File.ReadAllText(Path.Combine(root, plan.Replace('/', Path.DirectorySeparatorChar)));
            return PlanDocumentParser.Parse(markdown).ContractRows
                .FirstOrDefault(row => string.Equals(row.TaskId, taskId, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The task's own files as the gate takes them: literal paths only (a glob names no file) that pass the gate's path
    /// check. Files the task has not created yet are kept, because a test-first task's failing run comes before them.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="entry">The task.</param>
    /// <param name="row">
    /// The task's Test contract row. A test file it names is the task's own too, even when the task's file list leaves
    /// it out, so a compile error in it reads as this task's failing run.
    /// </param>
    public static IReadOnlyList<string> Files(string root, PlanTaskFileEntry entry, PlanContractRow? row = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string? contractFile = row?.TestProjectOrFile?.Trim().Trim('`').Trim();
        IEnumerable<string> candidates = entry.Files ?? [];
        if (!string.IsNullOrEmpty(contractFile) && Path.HasExtension(contractFile) && !contractFile.Contains(' ', StringComparison.Ordinal))
            candidates = candidates.Append(contractFile);

        return [.. candidates
            .Where(path => path.IndexOfAny(['*', '?', '[']) < 0 && TaskGateSelection.ValidatePath(root, path) is null)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The test classes the task's Test contract row names in a <c>FullyQualifiedName</c> filter, in order.</summary>
    public static IReadOnlyList<string> DeclaredClasses(PlanContractRow? row)
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
