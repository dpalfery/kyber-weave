using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Doctor;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Integration.Tests.Doctor;

/// <summary>The lock workspace waits on the clock it is given, and holds the lock on the real disk.</summary>
public sealed class FileCodeGraphIndexWorkspaceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FreeLock_IsTakenAtOnceWithoutTheClockMoving()
    {
        using TempDirectory fixture = new();
        FakeTimeProvider time = new();
        FileCodeGraphIndexWorkspace workspace = new(time);

        CodeGraphIndexLockAttempt attempt = await workspace.TryAcquireLockAsync(Path.Combine(fixture.Path, "index.lock"), Wait);
        using IDisposable? held = attempt.Lock;

        Assert.NotNull(held);
        Assert.Null(attempt.Failure);
    }

    [Fact]
    public async Task HeldLock_GivesUpOnceTheClockPassesTheWait()
    {
        using TempDirectory fixture = new();
        string lockPath = Path.Combine(fixture.Path, "index.lock");
        using FileStream holder = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        FakeTimeProvider time = new();
        FileCodeGraphIndexWorkspace workspace = new(time);

        using CancellationTokenSource stop = new();
        Task clockDriver = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(5, CancellationToken.None);
            }
        });

        CodeGraphIndexLockAttempt attempt = await workspace.TryAcquireLockAsync(lockPath, Wait, CancellationToken.None);
        IDisposable? held = attempt.Lock;
        string? failure = attempt.Failure;
        await stop.CancelAsync();
        await clockDriver;

        Assert.Null(held);
        Assert.Null(failure);
        Assert.True(time.GetUtcNow() - new FakeTimeProvider().GetUtcNow() >= Wait);
    }
}
