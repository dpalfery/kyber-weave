using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.CopilotPrompt;
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
    /// <param name="mode">Read-only, provisioning, or the ensure run that leaves the index and both tools proved.</param>
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

        // HOME is the Unix answer; USERPROFILE is the Windows one. A blank value counts as unset, because an empty
        // path makes GetFullPath throw. The install command resolves home through the same helper, so the probe below
        // looks for the prompt file where install wrote it.
        string? home = CopilotPromptHome.FromEnvironment(environment);
        if (!string.IsNullOrWhiteSpace(home) && SamePath(repositoryRoot, home))
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

        // D1: a repository may set `stop-on-tool-failure: false` to be told about a broken tool and carry on. The exit
        // code is what a caller branches on, so the opt-out has to change the code and not only the wording. It applies
        // to the ensure run — the command a code-editing run starts with — and never to `--check`, whose reader asked
        // for a verdict. A missing binary still exits 3: installing it is the user's decision either way.
        bool stops = mode != DoctorMode.Ensure || StopsOnToolFailure(repositoryRoot);
        if (!stops)
            checks = [.. checks.Select(AsWarning)];

        // The Copilot prompt override is opt-in: with no installed prompt file the probe adds nothing. It runs for any
        // repository the user's home contains, because the override is machine-wide and not a per-repository setting.
        if (!string.IsNullOrWhiteSpace(home))
            checks = [.. checks, .. CopilotPromptProbe.Run(mode, home, environment("APPDATA"), CurrentPlatform(), environment("XDG_CONFIG_HOME"))];

        int exitCode = codegraph.BinaryMissing
            ? 3
            : checks.Any(check => !check.Passed) ? 1 : 0;

        return new DoctorReport(repositoryRoot, mode, exitCode, checks);
    }

    /// <summary>
    /// Whether this repository wants a run to stop when a required tool cannot be repaired.
    /// </summary>
    /// <remarks>
    /// Read from the repository's own <c>.kyber-weave/kyber-weave.yml</c>. A file that cannot be loaded leaves the
    /// default in place — stop — because a broken configuration must not be the thing that weakens the rule.
    /// </remarks>
    internal static bool StopsOnToolFailure(string repositoryRoot)
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(repositoryRoot);
        return result.Config?.StopsOnToolFailure ?? true;
    }

    /// <summary>The operating system this process runs on, in the terms the VS Code layout is keyed by.</summary>
    private static OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Linux;

    /// <summary>Turns a failed check into a passing one whose reason says it is a warning, for a repository that opted out.</summary>
    private static DoctorCheck AsWarning(DoctorCheck check) =>
        check.Passed ? check : new DoctorCheck(check.Id, true, $"warning: {check.Reason}", null);

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
        catch (TimeoutException)
        {
            // The executor bounds every child, so a hung git is an ordinary outcome the doctor reports.
            return (null, "git did not finish while finding the repository root");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return (null, "git could not be run to find the repository root");
        }
    }

    private static DoctorReport Refused(string root, DoctorMode mode, DoctorCheck refusal) =>
        new(root, mode, 2, [refusal]);

    /// <summary>
    ///     Whether two paths name the same directory. A value that cannot be canonicalised is not that directory.
    /// </summary>
    /// <remarks>
    ///     One side is an environment variable, which is arbitrary text, and <c>Path.GetFullPath</c> throws on an
    ///     empty string, a NUL, or a too-long path. A home directory that is not a path is not the repository root.
    ///     Uses the same rule as the Copilot prompt commands, so the two never disagree about a path.
    /// </remarks>
    private static bool SamePath(string left, string right) => CopilotPromptFile.SamePath(left, right);
}
