using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>Runs the task-file generator over the checkout's file system and finds the plans in flight.</summary>
/// <param name="files">Reads the plans and the task files.</param>
/// <param name="paths">Resolves the repository root the caller names.</param>
/// <param name="environment">Names the directory a relative repository root is resolved against.</param>
public sealed partial class PlanTaskService(
    IRepositoryFileStore files,
    IPathResolver paths,
    IProcessEnvironment environment) : IPlanTaskService
{
    private const string TaskList = "tasks.md";

    /// <inheritdoc />
    public PlanTaskFileOutcome Run(string repoRoot, KyberWeaveConfig config, string artifactPath, PlanTaskFileMode mode, IPlanTaskAgentPicker? picker = null) =>
        new PlanTaskFileGenerator(paths.GetFullPath(repoRoot, environment.CurrentDirectory), config, files, picker)
            .Run(artifactPath, mode);

    /// <inheritdoc />
    public IReadOnlyList<string> ActivePlans(string repoRoot, KyberWeaveConfig config) =>
        Plans(repoRoot, config, activeOnly: true);

    /// <inheritdoc />
    public IReadOnlyList<string> Plans(string repoRoot, KyberWeaveConfig config) =>
        Plans(repoRoot, config, activeOnly: false);

    private IReadOnlyList<string> Plans(string repoRoot, KyberWeaveConfig config, bool activeOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        string root = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
        List<string> found = [];
        foreach (string folder in new PlanFileResolver(root, config, files, paths, environment).PlanningFolders())
        {
            string directory = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!files.DirectoryExists(directory))
            {
                continue;
            }

            IEnumerable<string> candidates = files.EnumerateFiles(directory, "*.md", recursive: false)
                .Where(file => !string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
                .Concat(files.EnumerateDirectories(directory)
                    .Select(sub => Path.Combine(sub, TaskList))
                    .Where(files.FileExists));
            foreach (string file in candidates)
            {
                if (!activeOnly || IsActive(file))
                {
                    found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private bool IsActive(string file)
    {
        try
        {
            return ActiveStatusRegex().IsMatch(files.ReadAllTextBounded(file));
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidDataException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\*\*Status:(?:\*\*)?\s*(?:Ready|In[ -]progress)(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveStatusRegex();
}
