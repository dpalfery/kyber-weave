using System.Text;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// The command that runs one task's tests, resolved for that task and printed with its packet, so a worker pastes it
/// instead of filling in a template's placeholders and guessing the filter syntax.
/// </summary>
/// <remarks>
/// The task's Test contract row wins when it names a runner command, printed as the plan wrote it. Otherwise the
/// agent's task gates that use <c>{tests}</c> are expanded by <see cref="TaskGateSelection.Plan"/> over the task's
/// files, the same call <c>review gates --agent</c> makes, so the packet and the gate cannot disagree. When neither
/// resolves, nothing is printed: a guessed command is worse than none.
/// </remarks>
public static class PacketTestCommand
{
    /// <summary>The heading the printed section starts with.</summary>
    public const string Heading = "## Test command";

    private const string TestsToken = "{tests}";

    /// <summary>Builds the section for one task, or null when no command resolves.</summary>
    /// <param name="repoRoot">The repository root, where the gate resolves test classes.</param>
    /// <param name="config">The effective configuration, holding the task gates and the test map.</param>
    /// <param name="agent">The agent working the task, or null when unknown.</param>
    /// <param name="entry">The task.</param>
    /// <param name="row">The task's Test contract row, or null when the plan has none for it.</param>
    /// <param name="windows">Whether to quote for a Windows shell rather than a POSIX one.</param>
    public static string? Render(
        string repoRoot,
        KyberWeaveConfig config,
        string? agent,
        PlanTaskFileEntry entry,
        PlanContractRow? row,
        bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(entry);

        if (PlanTaskFileGenerator.CommandFromContract(row) is { } contract)
            return $"{Heading}\n\nThis task's Test contract runner command, ready to paste:\n\n    {contract}";

        IReadOnlyList<string> gated = FromTaskGates(repoRoot, config, agent, entry, windows);
        if (gated.Count == 0)
            return null;

        StringBuilder text = new();
        text.Append(Heading).Append("\n\nThis task's tests, ready to paste (your `tests` task gate runs the same command):\n");
        foreach (string line in gated)
            text.Append("\n    ").Append(line);
        return text.ToString();
    }

    private static IReadOnlyList<string> FromTaskGates(
        string repoRoot, KyberWeaveConfig config, string? agent, PlanTaskFileEntry entry, bool windows)
    {
        if (agent is null || !config.Review.TaskGates.TryGetValue(agent, out IReadOnlyList<ReviewTaskGate>? gates))
            return [];

        ReviewTaskGate[] testGates = [.. gates.Where(gate => gate.Run.Any(element => element.Contains(TestsToken, StringComparison.Ordinal)))];
        string[] files = [.. (entry.Files ?? [])
            .Where(path => path.IndexOfAny(['*', '?', '[']) < 0 && TaskGateSelection.ValidatePath(repoRoot, path) is null)];
        string task = TaskSlug.Normalise(entry.Id);
        if (testGates.Length == 0 || files.Length == 0 || task.Length == 0)
            return [];

        TaskGatePlan plan = TaskGateSelection.Plan(
            testGates, files, task, windows ? "windows" : "unix", repoRoot, config.Review.TestMap);
        if (plan.Refusals.Count > 0)
            return [];

        return [.. plan.Gates.Select(gate =>
            (gate.WorkingDirectory is null ? string.Empty : $"cd {Quote(gate.WorkingDirectory, windows)} && ")
            + string.Join(' ', gate.Run.Select(element => Quote(element, windows))))];
    }

    /// <summary>The argument as a shell reads it back unchanged: bare when it holds only safe characters, else quoted.</summary>
    internal static string Quote(string element, bool windows)
    {
        bool safe = element.Length > 0
            && element[0] != '~'
            && element.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '=' or ':' or ',' or '@' or '%' or '+' or '~');
        if (safe)
            return element;

        return windows
            ? "\"" + element.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : "'" + element.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
