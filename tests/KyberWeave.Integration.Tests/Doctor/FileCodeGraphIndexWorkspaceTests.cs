using KyberWeave.Data.Doctor;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Integration.Tests.Doctor;

/// <summary>The lock workspace waits on the clock it is given, and holds the lock on the real disk.</summary>
public sealed class FileCodeGraphIndexWorkspaceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void FreeLock_IsTakenAtOnceWithoutTheClockMoving()
    {
        using TempDirectory fixture = new();
        FakeTimeProvider time = new();
        FileCodeGraphIndexWorkspace workspace = new(time);

        using IDisposable? held = workspace.TryAcquireLock(Path.Combine(fixture.Path, "index.lock"), Wait, out string? failure);

        Assert.NotNull(held);
        Assert.Null(failure);
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

        IDisposable? held = workspace.TryAcquireLock(lockPath, Wait, out string? failure);
        await stop.CancelAsync();
        await clockDriver;

        Assert.Null(held);
        Assert.Null(failure);
        Assert.True(time.GetUtcNow() - new FakeTimeProvider().GetUtcNow() >= Wait);
    }
}
