using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// The file rules the plan lifecycle commands share: which path is an active plan, where the plan index is, and how
/// the files a command touched are staged. <c>PlanCloseout</c> and <c>PlanFinalize</c> both refuse any
/// path that is not a plan file directly under the plans folder, so neither can be pointed at code or other documents.
/// </summary>
internal sealed class ActivePlanFiles
{
    private static readonly string[] StageArguments = ["add", "-A", "--"];
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;
    private readonly IRepositoryFileStore _files;
    private readonly IProcessRunner _process;

    internal ActivePlanFiles(string repoRoot, KyberWeaveConfig config, IRepositoryFileStore files, IProcessRunner process)
    {
        _repoRoot = repoRoot;
        _config = config;
        _files = files;
        _process = process;
    }

    /// <summary>The plan index's repository-relative path, or null when the configuration registers none.</summary>
    internal string? PlanIndexPath() =>
        _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(entry => string.Equals(entry.Name, ConfigRegConfig.PlanIndexProperty, StringComparison.Ordinal))
            ?.Path;

    /// <summary>Why <paramref name="plan"/> is not an active plan, or null when it is.</summary>
    internal string? CheckActivePlan(string plan)
    {
        string? index = PlanIndexPath();
        if (index is null)
            return "The configuration registers no plan index, so no plan is active.";

        string folder = Path.GetDirectoryName(index)?.Replace('\\', '/') ?? string.Empty;
        if (!_files.FileExists(Absolute(plan)))
            return $"'{plan}' does not exist.";

        string name = Path.GetFileName(plan);
        bool directlyInFolder = string.Equals(Path.GetDirectoryName(plan)?.Replace('\\', '/') ?? string.Empty, folder,
            StringComparison.OrdinalIgnoreCase);
        if (!directlyInFolder || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(plan, index, StringComparison.OrdinalIgnoreCase))
            return $"'{plan}' is not a plan file directly under '{folder}'.";

        return DocsRootOf(plan).Length == 0 && _config.Ontology.DocsRoots.All(root => root != ".")
            ? $"'{plan}' is under no documentation root."
            : null;
    }

    /// <summary>The documentation root a path sits under; empty for the repository root itself.</summary>
    internal string DocsRootOf(string relative)
    {
        foreach (string root in _config.Ontology.DocsRoots.OrderByDescending(root => root.Length))
        {
            if (root == "." || relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return root == "." ? string.Empty : root;
        }

        return string.Empty;
    }

    internal string Absolute(string relative) =>
        Path.Combine(_repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Whether the root is a git checkout, so there is something to stage and commit.</summary>
    internal bool IsGitCheckout() =>
        _files.DirectoryExists(Path.Combine(_repoRoot, ".git")) || _files.FileExists(Path.Combine(_repoRoot, ".git"));

    /// <summary>The staged paths that are not one of <paramref name="paths"/>, read without blocking a thread; null when git could not say.</summary>
    internal async Task<IReadOnlyList<string>?> StagedOutsideAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await GitAsync(["diff", "--cached", "--name-only", "-z"], cancellationToken)
            .ConfigureAwait(false);
        return ParseStagedOutside(paths, result);
    }

    private static IReadOnlyList<string>? ParseStagedOutside(IEnumerable<string> paths, ProcessResult result)
    {
        if (result.ExitCode != 0)
            return null;

        HashSet<string> own = new(paths.Select(path => path.Replace('\\', '/')), StringComparer.Ordinal);
        return [.. result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => !own.Contains(path))];
    }

    /// <summary>Stages the paths a command touched without blocking a thread, so a commit that only takes tracked files cannot leave a new one behind.</summary>
    internal async Task StageAsync(
        IEnumerable<string> paths,
        DiagnosticReport report,
        string plan,
        string stepCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsGitCheckout())
            return;

        ProcessResult result = await GitAsync([.. StageArguments, .. paths], cancellationToken).ConfigureAwait(false);
        RecordStageResult(result, report, plan, stepCode);
    }

    private static void RecordStageResult(ProcessResult result, DiagnosticReport report, string plan, string stepCode) =>
        report.Add(result.ExitCode == 0
            ? new Diagnostic(stepCode, Severity.Info, "Staged the moved and edited files.", plan, plan)
            : new Diagnostic(stepCode, Severity.Warning, $"git add failed ({result.StandardError.Trim()}); stage the files before committing.", plan, plan));

    /// <summary>
    /// Commits exactly <paramref name="paths"/> on the current branch without blocking a thread, never pushing. Does
    /// nothing when they hold no change; reports a warning and leaves them staged when git refuses.
    /// </summary>
    internal async Task CommitOnlyAsync(
        IReadOnlyList<string> paths,
        string message,
        DiagnosticReport report,
        string plan,
        string stepCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsGitCheckout())
            return;

        ProcessResult pending = await GitAsync(["status", "--porcelain", "--", .. paths], cancellationToken)
            .ConfigureAwait(false);
        if (!HasPendingChanges(pending, report, plan, stepCode))
            return;

        ProcessResult commit = await GitAsync(["commit", "-m", message, "--", .. paths], cancellationToken)
            .ConfigureAwait(false);
        RecordCommitResult(commit, paths, message, report, plan, stepCode);
    }

    private static bool HasPendingChanges(ProcessResult pending, DiagnosticReport report, string plan, string stepCode)
    {
        if (pending.ExitCode != 0)
        {
            report.Add(new Diagnostic(stepCode, Severity.Warning, $"git status failed ({pending.StandardError.Trim()}); nothing was committed.", plan, plan));
            return false;
        }

        return pending.StandardOutput.Trim().Length > 0;
    }

    private static void RecordCommitResult(
        ProcessResult commit,
        IReadOnlyList<string> paths,
        string message,
        DiagnosticReport report,
        string plan,
        string stepCode) =>
        report.Add(commit.ExitCode == 0
            ? new Diagnostic(stepCode, Severity.Info, $"Committed {string.Join(", ", paths)} on the current branch: {message}", plan, plan)
            : new Diagnostic(stepCode, Severity.Warning,
                $"git commit failed ({(commit.StandardError + commit.StandardOutput).Trim()}); the files are staged but not committed.", plan, plan));

    private ProcessStartInfo GitStartInfo(IEnumerable<string> arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private async Task<ProcessResult> GitAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = GitStartInfo(arguments);
        try
        {
            return await _process.RunAsync(startInfo, string.Empty, GitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, $"git could not be run: {ex.Message}");
        }
    }
}
