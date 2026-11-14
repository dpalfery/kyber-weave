using System.Text;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// How to run one task's tests, printed with its packet: the one <c>review gates --task</c> command for the failing
/// and the passing run, and the test command that run starts, so a worker neither fills in placeholders nor guesses
/// filter syntax.
/// </summary>
/// <remarks>
/// The tests are resolved by <see cref="TaskGateSelection.Plan"/>, the call <c>review gates --task</c> makes, over the
/// task's files from <see cref="PlanTaskTarget"/>, with the test classes the Test contract row names filling
/// <c>{tests}</c> first. A Test contract row that names its own runner command is printed as the plan wrote it. When
/// nothing resolves, nothing is printed: a guessed command is worse than none.
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

        string? contract = PlanTaskFileGenerator.CommandFromContract(row);
        IReadOnlyList<string> gated = FromTaskGates(repoRoot, config, agent, entry, row, windows);
        if (contract is null && gated.Count == 0)
            return null;

        StringBuilder text = new();
        text.Append(Heading).Append('\n');
        if (gated.Count > 0)
        {
            string run = GateCommand(agent!, entry.Id);
            text.Append("\nRun your tests with this one command. It builds, runs this task's tests, prints the failures in short, ")
                .Append("and writes the report that is your evidence:\n\n")
                .Append("    ").Append(run).Append(" --red    # test-first only: once, after writing the tests and before implementing\n")
                .Append("    ").Append(run).Append("          # every run after that, and the final passing run\n")
                .Append("\nThe tests it runs:\n");
            foreach (string line in gated)
                text.Append("\n    ").Append(line);
        }

        if (contract is not null)
        {
            text.Append(gated.Count > 0 ? "\n\n" : "\n").Append("This task's Test contract runner command, ready to paste:\n\n    ").Append(contract);
        }

        return text.ToString();
    }

    /// <summary>The task-gate command for one task, with the agent and task filled in and nothing else to supply.</summary>
    public static string GateCommand(string agent, string? taskId) =>
        $"kyber-weave review gates . --agent {agent} --task {TaskSlug.Normalise(taskId)}";

    private static IReadOnlyList<string> FromTaskGates(
        string repoRoot, KyberWeaveConfig config, string? agent, PlanTaskFileEntry entry, PlanContractRow? row, bool windows)
    {
        if (agent is null || !config.Review.TaskGates.TryGetValue(agent, out IReadOnlyList<ReviewTaskGate>? gates))
            return [];

        ReviewTaskGate[] testGates = [.. gates.Where(gate => gate.Run.Any(element => element.Contains(TestsToken, StringComparison.Ordinal)))];
        string slug = TaskSlug.Normalise(entry.Id);
        IReadOnlyList<string> files = PlanTaskTarget.Files(repoRoot, entry, row);
        if (testGates.Length == 0 || files.Count == 0 || slug.Length == 0)
            return [];

        TaskGatePlan plan = TaskGateSelection.Plan(
            testGates, files, slug, windows ? "windows" : "unix", repoRoot, config.Review.TestMap, PlanTaskTarget.DeclaredClasses(row));
        if (plan.Refusals.Count > 0)
            return [];

        return [.. plan.Gates.Select(gate => Line(gate.WorkingDirectory, gate.Run, windows))];
    }

    private static string Line(string? workingDirectory, IReadOnlyList<string> run, bool windows) =>
        (workingDirectory is null ? string.Empty : $"cd {Quote(workingDirectory, windows)} && ")
        + string.Join(' ', run.Select(element => Quote(element, windows)));

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
