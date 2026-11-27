using System.Globalization;

namespace KyberWeave.Core;

/// <summary>
/// The scratch folder a run keeps its working state under, and every relative path inside it that more than one
/// component writes or reads: the task folder, its review cycles, gate reports and logs.
/// </summary>
/// <remarks>
/// Paths are repository-relative and use <c>/</c> on every platform, so the same text is written into a prompt or a
/// message as well as combined with a checkout root. Which task id spelling names a folder is the caller's decision;
/// this type only joins the parts.
/// </remarks>
public static class ScratchLayout
{
    /// <summary>The run's scratch folder, relative to the checkout root and ignored by git.</summary>
    public const string Root = ".agent_scratchpad";

    /// <summary>The folder inside a task folder that holds a review's cycles.</summary>
    public const string ReviewFolderName = "review";

    /// <summary>The prefix of a cycle folder's name.</summary>
    public const string CycleFolderPrefix = "cycle-";

    /// <summary>A cycle folder's name without the number: the text every parser matches and then reads a number from.</summary>
    public const string CycleFolderPattern = CycleFolderPrefix + @"\d+";

    /// <summary>A task's passing gate report file name.</summary>
    public const string TaskGateFileName = "task-gate.json";

    /// <summary>A task's failing-run gate report file name.</summary>
    public const string TaskGateRedFileName = "task-gate-red.json";

    /// <summary>The file counting a task's gate runs.</summary>
    public const string TaskGateRunsFileName = "task-gate-runs";

    /// <summary>The file a retry's prior results are written to.</summary>
    public const string PriorResultsFileName = "prior-results.md";

    /// <summary>The folder a task's gate logs are written to.</summary>
    public const string LogsFolderName = "logs";

    /// <summary>The gate report file name inside a cycle folder.</summary>
    public const string GatesFileName = "gates.json";

    /// <summary>The task's folder inside the scratch folder.</summary>
    /// <param name="task">The task's folder name, already normalised when the caller normalises task ids.</param>
    public static string TaskDir(string task) => $"{Root}/{task}";

    /// <summary>The task's review folder, which holds its cycle folders.</summary>
    /// <param name="task">The task's folder name.</param>
    public static string ReviewDir(string task) => $"{TaskDir(task)}/{ReviewFolderName}";

    /// <summary>The name of one cycle's folder.</summary>
    /// <param name="cycle">The cycle number, counting from 1.</param>
    public static string CycleFolder(int cycle) => CycleFolderPrefix + cycle.ToString(CultureInfo.InvariantCulture);

    /// <summary>One cycle's folder inside the task's review folder.</summary>
    /// <param name="task">The task's folder name.</param>
    /// <param name="cycle">The cycle number, counting from 1.</param>
    public static string CycleDir(string task, int cycle) => $"{ReviewDir(task)}/{CycleFolder(cycle)}";

    /// <summary>The gate report the council writes into one cycle's folder.</summary>
    /// <param name="task">The task's folder name.</param>
    /// <param name="cycle">The cycle number, counting from 1.</param>
    public static string CycleGates(string task, int cycle) => $"{CycleDir(task, cycle)}/{GatesFileName}";

    /// <summary>A task's gate report, passing or failing-run.</summary>
    /// <param name="task">The task's folder name.</param>
    /// <param name="red">Whether the failing-run report (<c>--red</c>) is named.</param>
    public static string TaskGateReport(string task, bool red) =>
        $"{TaskDir(task)}/{(red ? TaskGateRedFileName : TaskGateFileName)}";

    /// <summary>The file counting how many gate runs a task has had.</summary>
    /// <param name="task">The task's folder name.</param>
    public static string TaskGateRuns(string task) => $"{TaskDir(task)}/{TaskGateRunsFileName}";

    /// <summary>The folder a task's gate logs are written to.</summary>
    /// <param name="task">The task's folder name.</param>
    public static string LogsDir(string task) => $"{TaskDir(task)}/{LogsFolderName}";

    /// <summary>The file a retry's prior results are read from.</summary>
    /// <param name="task">The task's folder name.</param>
    public static string PriorResults(string task) => $"{TaskDir(task)}/{PriorResultsFileName}";

    /// <summary>The review run id a dispatch names, built from the task and the cycle it belongs to.</summary>
    /// <param name="task">The task's folder name.</param>
    /// <param name="cycle">The cycle number, counting from 1.</param>
    public static string ReviewRun(string task, int cycle) => $"{task}-{CycleFolder(cycle)}";

    /// <summary>Reads a review run id back into its task folder name and cycle number.</summary>
    /// <param name="reviewRun">The id a dispatch's <c>REVIEW_RUN</c> header carries.</param>
    /// <param name="task">The task folder name, when the id is one.</param>
    /// <param name="cycle">The cycle number, when the id is one.</param>
    /// <returns>True when the id ends in <c>-cycle-&lt;n&gt;</c> with a positive number.</returns>
    public static bool TryParseReviewRun(string? reviewRun, out string task, out int cycle)
    {
        task = string.Empty;
        cycle = 0;
        if (string.IsNullOrWhiteSpace(reviewRun))
            return false;

        int separator = reviewRun.LastIndexOf("-" + CycleFolderPrefix, StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        string number = reviewRun[(separator + 1 + CycleFolderPrefix.Length)..];
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            return false;

        task = reviewRun[..separator];
        cycle = parsed;
        return true;
    }
}
