using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for the full-run guard in <c>scripts/proc-guard.sh</c>: <c>classify</c> names a command as a
/// <c>full</c> or <c>partial</c> test run, and <c>run</c> refuses a full run unless the caller passes <c>--council</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only the review council and CI run the full .NET, dash and Rust suites. A dev agent that starts one competes with
/// every other worker for every core, so the guard refuses it with exit 89 and names the targeted alternative
/// (<c>scripts/fast-loop.sh --class</c>). The refusal happens before the slot wait and before any process starts.
/// </para>
/// <para>
/// Interface the implementation must provide (chosen by the test author; the tests are the contract):
/// <c>scripts/proc-guard.sh classify -- &lt;argv&gt;</c> prints exactly <c>full</c> or <c>partial</c> on stdout, exits 0,
/// and starts nothing. <c>scripts/proc-guard.sh run [--council] [existing flags] -- &lt;argv&gt;</c> consumes
/// <c>--council</c> itself and does not forward it to the command.
/// </para>
/// <para>
/// Safety of this suite: every case puts stub <c>dotnet</c>, <c>npm</c> and <c>cargo</c> executables FIRST on the
/// child's PATH. Each stub appends its argv to a marker file and exits with a chosen code. A missing refusal therefore
/// shows up as a wrong exit code or a marker file, never as a real build or test run. The process table is read from
/// <c>Fixtures/proc-guard/ps-clean.sh</c>, so nothing on the machine can trip a cap. Every case asserts on the marker
/// file to prove whether anything started.
/// </para>
/// <para>
/// Accepted residual, by decision: a shell string handed to <c>sh -c</c> or <c>bash -c</c> is not unwrapped. Typing
/// <c>--council</c> is the same class of trust, so no row pins those shapes either way.
/// </para>
/// </remarks>
public sealed class ProcGuardFullRunGuardTests
{
    private const int RefusedExitCode = 89;
    private const string CouncilFlag = "--council";
    private const string FullArgv = "dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build";
    private const string PartialArgv = "dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build";

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Each row is a test-run shape that must classify as <c>full</c>: an unfiltered product suite under <c>dotnet</c>,
    /// <c>npm</c> or <c>cargo</c>. A flag such as <c>--coverage</c> or <c>--manifest-path</c> does not filter the run.
    /// </summary>
    /// <remarks>
    /// These are the shapes the review council and CI run. If the guard classified one of them as partial, a dev agent
    /// could start the whole suite with no refusal at all.
    /// </remarks>
    [Theory]
    [InlineData("dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build")]
    [InlineData("dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll")]
    [InlineData("npm --prefix dash run test")]
    [InlineData("npm run --prefix dash test")]
    [InlineData("npm --prefix dash run test -- --coverage")]
    [InlineData("cargo test")]
    [InlineData("cargo test --manifest-path dash/tray/src-tauri/Cargo.toml")]
    // Command prefixes are unwrapped before classifying (decision D17): the prefix runs the full suite it wraps.
    [InlineData("timeout 1800 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release")]
    [InlineData("timeout -s KILL 1800 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("timeout --signal=KILL -k 5 1800 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("env CI=1 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release")]
    [InlineData("env FOO=1 BAR=2 dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll")]
    [InlineData("nice -n 10 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("time dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("command dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("exec dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    // dotnet test is full unless narrowed or aimed at another project: absent, a solution, a directory, or the product project.
    [InlineData("dotnet test")]
    [InlineData("dotnet test -c Release")]
    [InlineData("dotnet test --no-build -c Release")]
    [InlineData("dotnet test KyberWeave.sln")]
    [InlineData("dotnet test Other.slnx")]
    [InlineData("dotnet test tests/KyberWeave.Tests")]
    [InlineData("dotnet test .")]
    [InlineData("dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("dotnet test --project tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("dotnet test --solution KyberWeave.sln")]
    [InlineData("dotnet test --directory tests/KyberWeave.Tests")]
    // npm spellings of the dash test script with no path after --.
    [InlineData("npm --prefix=dash run test")]
    [InlineData("npm --prefix ./dash run test")]
    [InlineData("npm --prefix dash/ run test")]
    [InlineData("npm run-script --prefix dash test")]
    [InlineData("npm test --prefix dash")]
    [InlineData("npm t --prefix dash")]
    [InlineData("npm test --prefix=dash -- --coverage")]
    // A toolchain word such as +stable is skipped; with no positional filter the run is the full tray suite.
    [InlineData("cargo +stable test --manifest-path dash/tray/src-tauri/Cargo.toml")]
    // dotnet run is full when its project is the product project and no filter appears anywhere.
    [InlineData("dotnet run --project tests/KyberWeave.Tests")]
    [InlineData("dotnet run --project tests/KyberWeave.Tests/")]
    [InlineData("dotnet run -c Release --project tests/KyberWeave.Tests/KyberWeave.Tests.csproj --no-build")]
    [InlineData("dotnet run --project=tests/KyberWeave.Tests -- -reporter quiet")]
    // Launchers (nohup, caffeinate, sudo, doas, ionice, setsid, stdbuf, arch) are stripped with their own options.
    [InlineData("nohup dotnet test")]
    [InlineData("nohup dotnet test KyberWeave.sln")]
    [InlineData("caffeinate -i dotnet test")]
    [InlineData("caffeinate -t 3600 -i dotnet test")]
    [InlineData("caffeinate -w 4242 -i dotnet test")]
    [InlineData("sudo dotnet test")]
    [InlineData("sudo -u dave dotnet test")]
    [InlineData("sudo -g staff dotnet test")]
    [InlineData("doas -u dave dotnet test")]
    [InlineData("sudo -E env FOO=1 dotnet test")]
    [InlineData("ionice -c 3 nice -n 10 dotnet test")]
    [InlineData("setsid dotnet test")]
    [InlineData("stdbuf -oL dotnet test")]
    [InlineData("stdbuf -o L dotnet test")]
    [InlineData("nohup timeout 900 dotnet test")]
    [InlineData("/usr/bin/nohup /usr/local/share/dotnet/dotnet test KyberWeave.sln")]
    [InlineData("/opt/homebrew/bin/caffeinate -dimsu npm --prefix dash run test")]
    [InlineData("caffeinate -i cargo test")]
    // A test script with no positional after -- is full whatever the prefix: none, relative, absolute, tray UI, or another dir.
    [InlineData("npm --prefix /Users/dave/git/personal/claude/kyber-weave/dash run test")]
    [InlineData("npm --prefix=/abs/path/dash run test")]
    [InlineData("npm --prefix /abs/path/dash/ test")]
    [InlineData("npm --prefix dash/tray/ui run test")]
    [InlineData("npm --prefix /abs/path/dash/tray/ui test")]
    [InlineData("npm --prefix other run test")]
    [InlineData("npm run test")]
    [InlineData("npm test")]
    [InlineData("npm test --prefix /abs/path/dash")]
    public void ClassifyReportsAnUnfilteredProductSuiteAsFull(string command)
    {
        AssertClassifiesAs(command, "full");
    }

    /// <summary>
    /// Each row is a run that must classify as <c>partial</c>: a filtered product run, the repository-policy project even
    /// when unfiltered, a path-filtered npm run, a build, or any other command.
    /// </summary>
    /// <remarks>
    /// The policy project is partial because its suite is small (about 30 seconds) and it is the one a dev agent needs to
    /// run after editing a script or a gate. A filtered run is partial because it names what it runs. Misclassifying either
    /// as full would refuse the targeted check the refusal message tells the agent to use.
    /// </remarks>
    [Theory]
    [InlineData("dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build")]
    [InlineData("dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll")]
    [InlineData("dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -class KyberWeave.Tests.FastLoopScriptTests")]
    [InlineData("dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -method KyberWeave.Tests.FastLoopScriptTests.ChangedScript_RunsTheRepositoryPolicyTests")]
    [InlineData("dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter FullyQualifiedName~FastLoop")]
    [InlineData("dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter-class KyberWeave.Tests.FastLoopScriptTests")]
    [InlineData("dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -trait Category=Scale")]
    [InlineData("dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -treenode-filter /*/*/FastLoop*/*")]
    [InlineData("npm --prefix dash run test:related -- src/foo.ts")]
    [InlineData("npm --prefix dash run test -- src/foo.test.ts")]
    [InlineData("cargo test --manifest-path dash/tray/src-tauri/Cargo.toml tray_menu")]
    [InlineData("dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release")]
    // Control rows for the bypass shapes: a narrowed, policy-project or other-project run stays partial, so the fix cannot over-refuse.
    [InlineData("timeout 60 dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj")]
    [InlineData("env X=1 dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll -class KyberWeave.Tests.Foo")]
    [InlineData("dotnet test KyberWeave.sln --filter FullyQualifiedName~X")]
    [InlineData("dotnet test --filter FullyQualifiedName~X")]
    [InlineData("dotnet test --project tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj")]
    [InlineData("npm test --prefix dash -- tests/foo.test.ts")]
    [InlineData("cargo +stable test --manifest-path dash/tray/src-tauri/Cargo.toml my_filter")]
    // dotnet run is partial unless it targets the product project with no filter; no project is partial.
    [InlineData("dotnet run --project tests/KyberWeave.Tests -- -class \"KyberWeave.Tests.ConfigRegResolutionTests\"")]
    [InlineData("dotnet run --project tests/KyberWeave.Tests -- --filter-class \"KyberWeave.Tests.X\"")]
    [InlineData("dotnet run --project src/KyberWeave.Cli -- review gates .")]
    [InlineData("dotnet run --project tests/KyberWeave.RepoPolicy.Tests")]
    [InlineData("dotnet run")]
    // Launchers with nothing after them, or with a non-test tool after them, are partial.
    [InlineData("nohup dotnet build")]
    [InlineData("sudo dotnet test --filter \"FullyQualifiedName~Foo\"")]
    [InlineData("caffeinate -i npm --prefix dash run lint")]
    [InlineData("nohup echo hello")]
    [InlineData("sudo")]
    // npm test scripts with a suffix, a path after --, or a non-test script are partial.
    [InlineData("npm --prefix /abs/path/dash run test:related -- src/a.test.ts")]
    [InlineData("npm --prefix /abs/path/dash run test -- src/foo.test.ts")]
    [InlineData("npm --prefix dash/tray/ui run test -- src/foo.test.tsx")]
    [InlineData("npm --prefix dash run lint")]
    [InlineData("npm --prefix dash run test:locks")]
    [InlineData("echo hi")]
    public void ClassifyReportsAFilteredPolicyOrOtherCommandAsPartial(string command)
    {
        AssertClassifiesAs(command, "partial");
    }

    /// <summary>
    /// A full suite run with no <c>--council</c> and non-interactive input exits 89 before the command starts, and the
    /// refusal names <c>--class</c> so the agent knows the targeted alternative.
    /// </summary>
    /// <remarks>
    /// This is the refusal itself. Before it existed, a dev agent running the full suite competed with the council for
    /// every core. The exit code is distinct so a caller can tell a refusal from a failing test run.
    /// </remarks>
    [Fact]
    public void RunFullSuiteWithoutCouncilIsRefusedWithExit89AndNeverStartsTheCommand()
    {
        SkipIfBashUnavailable();
        using GuardSandbox sandbox = new();
        sandbox.Stub("dotnet", exitCode: 0);

        ProcessResult result = sandbox.Guard(["run", "--", .. FullArgv.Split(' ')]);

        Assert.Equal(RefusedExitCode, result.ExitCode);
        Assert.Contains("--class", result.StandardError, StringComparison.Ordinal);
        Assert.False(sandbox.Started, $"The refused full run still started a command: {sandbox.MarkerText}");
    }

    /// <summary>
    /// A full suite run with <c>--council</c> starts the command, returns the command's own exit code, and does not pass
    /// <c>--council</c> on to it.
    /// </summary>
    /// <remarks>
    /// The council and CI are the callers allowed to run the full suite. Forwarding <c>--council</c> would hand the
    /// wrapped test runner an argument it does not know.
    /// </remarks>
    [Fact]
    public void RunFullSuiteWithCouncilStartsTheCommandAndPassesItsExitCodeThrough()
    {
        SkipIfBashUnavailable();
        using GuardSandbox sandbox = new();
        sandbox.Stub("dotnet", exitCode: 7);

        ProcessResult result = sandbox.Guard(["run", CouncilFlag, "--", .. FullArgv.Split(' ')]);

        Assert.Equal(7, result.ExitCode);
        Assert.True(sandbox.Started, "The council run did not start the command.");
        Assert.Contains("dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj", sandbox.MarkerText, StringComparison.Ordinal);
        Assert.DoesNotContain(CouncilFlag, sandbox.MarkerText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partial run (the repository-policy project) runs without <c>--council</c>, exactly as it does today.
    /// </summary>
    /// <remarks>
    /// The guard refuses only the full suite. Refusing the policy run too would stop a dev agent from checking a script
    /// or gate edit at all.
    /// </remarks>
    [Fact]
    public void RunPartialSuiteWithoutCouncilStillRunsTheCommand()
    {
        SkipIfBashUnavailable();
        using GuardSandbox sandbox = new();
        sandbox.Stub("dotnet", exitCode: 0);

        ProcessResult result = sandbox.Guard(["run", "--", .. PartialArgv.Split(' ')]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(sandbox.Started, "The partial run did not start the command.");
    }

    /// <summary>
    /// A full run in a bypass shape (a command prefix, a solution or directory target, or a toolchain word) is refused
    /// with exit 89 and starts nothing, exactly as the bare full run is.
    /// </summary>
    /// <remarks>
    /// Each row was classified as partial before the amended definition, so the guard admitted it without <c>--council</c>.
    /// A prefix such as <c>env</c> or <c>timeout</c> runs the command it wraps, so a refusal that looked only at the first
    /// word would let the wrapped full run start. The marker file is the proof: it must not exist after the refusal.
    /// </remarks>
    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet test KyberWeave.sln")]
    [InlineData("timeout 60 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj")]
    [InlineData("env CI=1 dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release")]
    [InlineData("npm --prefix=dash run test")]
    [InlineData("dotnet run --project tests/KyberWeave.Tests")]
    [InlineData("npm test")]
    [InlineData("npm --prefix other run test")]
    public void RunBypassShapeOfAFullRunWithoutCouncilIsRefusedWithExit89AndNeverStartsTheCommand(string command)
    {
        SkipIfBashUnavailable();
        using GuardSandbox sandbox = new();
        sandbox.Stub("dotnet", exitCode: 0);
        sandbox.Stub("npm", exitCode: 0);

        ProcessResult result = sandbox.Guard(["run", "--", .. command.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);

        Assert.Equal(RefusedExitCode, result.ExitCode);
        Assert.Contains("--class", result.StandardError, StringComparison.Ordinal);
        Assert.False(sandbox.Started, $"The refused bypass-shape run still started a command: {sandbox.MarkerText}");
    }

    /// <summary>
    /// A bypass-shape full run with <c>--council</c> still starts the wrapped command and returns its exit code.
    /// </summary>
    /// <remarks>
    /// Control for the refusal rows above: the council flag admits the run whatever the prefix, and the prefix is not
    /// stripped from the command that runs.
    /// </remarks>
    [Fact]
    public void RunBypassShapeOfAFullRunWithCouncilStillStartsTheCommand()
    {
        SkipIfBashUnavailable();
        using GuardSandbox sandbox = new();
        sandbox.Stub("dotnet", exitCode: 7);

        ProcessResult result = sandbox.Guard(
            ["run", CouncilFlag, "--", "env", "CI=1", "dotnet", "test", "tests/KyberWeave.Tests/KyberWeave.Tests.csproj", "-c", "Release"]);

        Assert.Equal(7, result.ExitCode);
        Assert.True(sandbox.Started, "The council run of a bypass shape did not start the command.");
        Assert.DoesNotContain(CouncilFlag, sandbox.MarkerText, StringComparison.Ordinal);
    }

    private static void AssertClassifiesAs(string command, string expected)
    {
        SkipIfBashUnavailable();
        using GuardSandbox sandbox = new();
        sandbox.Stub("dotnet", exitCode: 0);
        sandbox.Stub("npm", exitCode: 0);
        sandbox.Stub("cargo", exitCode: 0);

        ProcessResult result = sandbox.Guard(["classify", "--", .. command.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StandardOutput.TrimEnd('\r', '\n'));
        Assert.False(sandbox.Started, $"classify started a command: {sandbox.MarkerText}");
    }

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
        {
            throw SkipException.ForSkip("Process-guard script tests require /bin/bash on a POSIX environment.");
        }
    }

    private static string FixturePath(string fixtureName)
    {
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.RepoPolicy.Tests", "Fixtures", "proc-guard", fixtureName);
    }

    /// <summary>
    /// A temporary directory holding stub toolchain executables and a marker file that records every stub invocation.
    /// </summary>
    /// <remarks>
    /// The marker is the only evidence a stub ran. Its absence after a refusal is the proof that nothing started.
    /// </remarks>
    private sealed class GuardSandbox : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string _stubDir;
        private readonly string _markerPath;

        public GuardSandbox()
        {
            _stubDir = Path.Combine(_temp.Path, "stubs");
            _markerPath = Path.Combine(_temp.Path, "started.txt");
            Directory.CreateDirectory(_stubDir);
        }

        /// <summary>Whether any stub was invoked.</summary>
        public bool Started => File.Exists(_markerPath);

        /// <summary>The argv lines recorded by the stubs, or empty when none ran.</summary>
        public string MarkerText => File.Exists(_markerPath) ? File.ReadAllText(_markerPath) : string.Empty;

        /// <summary>Writes a stub executable that records its argv in the marker and exits with <paramref name="exitCode"/>.</summary>
        public void Stub(string name, int exitCode)
        {
            string path = Path.Combine(_stubDir, name);
            string code = exitCode.ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(path, $"#!/bin/sh\necho \"{name} $*\" >> \"$STUB_MARKER\"\nexit {code}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        /// <summary>Runs <c>scripts/proc-guard.sh</c> with the stub directory first on PATH and a fixture process table.</summary>
        public ProcessResult Guard(string[] guardArguments)
        {
            ProcessStartInfo startInfo = new("/bin/bash")
            {
                WorkingDirectory = KyberWeaveTestPaths.ToolRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "proc-guard.sh"));
            foreach (string argument in guardArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            startInfo.Environment["PATH"] = $"{_stubDir}:{originalPath}";
            startInfo.Environment["STUB_MARKER"] = _markerPath;
            startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath("ps-clean.sh");

            // Empty stdin, redirected: the guard sees a non-terminal, the same as a dev agent's shell tool.
            return ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
        }

        public void Dispose()
        {
            _temp.Dispose();
        }
    }
}
