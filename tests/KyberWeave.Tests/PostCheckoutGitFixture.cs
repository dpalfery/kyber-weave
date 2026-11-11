using System.Globalization;
using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests;

/// <summary>
/// A throwaway git setup for the post-checkout hook tests: a repository under a temporary root, a bin folder for
/// the stub <c>kyber-weave</c>, and a marker folder the stub writes to.
/// </summary>
/// <remarks>
/// <para>
/// The hook is one file shared by every worktree of a clone, so a test that let git reach the real clone would write
/// into this checkout's <c>.git/hooks</c>. Every git child therefore runs with HOME and GIT_CONFIG_GLOBAL under the
/// temporary root and GIT_CONFIG_NOSYSTEM set, and the commit identity is pinned here rather than read from the
/// developer's configuration.
/// </para>
/// <para>
/// The script tests start background children on purpose. The stub appends its PID to the marker folder, and
/// <see cref="Dispose"/> kills any shell or sleep left running, so proc-guard never sees a leftover child.
/// </para>
/// </remarks>
internal sealed class PostCheckoutGitFixture : IDisposable
{
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _temp = new();
    private readonly ProcessExecutor _runner = ProcessExecutor.WithTimeout(ChildTimeout);

    public PostCheckoutGitFixture()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Stubs);
        Directory.CreateDirectory(Markers);
        Directory.CreateDirectory(EmptyBin);
    }

    /// <summary>The temporary root every path in this fixture lives under.</summary>
    public string Root => _temp.Path;

    /// <summary>The repository that a governed test creates.</summary>
    public string Repository => Path.Combine(Root, "repo");

    /// <summary>The HOME that every git child sees, so no developer configuration leaks in.</summary>
    public string Home => Path.Combine(Root, "home");

    /// <summary>The folder that holds the stub <c>kyber-weave</c> when a test puts one on PATH.</summary>
    public string Stubs => Path.Combine(Root, "stubs");

    /// <summary>The folder the stub writes its invocations and PIDs to.</summary>
    public string Markers => Path.Combine(Root, "markers");

    /// <summary>An empty folder, for a PATH that must not find <c>kyber-weave</c>.</summary>
    public string EmptyBin => Path.Combine(Root, "empty-bin");

    /// <summary>The hook path git resolves for a repository whose hooks are not redirected.</summary>
    public static string HookPath(string root) => Path.Combine(root, ".git", "hooks", "post-checkout");

    /// <summary>
    /// The executor the code under test receives. Its git children run isolated, with PATH left as the test's own
    /// unless <paramref name="path"/> names one.
    /// </summary>
    public IProcessExecutor Executor(string? path = null) => new IsolatedChildExecutor(_runner, Home, path);

    /// <summary>A PATH that finds <paramref name="first"/> before the system folders and nothing else.</summary>
    public static string PathStartingWith(string first) => first + ":/usr/bin:/bin";

    /// <summary>Runs git in <paramref name="workingDirectory"/> with the isolated environment and returns the result.</summary>
    public ProcessResult Git(string workingDirectory, params string[] args) => Run(workingDirectory, null, args);

    /// <summary>Runs git with a chosen PATH, so a hook can find the stub or fail to find it.</summary>
    public ProcessResult GitOn(string workingDirectory, string path, params string[] args) => Run(workingDirectory, path, args);

    /// <summary>Runs git and fails the test with git's own stderr when the exit code is not zero.</summary>
    public void GitOk(string workingDirectory, params string[] args) => ExpectSuccess(Git(workingDirectory, args), args);

    /// <summary>Runs git with a chosen PATH and fails the test when the exit code is not zero.</summary>
    public void GitOnOk(string workingDirectory, string path, params string[] args) =>
        ExpectSuccess(GitOn(workingDirectory, path, args), args);

    /// <summary>Runs git and returns its standard output, failing when the exit code is not zero.</summary>
    public string GitOutput(string workingDirectory, params string[] args)
    {
        ProcessResult result = Git(workingDirectory, args);
        ExpectSuccess(result, args);
        return result.StandardOutput;
    }

    /// <summary>Creates an empty repository with one commit-less <c>git init</c>.</summary>
    public void CreateRepository()
    {
        Directory.CreateDirectory(Repository);
        GitOk(Repository, "init", "--quiet");
    }

    /// <summary>Creates a repository with governed docs: a config file and a docs folder, not yet committed.</summary>
    public void CreateGovernedRepository()
    {
        CreateRepository();
        WriteGoverned(Repository, "stop-on-tool-failure: true\n");
    }

    /// <summary>Writes the governed config and docs folder that make a repository governed.</summary>
    public static void WriteGoverned(string root, string config)
    {
        Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
        File.WriteAllText(Path.Combine(root, ".kyber-weave", "kyber-weave.yml"), config);
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "README.md"), "# Docs\n");
    }

    /// <summary>Stages everything under <paramref name="workingDirectory"/> and commits it.</summary>
    public void CommitAll(string workingDirectory, string message)
    {
        GitOk(workingDirectory, "add", "--all");
        GitOk(workingDirectory, "commit", "--quiet", "-m", message);
    }

    /// <summary>
    /// Kills any stub shell or sleep the tests started and left running. The stub records its PID on every
    /// invocation; a PID is killed only when its process is still a shell or a sleep, so a recycled PID is never hit.
    /// </summary>
    public void ReapBackgroundChildren()
    {
        string pids = Path.Combine(Markers, "pids");
        if (!File.Exists(pids))
        {
            return;
        }

        foreach (string line in File.ReadAllLines(pids))
        {
            if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }

            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.ProcessName is "sh" or "dash" or "bash" or "sleep")
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException)
            {
                // The stub already exited and was reaped, which is the outcome the test wanted.
            }
            catch (InvalidOperationException)
            {
                // The process ended between the lookup and the kill.
            }
        }
    }

    public void Dispose()
    {
        ReapBackgroundChildren();
        _temp.Dispose();
    }

    private ProcessResult Run(string workingDirectory, string? path, string[] args)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Executor(path).Run(startInfo, string.Empty);
    }

    private static void ExpectSuccess(ProcessResult result, string[] args)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    /// <summary>
    /// Pins the child environment: HOME and the global config point into the temporary root, the system config is
    /// off, and the commit identity is fixed. The wrapped runner still applies its own timeout and tree kill.
    /// </summary>
    private sealed class IsolatedChildExecutor(IProcessExecutor inner, string home, string? path) : IProcessExecutor
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            ArgumentNullException.ThrowIfNull(startInfo);

            startInfo.Environment["HOME"] = home;
            startInfo.Environment["USERPROFILE"] = home;
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig");
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_AUTHOR_NAME"] = "Kyber-Weave Tests";
            startInfo.Environment["GIT_AUTHOR_EMAIL"] = "kyber-weave@example.com";
            startInfo.Environment["GIT_COMMITTER_NAME"] = "Kyber-Weave Tests";
            startInfo.Environment["GIT_COMMITTER_EMAIL"] = "kyber-weave@example.com";
            if (path is not null)
            {
                startInfo.Environment["PATH"] = path;
            }

            return inner.Run(startInfo, standardInput);
        }
    }
}
