using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for the process watchdog <c>scripts/proc-guard.sh</c>.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog exists because a test run fork-bombed node (536 processes) and crashed three
/// developer machines. It is the safety net every later test run goes through. These tests pin
/// the observable contract: exit 86 when a process cap trips, exit 87 when processes survive a
/// command, and the command's own exit code when neither happens.
/// </para>
/// <para>
/// Safety of this suite itself: every case starts at most one real child, either a
/// <c>sleep 30</c> or a trivial <c>sh -c</c>, and the <c>sleep 30</c> is killed in <c>finally</c>.
/// No case starts node, vitest, playwright, or the dashboard. The process table the watchdog reads
/// is faked by the scripts under <c>Fixtures/proc-guard/</c>. Their pids sit far above any real
/// pid_max, so a fake row can never resolve to a live process.
/// </para>
/// </remarks>
public sealed class ProcGuardScriptTests
{
    private const int CapTrippedExitCode = 86;
    private const int LeftoversExitCode = 87;

    /// <summary>Pid of the orphan row in <c>ps-orphan.sh</c>.</summary>
    private const int OrphanPid = 2147483003;

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KillDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A tree cap of 1 with two fixture descendants must kill the real <c>sleep</c> within five seconds,
    /// exit 86, and name the cap on stderr.
    /// </summary>
    /// <remarks>
    /// Without a tree cap the watchdog lets a runaway fan-out run to completion. The cap is the only
    /// thing that stops a fork-bomb in progress, so it has to kill the wrapped child, not just report.
    /// </remarks>
    [Fact]
    public async Task RunWhenTreeExceedsMaxTree_KillsChildWithinFiveSecondsAndExits86NamingTheCap()
    {
        SkipIfBashUnavailable();
        IReadOnlyList<int> baseline = ListSleepThirtyPids();
        try
        {
            ProcessResult result = RunGuard(["run", "--max-tree", "1", "--", "sleep", "30"], "ps-tree-2.sh");
            IReadOnlyList<int> survivors = await WaitForNewSleepThirtyAsync(baseline, KillDeadline);

            Assert.Equal(CapTrippedExitCode, result.ExitCode);
            Assert.Contains("max-tree", result.StandardError, StringComparison.Ordinal);
            Assert.True(
                survivors.Count == 0,
                $"sleep 30 survived the tree-cap trip: pids {string.Join(", ", survivors)}");
        }
        finally
        {
            KillNewSleepThirtyPids(baseline);
        }
    }

    /// <summary>
    /// A node cap of 3 with three fixture node processes must kill the real <c>sleep</c> within five
    /// seconds, exit 86, and name the cap on stderr.
    /// </summary>
    /// <remarks>
    /// The node cap is the direct guard against the original incident: node fork-bombed to 536
    /// processes. It counts node processes, not the tree, so a fan-out of node workers trips it even
    /// when the total tree stays small.
    /// </remarks>
    [Fact]
    public async Task RunWhenUserNodeCountExceedsMaxNode_KillsChildWithinFiveSecondsAndExits86NamingTheCap()
    {
        SkipIfBashUnavailable();
        IReadOnlyList<int> baseline = ListSleepThirtyPids();
        try
        {
            ProcessResult result = RunGuard(["run", "--max-node", "3", "--", "sleep", "30"], "ps-node-3.sh");
            IReadOnlyList<int> survivors = await WaitForNewSleepThirtyAsync(baseline, KillDeadline);

            Assert.Equal(CapTrippedExitCode, result.ExitCode);
            Assert.Contains("max-node", result.StandardError, StringComparison.Ordinal);
            Assert.True(
                survivors.Count == 0,
                $"sleep 30 survived the node-cap trip: pids {string.Join(", ", survivors)}");
        }
        finally
        {
            KillNewSleepThirtyPids(baseline);
        }
    }

    /// <summary>
    /// A command that exits 0 but leaves a survivor carrying the repository root must exit 87 and list
    /// the survivor's pid.
    /// </summary>
    /// <remarks>
    /// A clean exit code does not mean a clean machine. A test runner can exit 0 and leave its node or
    /// vitest children behind, still holding the repository. Reporting only the command's exit code
    /// would let those leftovers pass as green.
    /// </remarks>
    [Fact]
    public void RunWhenCommandLeavesRepoOrphan_Exits87AndListsOrphan()
    {
        SkipIfBashUnavailable();

        ProcessResult result = RunGuard(["run", "--", "sh", "-c", "exit 0"], "ps-orphan.sh");
        string combinedOutput = result.StandardOutput + Environment.NewLine + result.StandardError;

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(OrphanPid.ToString(CultureInfo.InvariantCulture), combinedOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clean command must pass its own exit code through unchanged, for both success and a non-zero code.
    /// </summary>
    /// <remarks>
    /// The watchdog wraps commands, so it must not swallow or rewrite their results. A guard that always
    /// exited 0 would turn every failing test run into a pass.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void RunWhenCommandIsClean_PassesCommandExitCodeThrough(int commandExitCode)
    {
        SkipIfBashUnavailable();

        string shellCommand = "exit " + commandExitCode.ToString(CultureInfo.InvariantCulture);
        ProcessResult result = RunGuard(["run", "--", "sh", "-c", shellCommand], "ps-clean.sh");

        Assert.Equal(commandExitCode, result.ExitCode);
    }

    /// <summary>
    /// <c>check-leftovers --root &lt;dir&gt;</c> must exit 87 when a survivor carries that root.
    /// </summary>
    [Fact]
    public void CheckLeftovers_WhenSurvivorCarriesRoot_Exits87()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunGuard(["check-leftovers", "--root", root.Path], "ps-orphan.sh");
        string combinedOutput = result.StandardOutput + Environment.NewLine + result.StandardError;

        Assert.Equal(LeftoversExitCode, result.ExitCode);
        Assert.Contains(OrphanPid.ToString(CultureInfo.InvariantCulture), combinedOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>check-leftovers --root &lt;dir&gt;</c> must exit 0 when nothing survives.
    /// </summary>
    /// <remarks>
    /// The pair with the test above. A check that always exits 87 would block every clean run, and
    /// developers would learn to bypass it.
    /// </remarks>
    [Fact]
    public void CheckLeftovers_WhenNoSurvivors_Exits0()
    {
        SkipIfBashUnavailable();
        using TempDirectory root = new();

        ProcessResult result = RunGuard(["check-leftovers", "--root", root.Path], "ps-clean.sh");

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// Every <c>npm --prefix dash</c> and <c>dotnet test</c> command line in <c>scripts/fast-loop.sh</c> must
    /// run through <c>scripts/proc-guard.sh run --</c>.
    /// </summary>
    /// <remarks>
    /// The fast loop is the path developers run most often, so an unwrapped <c>npm</c> or <c>dotnet test</c>
    /// line there is a bypass of the watchdog. The check skips hint text that merely prints these commands
    /// (<c>printf</c> and <c>echo</c>) and comment lines, and requires at least one invocation so an empty
    /// match cannot pass vacuously.
    /// </remarks>
    [Fact]
    public void FastLoopNpmDashAndDotnetTestLines_RunThroughProcGuard()
    {
        string script = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "fast-loop.sh"));
        string[] lines = script.Split('\n');

        int invocationCount = 0;
        List<string> unwrapped = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string code = lines[index].Trim();
            if (code.Length == 0 || code.StartsWith('#') || code.StartsWith("printf ", StringComparison.Ordinal) ||
                code.StartsWith("echo ", StringComparison.Ordinal))
            {
                continue;
            }

            if (!code.Contains("npm --prefix dash", StringComparison.Ordinal) && !IsDotnetTestInvocation(code))
            {
                continue;
            }

            invocationCount++;
            if (!code.Contains("scripts/proc-guard.sh run --", StringComparison.Ordinal))
            {
                unwrapped.Add($"line {index + 1}: {code}");
            }
        }

        Assert.True(invocationCount > 0, "No npm --prefix dash or dotnet test lines were found in fast-loop.sh.");
        Assert.True(
            unwrapped.Count == 0,
            "fast-loop.sh runs these without scripts/proc-guard.sh run --: " + string.Join("; ", unwrapped));
    }

    private static bool IsDotnetTestInvocation(string code)
    {
        string[] tokens = code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index + 1 < tokens.Length; index++)
        {
            if (tokens[index] == "dotnet" && tokens[index + 1] == "test")
            {
                return true;
            }
        }

        return false;
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
        return Path.Combine(KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests", "Fixtures", "proc-guard", fixtureName);
    }

    /// <summary>
    /// Runs <c>scripts/proc-guard.sh</c> with the given arguments, reading the process table from a fixture.
    /// </summary>
    /// <remarks>
    /// The guard reads the process table through <c>KW_PROC_GUARD_PS</c>. Pointing it at a fixture
    /// keeps every test off the real process table, so a test cannot be tripped by, or kill, anything
    /// else on the machine.
    /// </remarks>
    private static ProcessResult RunGuard(string[] guardArguments, string fixtureName)
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

        startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath(fixtureName);

        return ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
    }

    /// <summary>
    /// Lists the pids of processes whose full command line is exactly <c>sleep 30</c>.
    /// </summary>
    /// <remarks>
    /// The pattern is anchored. An unanchored <c>sleep 30</c> would also match the guard's own command
    /// line, which contains the same words, and the leak check would then never pass.
    /// </remarks>
    private static IReadOnlyList<int> ListSleepThirtyPids()
    {
        ProcessStartInfo startInfo = new("pgrep")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("^sleep 30$");

        // pgrep exits 1 when nothing matches, which is the normal case, so the exit code is not checked.
        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(10));

        List<int> pids = [];
        foreach (string text in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    /// <summary>
    /// Waits until no <c>sleep 30</c> that was absent at <paramref name="baseline"/> remains, or the deadline passes.
    /// </summary>
    /// <returns>The new <c>sleep 30</c> pids still alive at the deadline. Empty means the child was killed.</returns>
    private static async Task<IReadOnlyList<int>> WaitForNewSleepThirtyAsync(IReadOnlyList<int> baseline, TimeSpan deadline)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (true)
        {
            IReadOnlyList<int> survivors = ListSleepThirtyPids().Where(pid => !baseline.Contains(pid)).ToArray();
            if (survivors.Count == 0 || clock.Elapsed >= deadline)
            {
                return survivors;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Kills any <c>sleep 30</c> that appeared since <paramref name="baseline"/>. Runs in <c>finally</c> so a failed
    /// assertion never leaves a real child running.
    /// </summary>
    private static void KillNewSleepThirtyPids(IReadOnlyList<int> baseline)
    {
        foreach (int pid in ListSleepThirtyPids().Where(pid => !baseline.Contains(pid)))
        {
            ProcessStartInfo startInfo = new("kill")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-KILL");
            startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));

            // A pid that already exited makes kill fail. That is the outcome wanted, so the result is ignored.
            _ = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(5));
        }
    }
}
