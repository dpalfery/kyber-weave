using KyberWeave.Application.Doctor;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The wait before a sync is retried after CodeGraph reports its own lock held, on a clock the test moves.</summary>
public sealed class CodeGraphIndexRetryClockTests
{
    private readonly InMemoryFileSystem _files = new();

    private string Root => Path.Combine(_files.Root, "kw", "repo");

    private string LockDirectory => Path.Combine(_files.Root, "kw", "locks");

    [Fact]
    public async Task ARetryWaitsOnTheTimeProviderNotOnTheWallClock()
    {
        FakeTimeProvider clock = new();
        int syncs = 0;
        ScriptedProcessRunner executor = new ScriptedProcessRunner()
            .Script("codegraph", request =>
            {
                if (request.Arguments.Contains("sync"))
                {
                    return ++syncs == 1
                        ? new ProcessRunResult(1, string.Empty, TimedOut: false)
                        {
                            StandardOutput = "Syncing CodeGraph\n",
                            StandardError = "Failed to sync: Sync could not acquire the file lock; retry when the index is available. "
                                + "CodeGraph database is locked by another process (PID 91106).\n"
                        }
                        : new ProcessRunResult(0, string.Empty, TimedOut: false);
                }

                return new ProcessRunResult(0, string.Empty, TimedOut: false);
            })
            .Script("git", new ProcessRunResult(0, string.Empty, TimedOut: false));
        CodeGraphIndexProvisioner provisioner = new(
            new FakeDoctorFileSystem().WithDirectory(Path.Combine(Root, ".codegraph")),
            new FreeWorkspace(),
            FakePathResolver.OverEmptyFileSystem(),
            new FakeProcessEnvironment(currentDirectory: Path.Combine(_files.Root, "kw", "work")),
            clock);
        DoctorOptions options = DoctorOptions.Defaults with { HeldLockRetryPauses = [TimeSpan.FromMinutes(5)] };

        Task<CodeGraphOutcome> run = provisioner.RunAsync(Root, DoctorMode.Ensure, options, executor, LockDirectory);
        await WaitUntilAsync(() => Volatile.Read(ref syncs) == 1);
        await Task.Delay(100);

        Assert.False(run.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref syncs));

        clock.Advance(TimeSpan.FromMinutes(5));
        _ = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, Volatile.Read(ref syncs));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The sync was never started.");
            await Task.Delay(10);
        }
    }

    private sealed class FreeWorkspace : ICodeGraphIndexWorkspace
    {
        public Task<CodeGraphIndexLockAttempt> TryAcquireLockAsync(
            string path,
            TimeSpan wait,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeGraphIndexLockAttempt(new Handle(), null));

        public bool IsWritable(string directory) => true;

        public bool IsCodeGraphLockHeld(string indexDirectory) => false;

        private sealed class Handle : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
