using KyberWeave.Application.Doctor;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The one <c>doctor --ensure</c> case whose subject is the real index lock file: the baseline git status must be
/// sampled while the lock is held, which only an exclusive open of a real file can observe. The rest of the ensure
/// contract runs in memory in <c>DoctorEnsureTests</c>.
/// </summary>
public sealed class DoctorEnsureRealLockFileTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _stubs = new();

    public void Dispose()
    {
        _stubs.Dispose();
        _root.Dispose();
    }

    private string Root => _root.Path;

    /// <summary>
    /// Baseline tracked changes must be sampled AFTER lock acquisition so edits made while waiting
    /// are not classified as newly written by the index preparation and restored.
    /// </summary>
    [Fact]
    public void EnsureSamplesBaselineTrackedChangesAfterLockAcquisition()
    {
        string lockPath = CodeGraphIndexRules.LockPathFor(Root, Path.GetTempPath());
        bool lockHeldDuringBaselineStatus = false;
        int statusCalls = 0;

        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                string arguments = string.Join(' ', startInfo.ArgumentList);
                if (arguments.Contains("rev-parse", StringComparison.Ordinal))
                    return new ProcessResult(0, Root + "\n", string.Empty);

                if (arguments.Contains("status", StringComparison.Ordinal))
                {
                    statusCalls++;
                    if (statusCalls == 1)
                    {
                        if (File.Exists(lockPath))
                        {
                            try
                            {
                                using FileStream testOpen = new(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                                lockHeldDuringBaselineStatus = false;
                            }
                            catch (IOException)
                            {
                                lockHeldDuringBaselineStatus = true;
                            }
                        }
                    }

                    return new ProcessResult(0, string.Empty, string.Empty);
                }

                return DoctorHarness.Ok();
            }

            if (startInfo.FileName == "codegraph")
                return DoctorHarness.Ok();

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        RunEnsure(executor);

        Assert.True(
            lockHeldDuringBaselineStatus,
            "Baseline tracked changes must be sampled AFTER lock acquisition so edits made while waiting are not lost.");
    }

    /// <summary>
    /// Runs <c>doctor --ensure &lt;root&gt;</c> through the real CLI parser with the scripted child
    /// executor, and returns its exit code.
    /// </summary>
    /// <summary>
    /// A sync that finds CodeGraph's own lock held on every attempt is not repaired: the run passes
    /// with a warning, exits 0, and still proves the index on disk with one explore.
    /// </summary>
    /// <remarks>
    /// The lock is held by CodeGraph's server, its watcher or another sync, so the index is healthy.
    /// <c>codegraph index</c> run against a held lock exits 1 and leaves the database empty, so a
    /// repair here would break a working index.
    /// </remarks>

    /// <summary>
    /// With the lock held, a failed explore proof is a real failure and is still not answered with a
    /// rebuild while another live process holds CodeGraph's lock file.
    /// </summary>
    [Fact]
    public void EnsureNeverRebuildsWhileAnotherLiveProcessHoldsTheCodeGraphLock()
    {
        Directory.CreateDirectory(IndexDirectory);
        File.WriteAllText(
            Path.Combine(IndexDirectory, "codegraph.lock"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("sync", StringComparer.Ordinal) ? HeldIndexLock
                : arguments.Contains("explore", StringComparer.Ordinal) ? new ProcessResult(1, string.Empty, "no answer")
                : DoctorHarness.Ok());

        DoctorReport report = RunEnsureWithoutWaiting(executor);

        DoctorHarness.AssertNoCall(executor, "index");
        Assert.Contains(report.Checks, check => check.Id == "codegraph.explore" && !check.Passed);
        Assert.Equal(1, report.ExitCode);
    }

    /// <summary>
    /// What CodeGraph 1.6.2 answers, captured from a real run, when <c>codegraph sync .</c> finds
    /// <c>.codegraph/codegraph.lock</c> held by a live process.
    /// </summary>

    private static readonly ProcessResult HeldIndexLock = new(
        1,
        "┌  Syncing CodeGraph\n│\n",
        "✗ Failed to sync: Sync could not acquire the file lock; retry when the index is available. "
        + "CodeGraph database is locked by another process (PID 91106). If this is stale, run "
        + "'codegraph unlock' or delete /repo/.codegraph/codegraph.lock\n");

    private string IndexDirectory => Path.Combine(Root, ".codegraph");

    private DoctorReport RunEnsureWithoutWaiting(ScriptedProcessExecutor executor) =>
        DoctorTestServices.Doctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)) with
            {
                HeldLockRetryPauses = [TimeSpan.Zero, TimeSpan.Zero]
            },
            executor,
            _ => null);

    private int RunEnsure(ScriptedProcessExecutor executor)
    {
        DoctorCommand command = DoctorTestEnvironment.Command(false, _ => null, executor, DoctorHarness.HealthyStub(_stubs.Path, Root));
        return DoctorAppHarness.Run(command, "doctor", "--ensure", Root);
    }
}
