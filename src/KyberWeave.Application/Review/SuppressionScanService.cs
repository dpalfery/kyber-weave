using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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
    IProcessRunner programs,
    IFileSystem fileSystem,
    IBoundedFileReader files,
    IPathResolver paths,
    IProcessEnvironment environment) : ISuppressionScanService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] DiffPrefixArguments =
        ["diff", "--no-color", "--no-ext-diff", "-U0", "-M", "--relative", "--diff-filter=ACMRT"];

    private static readonly string[] UntrackedArguments =
        ["ls-files", "-z", "--others", "--exclude-standard", "--"];

    /// <inheritdoc />
    public SuppressionScanResult Scan(string workingDirectory, string baseRef)
    {
        if (!TryPrepareScan(workingDirectory, baseRef, out string root, out SuppressionScanResult? failure))
            return failure;

        GitRunResult diffResult = RunGit(root, SuppressionScan.MaxDiffBytes, [.. DiffPrefixArguments, baseRef, "--"]);
        if (!TryParseDiff(baseRef, diffResult, out SuppressionCandidates? added, out SuppressionScanResult? diffFailure))
            return diffFailure;

        GitRunResult untrackedResult = RunGit(root, null, UntrackedArguments);
        return FinishScan(root, added, untrackedResult);
    }

    /// <inheritdoc />
    public async Task<SuppressionScanResult> ScanAsync(
        string workingDirectory,
        string baseRef,
        CancellationToken cancellationToken = default)
    {
        if (!TryPrepareScan(workingDirectory, baseRef, out string root, out SuppressionScanResult? failure))
            return failure;

        GitRunResult diffResult = await RunGitAsync(
            root,
            SuppressionScan.MaxDiffBytes,
            cancellationToken,
            [.. DiffPrefixArguments, baseRef, "--"]).ConfigureAwait(false);
        if (!TryParseDiff(baseRef, diffResult, out SuppressionCandidates? added, out SuppressionScanResult? diffFailure))
            return diffFailure;

        GitRunResult untrackedResult = await RunGitAsync(
            root,
            null,
            cancellationToken,
            UntrackedArguments).ConfigureAwait(false);
        return FinishScan(root, added, untrackedResult);
    }

    private bool TryPrepareScan(
        string workingDirectory,
        string baseRef,
        out string root,
        [NotNullWhen(false)] out SuppressionScanResult? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        try
        {
            root = paths.GetFullPath(workingDirectory, environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            root = string.Empty;
            failure = new SuppressionScanResult([], $"'{workingDirectory}' is not a usable path: {ex.Message}");
            return false;
        }

        if (!fileSystem.DirectoryExists(root))
        {
            failure = new SuppressionScanResult([], $"'{root}' is not a directory");
            return false;
        }

        if (GitChangeParsing.ValidateBase(baseRef) is { } baseProblem)
        {
            failure = new SuppressionScanResult([], baseProblem);
            return false;
        }

        failure = null;
        return true;
    }

    private static bool TryParseDiff(
        string baseRef,
        GitRunResult diffResult,
        [NotNullWhen(true)] out SuppressionCandidates? added,
        [NotNullWhen(false)] out SuppressionScanResult? failure)
    {
        if (!diffResult.Succeeded)
        {
            added = null;
            failure = diffResult.ExceededBudget
                ? new SuppressionScanResult([], $"the diff against '{baseRef}' exceeded the 10 MB scan budget")
                : new SuppressionScanResult([], $"the change against '{baseRef}' could not be read: {diffResult.Output}");
            return false;
        }

        added = SuppressionScan.FromDiff(diffResult.Output);
        failure = null;
        return true;
    }

    private SuppressionScanResult FinishScan(
        string root,
        SuppressionCandidates added,
        GitRunResult untrackedResult)
    {
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
            ProcessResult result = ((IProgramRunner)programs).Run("git", arguments, workingDirectory, GitTimeout, maxOutputBytes);
            return ToGitRunResult(arguments, result);
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

    private async Task<GitRunResult> RunGitAsync(
        string workingDirectory,
        long? maxOutputBytes,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        try
        {
            ProcessResult result = await programs.RunAsync(
                "git", arguments, workingDirectory, GitTimeout, maxOutputBytes, cancellationToken).ConfigureAwait(false);
            return ToGitRunResult(arguments, result);
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

    private static GitRunResult ToGitRunResult(string[] arguments, ProcessResult result) =>
        result.ExitCode == 0
            ? new GitRunResult(true, result.StandardOutput)
            : new GitRunResult(false, $"git {string.Join(' ', arguments)} exited with code {result.ExitCode}: " +
                                      result.StandardError.Trim());
}
