using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     The repository doctor: the one place that decides whether a repository's documentation tooling works.
/// </summary>
/// <remarks>
///     The <c>doctor</c> CLI verb, <c>docs init --provision</c>, and the squad skill all call this, so the three
///     cannot diverge. Exit codes: 0 all checks pass; 1 a check failed; 2 a client-input refusal (not a repository,
///     or the root is the home directory); 3 the CodeGraph binary is missing and installing it needs the user's
///     approval. Exit 3 is checked before 1, so a missing binary is never reported as an ordinary failure.
/// </remarks>
public static class RepositoryDoctor
{
    /// <summary>Runs the doctor against <paramref name="root"/>.</summary>
    /// <param name="root">The directory to check. The git toplevel of this directory becomes the repository root.</param>
    /// <param name="mode">Read-only or provisioning.</param>
    /// <param name="options">The budgets and executable names.</param>
    /// <param name="executor">
    ///     Runs git and CodeGraph children. Production passes an executor bounded by <see cref="DoctorOptions.IndexTimeout"/>.
    /// </param>
    /// <param name="environment">Reads one environment variable. Injected so the home-directory refusal is testable.</param>
    /// <param name="lockDirectory">Where the index lock file lives. Null uses the OS temp directory.</param>
    public static DoctorReport Run(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessExecutor executor,
        Func<string, string?> environment,
        string? lockDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(environment);

        (string? repositoryRoot, string? notRepository) = ResolveRepositoryRoot(root, executor);
        if (repositoryRoot is null)
            return Refused(root, mode, new DoctorCheck(
                "repository.root",
                false,
                notRepository,
                "run kyber-weave doctor from inside a git repository"));

        string? home = environment("HOME");
        if (home is not null && SamePath(repositoryRoot, home))
            return Refused(repositoryRoot, mode, new DoctorCheck(
                "repository.root",
                false,
                "the repository root is the home directory; refusing to index it",
                "cd into a repository and run kyber-weave doctor"));

        CodeGraphOutcome codegraph = CodeGraphProvisioner.Run(
            repositoryRoot,
            mode,
            options,
            executor,
            lockDirectory ?? Path.GetTempPath());

        List<DoctorCheck> checks = [.. codegraph.Checks, .. McpLiveProbe.Run(repositoryRoot, options)];

        int exitCode = codegraph.BinaryMissing
            ? 3
            : checks.Any(check => !check.Passed) ? 1 : 0;

        return new DoctorReport(repositoryRoot, mode, exitCode, checks);
    }

    /// <summary>
    ///     Asks git for the toplevel of <paramref name="root"/>. A directory outside any repository, or a git that
    ///     cannot run, is a refusal rather than a crash.
    /// </summary>
    private static (string? Root, string? Reason) ResolveRepositoryRoot(string root, IProcessExecutor executor)
    {
        ProcessStartInfo startInfo = new("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--show-toplevel");

        try
        {
            ProcessResult result = executor.Run(startInfo, string.Empty);
            if (result.ExitCode != 0)
                return (null, $"'{root}' is not inside a git repository");

            string top = result.StandardOutput.Trim();
            return top.Length == 0 ? (null, $"'{root}' is not inside a git repository") : (top, null);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return (null, "git could not be run to find the repository root");
        }
    }

    private static DoctorReport Refused(string root, DoctorMode mode, DoctorCheck refusal) =>
        new(root, mode, 2, [refusal]);

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.Ordinal);
}
