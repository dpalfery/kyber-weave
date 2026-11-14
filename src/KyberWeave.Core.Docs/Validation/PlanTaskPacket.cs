using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// What a worker is handed for one task, and how it is found: the plan to read it from, the task the worker named,
/// and the packet with the two facts no plan can know filled in.
/// </summary>
/// <remarks>
/// A worker may not open a plan, and a conductor that retypes a 33 KB packet loses words, so the machine prints the
/// packet. The tolerant parts live here, once, so the command and its tests agree on what a worker may type.
/// </remarks>
public static partial class PlanTaskPacket
{
    /// <summary>What stands in for the prior-results slot when the caller supplied none.</summary>
    public const string NoPriorResults = "(none supplied: this packet was printed without the results of earlier tasks)";

    private const string WorktreeSlot = "{worktree}";
    private const string PriorResultsSlot = "{prior_results}";
    private const string TaskList = "tasks.md";

    /// <summary>
    /// The plans and specifications that are Ready or In progress, repository-relative and sorted: files directly in a
    /// planning folder, and each specification folder's <c>tasks.md</c>.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="config">The effective configuration, which says where plans live.</param>
    public static IReadOnlyList<string> ActivePlans(string repoRoot, KyberWeaveConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        string root = Path.GetFullPath(repoRoot);
        List<string> found = [];
        foreach (string folder in new PlanPathResolver(root, config).PlanningFolders())
        {
            string directory = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> candidates = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .Where(file => !string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
                .Concat(Directory.EnumerateDirectories(directory)
                    .Select(sub => Path.Combine(sub, TaskList))
                    .Where(File.Exists));
            foreach (string file in candidates)
            {
                if (IsActive(file))
                {
                    found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The task a worker named, accepting the spellings an agent types: <c>T1</c>, <c>t1</c>, <c>task 1</c>,
    /// <c>Task-1</c>, or the bare number when the ids are a letter and a number.
    /// </summary>
    /// <param name="file">The task file.</param>
    /// <param name="typed">What the worker typed.</param>
    /// <returns>The entry, or null when none matches or the spelling fits more than one.</returns>
    public static PlanTaskFileEntry? FindTask(PlanTaskFile file, string typed)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(typed);

        string key = Key(typed);
        if (key.Length == 0)
        {
            return null;
        }

        IReadOnlyList<PlanTaskFileEntry> tasks = file.Tasks ?? [];
        List<PlanTaskFileEntry> exact = [.. tasks.Where(task => Key(task.Id ?? string.Empty) == key)];
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count == 0 && key.All(char.IsAsciiDigit))
        {
            List<PlanTaskFileEntry> numbered = [.. tasks.Where(task => Number(task.Id) == key)];
            if (numbered.Count == 1)
            {
                return numbered[0];
            }
        }

        return null;
    }

    /// <summary>The packet with the worktree and the prior results filled in; nothing else changes.</summary>
    /// <param name="entry">The task.</param>
    /// <param name="worktree">The absolute path of the worktree the worker is in.</param>
    /// <param name="priorResults">The earlier tasks' reported results, or null when none were supplied.</param>
    public static string Fill(PlanTaskFileEntry entry, string worktree, string? priorResults)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);

        string prior = string.IsNullOrWhiteSpace(priorResults) ? NoPriorResults : priorResults.Trim();
        return (entry.Prompt ?? string.Empty)
            .Replace(WorktreeSlot, worktree, StringComparison.Ordinal)
            .Replace(PriorResultsSlot, prior, StringComparison.Ordinal);
    }

    /// <summary>
    /// One line for a task: id, the abilities it needs, the agent the plan assigned or the model picked, what it
    /// depends on, its session and how many files it scopes.
    /// </summary>
    /// <param name="entry">The task.</param>
    public static string ListLine(PlanTaskFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string depends = entry.DependsOn is { Count: > 0 } ? string.Join(',', entry.DependsOn) : "none";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Id}  needs={NeedsOf(entry)}{RoleOf(entry, "  ")}{AgentOf(entry, "  ")}  depends-on={depends}  session={entry.Session ?? "-"}  files={entry.Files?.Count ?? 0}");
    }

    /// <summary>
    /// The queue a conductor dispatches from: one line per task (id, title, the abilities it needs, the agent the plan
    /// assigned or the decision model picked, what it waits on, session), then the tasks with no dependency, which may run
    /// in parallel. Where the line names no agent the conductor chooses one from the abilities, since it never opens the plan.
    /// </summary>
    /// <param name="tasks">The task file's tasks.</param>
    public static string ReadyQueue(IReadOnlyList<PlanTaskFileEntry> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        StringBuilder queue = new();
        foreach (PlanTaskFileEntry task in tasks)
        {
            string depends = task.DependsOn is { Count: > 0 } ? string.Join(',', task.DependsOn) : "none";
            queue.Append(CultureInfo.InvariantCulture, $"{task.Id}: {TitleOf(task)} | needs: {NeedsOf(task)}{RoleOf(task, " | ")}{AgentOf(task, " | ")} | after={depends}");
            if (task.Session is not null)
                queue.Append(CultureInfo.InvariantCulture, $" | session={task.Session}");
            queue.Append('\n');
        }

        string[] ready = [.. tasks.Where(task => task.DependsOn is not { Count: > 0 }).Select(task => task.Id ?? string.Empty)];
        queue.Append(ready.Length > 1
            ? $"ready now, may run in parallel: {string.Join(", ", ready)}"
            : $"ready now: {string.Join(", ", ready)}");
        return queue.ToString();
    }

    private static string NeedsOf(PlanTaskFileEntry task) =>
        string.IsNullOrWhiteSpace(task.Capabilities) ? "-" : task.Capabilities;

    private static string RoleOf(PlanTaskFileEntry task, string separator) =>
        task.Role is null ? string.Empty : $"{separator}role={task.Role}";

    private static string AgentOf(PlanTaskFileEntry task, string separator) =>
        task.Role is null && task.Agent is not null ? $"{separator}agent={task.Agent}" : string.Empty;

    private static string TitleOf(PlanTaskFileEntry task)
    {
        string first = (task.Prompt ?? string.Empty).Split('\n', 2)[0].Trim();
        string prefix = $"Task {task.Id}";
        return first.StartsWith(prefix, StringComparison.Ordinal) ? first[prefix.Length..].TrimStart(':', ' ') : first;
    }

    private static bool IsActive(string file)
    {
        try
        {
            return ActiveStatusRegex().IsMatch(File.ReadAllText(file));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Key(string value)
    {
        string key = NonAlphanumericRegex().Replace(value, string.Empty).ToUpperInvariant();
        return key.StartsWith("TASK", StringComparison.Ordinal) && key.Length > 4 ? key[4..] : key;
    }

    private static string Number(string? id)
    {
        Match match = TrailingNumberRegex().Match(Key(id ?? string.Empty));
        return match.Success ? match.Groups["n"].Value.TrimStart('0') : string.Empty;
    }

    [GeneratedRegex(@"\*\*Status:(?:\*\*)?\s*(?:Ready|In[ -]progress)(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveStatusRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"^[A-Z]*(?<n>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNumberRegex();
}
