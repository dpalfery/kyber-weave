using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Review;

/// <summary>Lists the files a working tree changed, by running git with argv and never a shell.</summary>
public static class ChangedFiles
{
    /// <summary>The ref the working tree is compared with when none is named.</summary>
    public const string DefaultBase = "HEAD";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Refuses a <c>--base</c> value git would read as an option, or that is empty.</summary>
    /// <returns>The reason it is refused, or <see langword="null"/> when the value is acceptable.</returns>
    public static string? ValidateBase(string baseRef)
    {
        if (string.IsNullOrWhiteSpace(baseRef))
            return "--base is empty. Name a git ref such as HEAD or origin/main.";

        if (baseRef.StartsWith('-'))
            return $"--base '{baseRef}' starts with '-' and would be read as a git option. " +
                   "Name a git ref such as HEAD or origin/main.";

        return null;
    }

    /// <summary>
    /// Lists the paths changed since <paramref name="baseRef"/> that still exist, together with untracked files.
    /// </summary>
    /// <remarks>
    /// Deleted paths are left out on purpose: a gate cannot read a file that is gone. The output is
    /// NUL-separated so a name git would otherwise quote comes through exactly as written.
    /// </remarks>
    /// <param name="workingDirectory">The repository root. Paths come back relative to it.</param>
    /// <param name="baseRef">The ref to diff the working tree against.</param>
    /// <exception cref="InvalidOperationException">git could not list the files.</exception>
    public static IReadOnlyList<string> Resolve(string workingDirectory, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string[] changed = RunGitPaths(
            workingDirectory,
            "diff", "-z", "--name-only", "--relative", "--diff-filter=ACMRT", baseRef, "--");
        string[] untracked = RunGitPaths(
            workingDirectory,
            "ls-files", "-z", "--others", "--exclude-standard", "--");

        return changed
            .Concat(untracked)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] RunGitPaths(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
