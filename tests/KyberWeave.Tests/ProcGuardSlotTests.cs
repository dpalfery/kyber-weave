using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract tests for the machine-wide test slot of <c>scripts/proc-guard.sh run --slot &lt;name&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Design under test. Several agent harnesses share one machine, and a full <c>dotnet test</c> run saturates its cores,
/// so overlapping runs slow each other down and flake the load-sensitive classes. <c>--slot</c> makes a run take one of
/// <c>--slot-count</c> machine-wide slots first. A slot is a lock directory <c>&lt;name&gt;.&lt;n&gt;.lock</c> created
/// with an atomic <c>mkdir</c>, because macOS has no <c>flock</c>. A <c>holder</c> file in it records the guard's pid, its
/// start time, and a task label. A busy caller prints who holds the slot and polls for up to <c>--slot-wait</c> seconds,
/// then gives up with its own exit code, 88. A lock whose holder is gone is recovered.
/// </para>
/// <para>
/// Why the cases are shaped this way. A holder is simulated by writing the lock directory by hand, with the test host's own
/// pid for a live holder and a pid far above any real pid_max for a dead one, so no case needs a second real holder
/// process. The slot directory is a per-test temp directory, handed over through <c>KW_PROC_GUARD_SLOT_DIR</c>, so a case
/// never touches the real <c>~/.cache/kyber-weave/test-slots</c>.
/// </para>
/// <para>
/// Safety of this suite: every case starts only the guard, under <c>/bin/bash</c>, and a trivial <c>sh -c</c> or
/// <c>sleep</c> that exits by itself within seconds. The process table the guard reads is the empty fixture
/// <c>ps-clean.sh</c>. No case starts node, vitest, playwright, or the dashboard.
/// </para>
/// </remarks>
public sealed class ProcGuardSlotTests : IDisposable
{
    private const int SlotWaitExitCode = 88;
    private const int TimeoutExitCode = 124;
    private const int UsageExitCode = 2;
    private const string SlotName = "dotnet-test";

    /// <summary>A pid far above any real pid_max, so signalling it can only report that it does not exist.</summary>
    private const int DeadPid = 2147483001;

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueuedDeadline = TimeSpan.FromSeconds(10);

    private readonly TempDirectory slotRoot = new();
    private readonly TempDirectory work = new();

    public void Dispose()
    {
        slotRoot.Dispose();
        work.Dispose();
    }

    /// <summary>
    /// With the slot free, the command runs, its own exit code comes back unchanged, and the slot is released afterwards.
    /// </summary>
    /// <remarks>
    /// A slot that stayed taken after a failing run would block every later test run on the machine until its holder
    /// was found to be dead, and a guard that rewrote the exit code would turn a failing suite into a pass.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void RunWhenSlotIsFree_RunsCommandPassesExitCodeThroughAndReleasesSlot(int commandExitCode)
    {
        SkipIfBashUnavailable();
        string marker = Path.Combine(work.Path, "ran");

        GuardRun result = RunGuard(
            ["run", "--slot", SlotName, "--", "sh", "-c", "echo ran > \"$0\"; exit " + commandExitCode.ToString(CultureInfo.InvariantCulture), marker]);

        Assert.Equal(commandExitCode, result.ExitCode);
        Assert.True(File.Exists(marker), "The wrapped command did not run.");
        AssertNoLocksRemain();
    }

    /// <summary>
    /// With a live process holding the only slot and no wait allowed, the guard exits 88, names the holder's pid and task on
    /// stderr, and runs nothing.
    /// </summary>
    /// <remarks>
    /// Naming the holder is what tells a queued developer whether to wait or to go and look at the run in the way.
    /// Exit 88 is its own code so a slot timeout is never mistaken for 124, the command's own timeout.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RunWhenSlotIsHeldByLiveProcess_ExitsSlotWaitCodeNamingHolderAndRunsNothing(int slotWaitSeconds)
    {
        SkipIfBashUnavailable();
        SeedLock(SlotName, 1, Environment.ProcessId, "council-gate-of-another-run");
        string marker = Path.Combine(work.Path, "ran");

        GuardRun result = RunGuard(
            ["run", "--slot", SlotName, "--slot-wait", slotWaitSeconds.ToString(CultureInfo.InvariantCulture), "--", "sh", "-c", "echo ran > \"$0\"", marker]);

        Assert.Equal(SlotWaitExitCode, result.ExitCode);
        Assert.Contains("pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture), result.StandardError, StringComparison.Ordinal);
        Assert.Contains("council-gate-of-another-run", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--slot-wait", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(marker), "The wrapped command ran although the slot was held.");
    }

    /// <summary>
    /// A lock whose holder pid is dead is recovered: the command runs, the exit code is its own, and stderr says the lock
    /// was stale.
    /// </summary>
    /// <remarks>
    /// A run killed with SIGKILL, or a machine that rebooted mid-run, leaves its lock behind. Without recovery the slot
    /// would stay taken forever and every later run would queue until its wait expired.
    /// </remarks>
    [Fact]
    public void RunWhenSlotHolderIsDead_RecoversStaleLockAndRunsCommand()
    {
        SkipIfBashUnavailable();
        SeedLock(SlotName, 1, DeadPid, "crashed-run");
        string marker = Path.Combine(work.Path, "ran");

        GuardRun result = RunGuard(
            ["run", "--slot", SlotName, "--slot-wait", "0", "--", "sh", "-c", "echo ran > \"$0\"", marker]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(marker), "The wrapped command did not run after the stale lock was recovered.");
        Assert.Contains("stale", result.StandardError, StringComparison.Ordinal);
        AssertNoLocksRemain();
    }

    /// <summary>
    /// A holder file whose recorded start time differs from the live process now using that pid is stale.
    /// </summary>
    /// <remarks>
    /// Pids are reused. A crashed run's pid may now belong to an unrelated process, and judging liveness by the pid alone
    /// would keep the slot taken for as long as that process lives.
    /// </remarks>
    [Fact]
    public void RunWhenHolderPidWasReusedByAnotherProcess_TreatsLockAsStale()
    {
        SkipIfBashUnavailable();
        SeedLock(SlotName, 1, Environment.ProcessId, "reused-pid", started: "Thu Jan  1 00:00:00 1970");

        GuardRun result = RunGuard(
            ["run", "--slot", SlotName, "--slot-wait", "0", "--", "sh", "-c", "exit 0"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stale", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lock directory that is seconds old and has no holder file yet is respected, not stolen.
    /// </summary>
    /// <remarks>
    /// <c>mkdir</c> and the holder-file write are two steps. A caller that looks between them must treat the lock as
    /// being created by a live caller, or two runs would hold the one slot.
    /// </remarks>
    [Fact]
    public void RunWhenLockDirectoryIsFreshAndHasNoHolderFile_WaitsInsteadOfStealing()
    {
        SkipIfBashUnavailable();
        Directory.CreateDirectory(LockPath(SlotName, 1));

        GuardRun result = RunGuard(
            ["run", "--slot", SlotName, "--slot-wait", "0", "--", "sh", "-c", "exit 0"]);

        Assert.Equal(SlotWaitExitCode, result.ExitCode);
    }

    /// <summary>
    /// A lock directory that has had no holder file for minutes is abandoned and is recovered.
    /// </summary>
    /// <remarks>
    /// The pair with the test above. A holder killed between the <c>mkdir</c> and the holder-file write leaves an empty
    /// lock directory, and only its age can show that nobody is going to finish creating it.
    /// </remarks>
    [Fact]
    public void RunWhenLockDirectoryHasHadNoHolderFileForMinutes_RecoversIt()
    {
        SkipIfBashUnavailable();
        string lockPath = LockPath(SlotName, 1);
        Directory.CreateDirectory(lockPath);
        Directory.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-5));

        GuardRun result = RunGuard(
            ["run", "--slot", SlotName, "--slot-wait", "0", "--", "sh", "-c", "exit 0"]);

        Assert.Equal(0, result.ExitCode);
        AssertNoLocksRemain();
    }

    /// <summary>
    /// A busy caller prints who holds the slot, keeps waiting, and runs its command once the holder lets go.
    /// </summary>
    /// <remarks>
    /// The holder is released only after the queued caller has printed its waiting line, so the run proves the caller really
    /// queued rather than finding the slot free by luck of timing.
    /// </remarks>
    [Fact]
    public async Task RunWhenSlotIsBusy_QueuesPrintsHolderAndRunsAfterHolderReleases()
    {
        SkipIfBashUnavailable();
        SeedLock(SlotName, 1, Environment.ProcessId, "long-council-run");
        string marker = Path.Combine(work.Path, "ran");
        string stderrFile = Path.Combine(work.Path, "queued.err");

        Task<GuardRun> queued = Task.Run(() => RunGuard(
            ["run", "--slot", SlotName, "--slot-wait", "30", "--", "sh", "-c", "echo ran > \"$0\"", marker], stderrFile));
        await WaitForStderrAsync(stderrFile, "waiting", QueuedDeadline);
        Assert.False(File.Exists(marker), "The queued command ran while the slot was still held.");
        Directory.Delete(LockPath(SlotName, 1), recursive: true);
        GuardRun result = await queued;

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(marker), "The queued command did not run after the slot was released.");
        Assert.Contains("long-council-run", result.StandardError, StringComparison.Ordinal);
        AssertNoLocksRemain();
    }

    /// <summary>
    /// Time spent queued for the slot is not counted against <c>--timeout</c>: a command that fits its timeout still passes
    /// after a queue wait longer than that timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the longer the machine is busy the likelier a healthy run is to be killed as hung (exit 124), and the
    /// rerun adds the load the slot is there to remove. The timeout starts when the command starts.
    /// </para>
    /// <para>
    /// The guarantee is about wall-clock time, so the holder is released only once the queue wait has passed the timeout. The
    /// wrapped <c>sleep 0.3</c> keeps the command alive across the guard's first poll, where a timeout that had started at
    /// the queue would trip.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunWhenQueuedLongerThanTimeout_QueueWaitIsNotCountedAgainstTimeout()
    {
        SkipIfBashUnavailable();
        SeedLock(SlotName, 1, Environment.ProcessId, "slow-holder");
        string stderrFile = Path.Combine(work.Path, "queued.err");
        TimeSpan timeout = TimeSpan.FromSeconds(2);

        Task<GuardRun> queued = Task.Run(() => RunGuard(
            ["run", "--slot", SlotName, "--slot-wait", "30", "--timeout", "2", "--", "sleep", "0.3"], stderrFile));
        await WaitForStderrAsync(stderrFile, "waiting", QueuedDeadline);
        Stopwatch queuedFor = Stopwatch.StartNew();
        while (queuedFor.Elapsed < timeout + TimeSpan.FromMilliseconds(500))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Directory.Delete(LockPath(SlotName, 1), recursive: true);
        GuardRun result = await queued;

        Assert.NotEqual(TimeoutExitCode, result.ExitCode);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// With <c>--slot-count 2</c> a caller takes the free slot while one is held, and queues only when both are held.
    /// </summary>
    [Fact]
    public void RunWithTwoSlots_TakesTheFreeOneAndQueuesOnlyWhenBothAreHeld()
    {
        SkipIfBashUnavailable();
        SeedLock(SlotName, 1, Environment.ProcessId, "first-holder");

        GuardRun whileOneFree = RunGuard(
            ["run", "--slot", SlotName, "--slot-count", "2", "--slot-wait", "0", "--", "sh", "-c", "exit 0"]);
        SeedLock(SlotName, 1, Environment.ProcessId, "first-holder");
        SeedLock(SlotName, 2, Environment.ProcessId, "second-holder");
        GuardRun whileBothHeld = RunGuard(
            ["run", "--slot", SlotName, "--slot-count", "2", "--slot-wait", "0", "--", "sh", "-c", "exit 0"]);

        Assert.Equal(0, whileOneFree.ExitCode);
        Assert.Equal(SlotWaitExitCode, whileBothHeld.ExitCode);
    }

    /// <summary>
    /// Two slotted commands started at the same instant never run at the same time.
    /// </summary>
    /// <remarks>
    /// This is the property the slot exists for. Each command logs a start and an end around a short sleep; any overlap
    /// would interleave the log.
    /// </remarks>
    [Fact]
    public async Task RunTwoSlottedCommandsAtOnce_NeverOverlap()
    {
        SkipIfBashUnavailable();
        string log = Path.Combine(work.Path, "order.log");
        string[] Command(string id) =>
            ["run", "--slot", SlotName, "--slot-wait", "30", "--", "sh", "-c", $"echo start-{id} >> \"$0\"; sleep 1; echo end-{id} >> \"$0\"", log];

        Task<GuardRun> first = Task.Run(() => RunGuard(Command("a"), Path.Combine(work.Path, "a.err")));
        Task<GuardRun> second = Task.Run(() => RunGuard(Command("b"), Path.Combine(work.Path, "b.err")));
        GuardRun[] results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        string[] lines = await File.ReadAllLinesAsync(log);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("start-", lines[0], StringComparison.Ordinal);
        Assert.Equal("end-" + lines[0]["start-".Length..], lines[1]);
        Assert.StartsWith("start-", lines[2], StringComparison.Ordinal);
        Assert.Equal("end-" + lines[2]["start-".Length..], lines[3]);
        AssertNoLocksRemain();
    }

    /// <summary>
    /// A run without <c>--slot</c> neither takes nor waits for a slot, even while one is held.
    /// </summary>
    /// <remarks>
    /// The slot is for full test runs only. Builds and format checks go through the same guard and must never queue.
    /// </remarks>
    [Fact]
    public void RunWithoutSlotFlag_IgnoresAHeldSlot()
    {
        SkipIfBashUnavailable();
        SeedLock(SlotName, 1, Environment.ProcessId, "a-test-run");

        GuardRun result = RunGuard(["run", "--", "sh", "-c", "exit 0"]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(LockPath(SlotName, 1)), "A run without --slot disturbed the held slot.");
    }

    /// <summary>
    /// A command killed by <c>--timeout</c> exits 124 and still releases its slot.
    /// </summary>
    /// <remarks>
    /// A hung suite is exactly the run most likely to be killed, and a slot left behind by it would block the machine.
    /// </remarks>
    [Fact]
    public void RunWhenCommandTimesOut_ExitsTimeoutCodeAndReleasesSlot()
    {
        SkipIfBashUnavailable();

        GuardRun result = RunGuard(["run", "--slot", SlotName, "--timeout", "1", "--", "sleep", "5"]);

        Assert.Equal(TimeoutExitCode, result.ExitCode);
        AssertNoLocksRemain();
    }

    /// <summary>
    /// Bad slot flags are a usage error (exit 2) that happens before anything is created in the slot directory.
    /// </summary>
    /// <remarks>
    /// <c>--slot-wait</c> and <c>--slot-count</c> without <c>--slot</c> would silently do nothing, so they are refused. A slot
    /// name is a path component and must not be able to leave the slot directory.
    /// </remarks>
    [Theory]
    [InlineData("--slot-wait", "5")]
    [InlineData("--slot-count", "2")]
    [InlineData("--slot", "../escape")]
    [InlineData("--slot", "")]
    public void RunWithBadSlotFlags_ExitsUsageErrorAndCreatesNothing(string flag, string value)
    {
        SkipIfBashUnavailable();

        GuardRun result = RunGuard(["run", flag, value, "--", "sh", "-c", "exit 0"]);

        Assert.Equal(UsageExitCode, result.ExitCode);
        Assert.Empty(Directory.GetFileSystemEntries(slotRoot.Path));
    }

    /// <summary>
    /// A slot count that is zero or not a number is a usage error.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("two")]
    [InlineData("-1")]
    public void RunWithInvalidSlotCount_ExitsUsageError(string count)
    {
        SkipIfBashUnavailable();

        GuardRun result = RunGuard(["run", "--slot", SlotName, "--slot-count", count, "--", "sh", "-c", "exit 0"]);

        Assert.Equal(UsageExitCode, result.ExitCode);
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

    private string LockPath(string name, int index)
    {
        return Path.Combine(slotRoot.Path, $"{name}.{index.ToString(CultureInfo.InvariantCulture)}.lock");
    }

    /// <summary>
    /// Writes the lock directory a running guard would hold: the lock directory and its holder file.
    /// </summary>
    private void SeedLock(string name, int index, int pid, string task, string? started = null)
    {
        string lockPath = LockPath(name, index);
        Directory.CreateDirectory(lockPath);
        List<string> lines =
        [
            "pid=" + pid.ToString(CultureInfo.InvariantCulture),
            "since=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "task=" + task,
            "cmd=dotnet test"
        ];
        if (started is not null)
        {
            lines.Add("started=" + started);
        }

        File.WriteAllLines(Path.Combine(lockPath, "holder"), lines);
    }

    private void AssertNoLocksRemain()
    {
        string[] remaining = Directory.GetFileSystemEntries(slotRoot.Path);
        Assert.True(remaining.Length == 0, "The slot directory still holds: " + string.Join(", ", remaining));
    }

    /// <summary>
    /// Runs <c>scripts/proc-guard.sh</c> with the slot directory in the per-test temp directory and the process table from
    /// a fixture, sending the guard's stderr to a file so a test can read it while the guard is still running.
    /// </summary>
    /// <remarks>
    /// The environment is set on the child's <see cref="ProcessStartInfo"/>, never on the test process, so parallel test
    /// classes cannot observe it. The stderr file is read back into the result once the guard exits.
    /// </remarks>
    private GuardRun RunGuard(string[] guardArguments, string? stderrFile = null)
    {
        stderrFile ??= Path.Combine(work.Path, "guard-" + Guid.NewGuid().ToString("N") + ".err");
        ProcessStartInfo startInfo = new("/bin/bash")
        {
            WorkingDirectory = KyberWeaveTestPaths.ToolRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec /bin/bash \"$@\" 2>\"$KW_TEST_GUARD_STDERR\"");
        startInfo.ArgumentList.Add("proc-guard-slot-test");
        startInfo.ArgumentList.Add(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "proc-guard.sh"));
        foreach (string argument in guardArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["KW_PROC_GUARD_PS"] = FixturePath("ps-clean.sh");
        startInfo.Environment["KW_PROC_GUARD_SLOT_DIR"] = slotRoot.Path;
        startInfo.Environment["KW_TEST_GUARD_STDERR"] = stderrFile;

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GuardTimeout);
        return new GuardRun(result.ExitCode, result.StandardOutput, ReadStderr(stderrFile));
    }

    private static string ReadStderr(string stderrFile)
    {
        if (!File.Exists(stderrFile))
        {
            return string.Empty;
        }

        using FileStream stream = new(stderrFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static async Task WaitForStderrAsync(string stderrFile, string text, TimeSpan deadline)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!ReadStderr(stderrFile).Contains(text, StringComparison.Ordinal))
        {
            Assert.True(clock.Elapsed < deadline, $"The guard never printed '{text}'; stderr was: {ReadStderr(stderrFile)}");
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private sealed record GuardRun(int ExitCode, string StandardOutput, string StandardError);
}
