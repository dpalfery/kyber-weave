using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// A RED test task in a compiled test project can only fail by running a test, and the project only compiles when the
/// production code its tests reference exists. When the plan names a production C# file that no task earlier in this
/// task's dependency chain creates, the task cannot produce RED evidence.
/// </summary>
/// <remarks>
/// Only what the parsed plan states is judged: the test files in the task's file scope, the production <c>.cs</c> paths
/// named in the task text or its contract row, and the dependency graph. Symbols are not read from prose; whether the
/// packet states the symbols the tests need is part of the model-graded packet check.
/// </remarks>
internal static partial class PlanCompileUnitOrder
{
    /// <summary>The warning for <paramref name="task"/>, or null when its order is sound or cannot be judged.</summary>
    /// <param name="task">The task.</param>
    /// <param name="parsed">The plan.</param>
    /// <param name="repoRoot">The repository root, to tell a file that already exists from one a task must create; null when unknown.</param>
    /// <param name="artifactPath">The plan, for the finding.</param>
    /// <param name="fixCommand">The command that regenerates the task file.</param>
    public static Diagnostic? Check(PlanTask task, PlanDocument parsed, string? repoRoot, string artifactPath, string fixCommand)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(parsed);

        if (!task.Files.Any(IsTestSource))
        {
            return null;
        }

        string? red = parsed.RedEvidenceRequirement(task.Id);
        bool redTask = task.Id.Contains("RED", StringComparison.OrdinalIgnoreCase)
                       || (red is not null && !red.Equals("Not applicable", StringComparison.OrdinalIgnoreCase));
        if (!redTask)
        {
            return null;
        }

        PlanContractRow? row = parsed.ContractRows.FirstOrDefault(candidate => string.Equals(candidate.TaskId, task.Id, StringComparison.Ordinal));
        string text = string.Join('\n', task.Text, row?.TestProjectOrFile, row?.RunnerFilter, row?.ObservableBehaviour, row?.RedEvidenceRequired, row?.GreenAcceptance);
        HashSet<string> own = [.. task.Files.Select(Normalize)];
        List<string> production = [];
        foreach (Match match in CSharpPathRegex().Matches(text))
        {
            string path = Normalize(match.Value);
            if (!IsTestSource(path) && !own.Contains(path) && !production.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                production.Add(path);
            }
        }

        if (production.Count == 0)
        {
            return null;
        }

        HashSet<string> chain = Chain(task, parsed);
        List<string> uncovered = [];
        foreach (string path in production)
        {
            bool created = parsed.Tasks.Any(other => chain.Contains(other.Id) && other.Files.Any(file => Same(file, path)));
            if (created)
            {
                continue;
            }

            bool laterTask = parsed.Tasks.Any(other => other.Id != task.Id && !chain.Contains(other.Id) && other.Files.Any(file => Same(file, path)));
            bool missingOnDisk = repoRoot is not null && !File.Exists(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (laterTask || missingOnDisk)
            {
                uncovered.Add(path);
            }
        }

        if (uncovered.Count == 0)
        {
            return null;
        }

        return new Diagnostic(
            PlanTaskFileValidator.CompileUnitOrder,
            Severity.Warning,
            $"Task '{task.Id}' writes tests against production code that no earlier task in its chain creates ({string.Join(", ", uncovered)}). " +
            "In a compiled test project it cannot compile, so it cannot produce RED evidence.",
            task.Id,
            artifactPath,
            Hint: $"Add a stub task that declares those types and members, and make '{task.Id}' depend on it; or make '{task.Id}' depend on the task " +
                  $"that creates them; then run {fixCommand} again.");
    }

    private static HashSet<string> Chain(PlanTask task, PlanDocument parsed)
    {
        Dictionary<string, PlanTask> byId = parsed.Tasks.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new(task.DependsOn);
        while (pending.Count > 0)
        {
            string id = pending.Pop();
            if (seen.Add(id) && byId.TryGetValue(id, out PlanTask? dependency))
            {
                foreach (string next in dependency.DependsOn)
                {
                    pending.Push(next);
                }
            }
        }

        return seen;
    }

    private static string Normalize(string path) => path.Trim().Trim('`').Replace('\\', '/').TrimStart('.', '/');

    private static bool Same(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsTestSource(string path)
    {
        string normalized = Normalize(path);
        if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = normalized.Split('/');
        return segments[..^1].Any(segment => segment.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                                             || segment.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                                             || segment.Equals("tests", StringComparison.OrdinalIgnoreCase))
               || segments[^1].EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
               || segments[^1].EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"[\w./\\-]+\.cs\b", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpPathRegex();
}
