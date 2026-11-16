using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>Runs the task-file generator over the checkout's file system and finds the plans in flight.</summary>
/// <param name="files">Reads the plans and the task files.</param>
public sealed partial class PlanTaskService(IRepositoryFileStore files) : IPlanTaskService
{
    private const string TaskList = "tasks.md";

    /// <inheritdoc />
    public PlanTaskFileOutcome Run(string repoRoot, IDocsEngineConfig config, string artifactPath, PlanTaskFileMode mode, IPlanTaskAgentPicker? picker = null) =>
        new PlanTaskFileGenerator(repoRoot, config, files, picker).Run(artifactPath, mode);

    /// <inheritdoc />
    public IReadOnlyList<string> ActivePlans(string repoRoot, IDocsEngineConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        string root = Path.GetFullPath(repoRoot);
        List<string> found = [];
        foreach (string folder in new PlanFileResolver(root, config, files).PlanningFolders())
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
                if (IsActive(file))
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
