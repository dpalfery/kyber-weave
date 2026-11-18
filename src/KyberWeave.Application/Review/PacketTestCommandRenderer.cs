using System.Text;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>
/// How to run one task's tests, printed with its packet: the one <c>review gates --task</c> command for the failing
/// and the passing run, and the test command that run starts, so a worker neither fills in placeholders nor guesses
/// filter syntax.
/// </summary>
/// <remarks>
/// The tests are resolved by <see cref="ITaskGateSelectionService.Plan"/>, the call <c>review gates --task</c> makes,
/// over the task's files from <see cref="IPlanTaskTargetService"/>, with the test classes the Test contract row names
/// filling <c>{tests}</c> first. A Test contract row that names its own runner command is printed as the plan wrote it.
/// When nothing resolves, nothing is printed: a guessed command is worse than none.
/// </remarks>
public static class PacketTestCommandRenderer
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
    /// <param name="targets">Reads the task's files and declared test classes.</param>
    /// <param name="taskGates">Resolves the test gates against the task's files.</param>
    public static string? Render(
        string repoRoot,
        KyberWeaveConfig config,
        string? agent,
        PlanTaskFileEntry entry,
        PlanContractRow? row,
        bool windows,
        IPlanTaskTargetService targets,
        ITaskGateSelectionService taskGates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(taskGates);

        string? contract = PlanTaskFileGenerator.CommandFromContract(row);
        IReadOnlyList<string> gated = FromTaskGates(repoRoot, config, agent, entry, row, windows, targets, taskGates);
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
                .Append("\nNever run the tests by hand as well: the gate run is the test run, and a second run of the same tests only costs time.\n")
                .Append("\nThe tests it runs:\n");
            foreach (string line in gated)
                text.Append("\n    ").Append(line);
        }

        if (gated.Count == 0 && contract is not null)
        {
            text.Append("\nThis task's Test contract runner command, ready to paste:\n\n    ").Append(contract);
        }

        return text.ToString();
    }

    /// <summary>The task-gate command for one task, with the agent and task filled in and nothing else to supply.</summary>
    public static string GateCommand(string agent, string? taskId) =>
        $"kyber-weave review gates . --agent {agent} --task {TaskSlug.Normalise(taskId)}";

    /// <summary>The argument as a shell reads it back unchanged: bare when it holds only safe characters, else quoted.</summary>
    public static string Quote(string element, bool windows)
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

    private static IReadOnlyList<string> FromTaskGates(
        string repoRoot,
        KyberWeaveConfig config,
        string? agent,
        PlanTaskFileEntry entry,
        PlanContractRow? row,
        bool windows,
        IPlanTaskTargetService targets,
        ITaskGateSelectionService taskGates)
    {
        if (agent is null)
            return [];

        TaskGateResolution resolved = TaskGateResolver.Resolve(
            config.Review,
            agent,
            entry.Technology,
            TaskGateResolver.ParentTechnologyOrNone(repoRoot, config, entry.Technology));

        ReviewTaskGate[] testGates = [.. resolved.Gates.Where(gate => gate.Run.Any(element => element.Contains(TestsToken, StringComparison.Ordinal)))];
        string slug = TaskSlug.Normalise(entry.Id);
        IReadOnlyList<string> files = targets.Files(repoRoot, entry, row);
        if (testGates.Length == 0 || files.Count == 0 || slug.Length == 0)
            return [];

        TaskGatePlan plan = taskGates.Plan(
            testGates, files, slug, windows ? "windows" : "unix", repoRoot, config.Review.TestMap, targets.DeclaredClasses(row));
        if (plan.Refusals.Count > 0)
            return [];

        return [.. plan.Gates.Select(gate => Line(gate.WorkingDirectory, gate.Run, windows))];
    }

    private static string Line(string? workingDirectory, IReadOnlyList<string> run, bool windows) =>
        (workingDirectory is null ? string.Empty : $"cd {Quote(workingDirectory, windows)} && ")
        + string.Join(' ', run.Select(element => Quote(element, windows)));
}
