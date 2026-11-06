using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Refusal rows for the full-suite modes of <c>scripts/fast-loop.sh</c>: <c>--full</c>, <c>--scoped</c> and
/// <c>--regenerate</c> exit 89 when started by a dev agent, before any build, and name the targeted alternative.
/// </summary>
/// <remarks>
/// <para>
/// The refusal applies when the script is started non-interactively (stdin from <c>/dev/null</c>). These tests redirect
/// stdin, so they are non-interactive by construction. <c>--help</c>, <c>--class</c> and <c>--method</c> are never refused
/// with 89: the first prints usage, the other two are the targeted runs the refusal sends agents to.
/// </para>
/// <para>
/// Safety of this suite: the fixture copies <c>fast-loop.sh</c> and <c>proc-guard.sh</c> into a temporary repository and
/// puts stub <c>dotnet</c>, <c>npm</c> and <c>cargo</c> FIRST on PATH. Each stub appends its argv to a marker file.
/// <c>TMPDIR</c> and <c>HOME</c> point into the temporary directory, so no lock or timing file lands in the real home.
/// No row starts a real build or test run. A refused row must leave the marker absent.
/// </para>
/// </remarks>
public sealed class FastLoopFullRunRefusalTests
{
    private const int RefusedExitCode = 89;

    private static readonly TimeSpan LoopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>scripts/fast-loop.sh --full</c>, started non-interactively, exits 89 before any build, prints the
    /// <c>--class</c> alternative, and starts no command.
    /// </summary>
    /// <remarks>
    /// <c>--full</c> runs every declared gate, including the full product suite. A dev agent that runs it competes with the
    /// council for every core, which is the load this refusal exists to stop.
    /// </remarks>
    [Fact]
    public void FullModeStartedNonInteractivelyIsRefusedWithExit89BeforeAnyBuild()
    {
        SkipIfBashUnavailable();
        using FastLoopSandbox sandbox = new();

        ProcessResult result = sandbox.RunFastLoop("--full");

        Assert.Equal(RefusedExitCode, result.ExitCode);
        Assert.Contains("--class", result.StandardError, StringComparison.Ordinal);
        Assert.False(sandbox.Started, $"--full started a command before refusing: {sandbox.MarkerText}");
    }

    /// <summary>
    /// <c>scripts/fast-loop.sh --scoped</c>, started non-interactively, exits 89 before any build, prints the <c>--class</c>
    /// alternative, and starts no command.
    /// </summary>
    /// <remarks>
    /// <c>--scoped</c> runs the full product suite on a .NET change, so it is refused for the same reason as <c>--full</c>.
    /// </remarks>
    [Fact]
    public void ScopedModeStartedNonInteractivelyIsRefusedWithExit89BeforeAnyBuild()
    {
        SkipIfBashUnavailable();
        using FastLoopSandbox sandbox = new();

        ProcessResult result = sandbox.RunFastLoop("--scoped");

        Assert.Equal(RefusedExitCode, result.ExitCode);
        Assert.Contains("--class", result.StandardError, StringComparison.Ordinal);
        Assert.False(sandbox.Started, $"--scoped started a command before refusing: {sandbox.MarkerText}");
    }

    /// <summary>
    /// <c>scripts/fast-loop.sh --regenerate</c>, started non-interactively, exits 89 before the timing run, prints the
    /// <c>--class</c> alternative, and starts no command.
    /// </summary>
    /// <remarks>
    /// <c>--regenerate</c> runs both test assemblies in full to collect per-test timings. That is a full run, so it is refused
    /// unless the caller is the council. A refusal before the timing run also keeps the machine-wide slot free.
    /// </remarks>
    [Fact]
    public void RegenerateStartedNonInteractivelyIsRefusedWithExit89BeforeTheTimingRun()
    {
        SkipIfBashUnavailable();
        using FastLoopSandbox sandbox = new();

        ProcessResult result = sandbox.RunFastLoop("--regenerate");

        Assert.Equal(RefusedExitCode, result.ExitCode);
        Assert.Contains("--class", result.StandardError, StringComparison.Ordinal);
        Assert.False(sandbox.Started, $"--regenerate started a command before refusing: {sandbox.MarkerText}");
    }

    /// <summary>
    /// <c>--help</c> is never refused: it exits 0 and prints the usage text.
    /// </summary>
    /// <remarks>
    /// The refusal message points agents at the script's help. A refused help request would leave them with no way to read
    /// the alternatives.
    /// </remarks>
    [Fact]
    public void HelpIsNeverRefusedAndPrintsUsage()
    {
        SkipIfBashUnavailable();
        using FastLoopSandbox sandbox = new();

        ProcessResult result = sandbox.RunFastLoop("--help");

        Assert.NotEqual(RefusedExitCode, result.ExitCode);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage: scripts/fast-loop.sh", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--class</c> and <c>--method</c> are never refused with 89: each reaches the .NET test runner with its filter.
    /// </summary>
    /// <remarks>
    /// These are the targeted runs the refusal sends agents to. Refusing them would leave a dev agent with no check at all.
    /// The stub records the filter, so the row proves the run reached the runner rather than stopping at a refusal.
    /// </remarks>
    [Theory]
    [InlineData("--class", "KyberWeave.Tests.FastLoopScriptTests", "-class KyberWeave.Tests.FastLoopScriptTests")]
    [InlineData("--method", "KyberWeave.Tests.FastLoopScriptTests.ChangedScript_RunsTheRepositoryPolicyTests", "-method KyberWeave.Tests.FastLoopScriptTests.ChangedScript_RunsTheRepositoryPolicyTests")]
    public void ClassAndMethodFiltersAreNeverRefusedAndReachTheRunner(string flag, string value, string expectedArgument)
    {
        SkipIfBashUnavailable();
        using FastLoopSandbox sandbox = new();

        ProcessResult result = sandbox.RunFastLoop(flag, value);

        Assert.NotEqual(RefusedExitCode, result.ExitCode);
        Assert.True(sandbox.Started, "The targeted run never reached the test runner.");
        Assert.Contains(expectedArgument, sandbox.MarkerText, StringComparison.Ordinal);
    }

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
        {
            throw SkipException.ForSkip("Shell script tests require /bin/bash on a POSIX environment.");
        }
    }

    private static string FixturePath(string fixtureName)
    {
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.RepoPolicy.Tests", "Fixtures", "proc-guard", fixtureName);
    }

    /// <summary>
    /// A temporary repository holding copies of the two scripts, a stub solution file, and stub toolchain executables that
    /// record every invocation in a marker file.
    /// </summary>
    private sealed class FastLoopSandbox : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string _root;
        private readonly string _stubDir;
        private readonly string _markerPath;

        public FastLoopSandbox()
        {
            _root = Path.Combine(_temp.Path, "repo");
            _stubDir = Path.Combine(_temp.Path, "stubs");
            _markerPath = Path.Combine(_temp.Path, "started.txt");
            Directory.CreateDirectory(Path.Combine(_root, "scripts"));
            Directory.CreateDirectory(_stubDir);

            CopyScript("fast-loop.sh");
            CopyScript("proc-guard.sh");
            File.WriteAllText(Path.Combine(_root, "KyberWeave.sln"), "# stub solution\n");

            WriteStub("dotnet");
            WriteStub("npm");
            WriteStub("cargo");
        }

        /// <summary>Whether any stub was invoked.</summary>
        public bool Started => File.Exists(_markerPath);

        /// <summary>The argv lines recorded by the stubs, or empty when none ran.</summary>
        public string MarkerText => File.Exists(_markerPath) ? File.ReadAllText(_markerPath) : string.Empty;

        /// <summary>Runs <c>scripts/fast-loop.sh</c> in the fixture with the given arguments and stdin redirected from empty input.</summary>
        public ProcessResult RunFastLoop(params string[] arguments)
        {
            ProcessStartInfo startInfo = new("/bin/bash")
            {
                WorkingDirectory = _root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("scripts/fast-loop.sh");
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            startInfo.Environment["PATH"] = $"{_stubDir}:{originalPath}";
            startInfo.Environment["STUB_MARKER"] = _markerPath;
            startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath("ps-clean.sh");
            startInfo.Environment["TMPDIR"] = _temp.Path;
            startInfo.Environment["HOME"] = _temp.Path;

            return ProcessRunner.Run(startInfo, string.Empty, LoopTimeout);
        }

        public void Dispose()
        {
            _temp.Dispose();
        }

        private void CopyScript(string name)
        {
            string destination = Path.Combine(_root, "scripts", name);
            File.Copy(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", name), destination, overwrite: true);
            MakeExecutable(destination);
        }

        private static void MakeExecutable(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        /// <summary>Writes a stub that records its argv and reports one passing test, so a run that is not refused exits 0.</summary>
        private void WriteStub(string name)
        {
            string path = Path.Combine(_stubDir, name);
            File.WriteAllText(
                path,
                $"#!/bin/sh\necho \"{name} $*\" >> \"$STUB_MARKER\"\necho \"Total: 1, Passed: 1\"\nexit 0\n");
            MakeExecutable(path);
        }
    }
}
