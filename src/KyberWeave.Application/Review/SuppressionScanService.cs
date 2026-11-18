using System.ComponentModel;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>Requires every warning suppression a change adds to state a reason, judging added lines and never history.</summary>
/// <param name="programs">Starts git.</param>
/// <param name="fileSystem">Probes the repository root.</param>
/// <param name="files">Reads the untracked files whose every line is added.</param>
/// <param name="paths">Resolves the working directory the caller spelled.</param>
/// <param name="environment">Names the working directory a relative one resolves against.</param>
public sealed class SuppressionScanService(
    IProgramRunner programs,
    IFileSystem fileSystem,
    IBoundedFileReader files,
    IPathResolver paths,
    IProcessEnvironment environment) : ISuppressionScanService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public SuppressionScanResult Scan(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string root;
        try
        {
            root = paths.GetFullPath(workingDirectory, environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return new SuppressionScanResult([], $"'{workingDirectory}' is not a usable path: {ex.Message}");
        }

        if (!fileSystem.DirectoryExists(root))
            return new SuppressionScanResult([], $"'{root}' is not a directory");

        if (GitChangeParsing.ValidateBase(baseRef) is { } baseProblem)
            return new SuppressionScanResult([], baseProblem);

        GitRunResult diffResult = RunGit(
            root,
            SuppressionScan.MaxDiffBytes,
            "diff", "--no-color", "--no-ext-diff", "-U0", "-M", "--relative", "--diff-filter=ACMRT", baseRef, "--");
        if (!diffResult.Succeeded)
        {
            if (diffResult.ExceededBudget)
                return new SuppressionScanResult([], $"the diff against '{baseRef}' exceeded the 10 MB scan budget");

            return new SuppressionScanResult([], $"the change against '{baseRef}' could not be read: {diffResult.Output}");
        }

        SuppressionCandidates added = SuppressionScan.FromDiff(diffResult.Output);

        GitRunResult untrackedResult = RunGit(
            root, null, "ls-files", "-z", "--others", "--exclude-standard", "--");
        if (!untrackedResult.Succeeded)
            return new SuppressionScanResult([], $"the untracked files could not be listed: {untrackedResult.Output}");

        string[] untrackedPaths = untrackedResult.Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (string path in untrackedPaths)
        {
            if (added.Has(path))
                continue;

            if (files.ReadBytes(Path.Combine(root, path), SuppressionScan.MaxDiffBytes) is { } content)
                _ = added.AddUntracked(path, content);
        }

        return new SuppressionScanResult(added.Findings(), null);
    }

    private readonly record struct GitRunResult(bool Succeeded, string Output, bool ExceededBudget = false);

    /// <summary>
    /// Runs git with argv, never a shell, and returns its output or the reason it produced none. A missing git, a
    /// timeout, and a non-zero exit are all the same fact to the caller: the change was not read.
    /// </summary>
    private GitRunResult RunGit(string workingDirectory, long? maxOutputBytes, params string[] arguments)
    {
        try
        {
            ProcessResult result = programs.Run("git", arguments, workingDirectory, GitTimeout, maxOutputBytes);
            return result.ExitCode == 0
                ? new GitRunResult(true, result.StandardOutput)
                : new GitRunResult(false, $"git {string.Join(' ', arguments)} exited with code {result.ExitCode}: " +
                                          result.StandardError.Trim());
        }
        catch (OutputLimitExceededException ex)
        {
            return new GitRunResult(false, ex.Message.Trim(), ExceededBudget: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException or IOException)
        {
            return new GitRunResult(false, ex.Message.Trim());
        }
    }
}
