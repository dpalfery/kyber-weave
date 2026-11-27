using Xunit;
using KyberDash.Contracts.Services;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Tests;

/// <summary>Process-coordination files and leases for <see cref="ServerStateRepository"/>.</summary>
public sealed class ServerStateRepositoryTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly ServerStateRepository repository = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void DefaultStateDirectory_FollowsExplicitEnvironmentThenHomePrecedence()
    {
        ServerStateRepository explicitRepository = new(directory.Combine("explicit"));
        Assert.Equal(Path.GetFullPath(directory.Combine("explicit")), explicitRepository.DefaultStateDirectory);

        ServerStateRepository environmentRepository = new(Environment.GetEnvironmentVariable(ServerStateRepository.StateDirectoryEnvironmentVariable));
        Assert.False(string.IsNullOrEmpty(environmentRepository.DefaultStateDirectory));
        Assert.True(Path.IsPathFullyQualified(environmentRepository.DefaultStateDirectory));
    }

    [Fact]
    public void ServerInfo_RoundTripsThroughServerJson()
    {
        ServerInfo info = new(Port: 4747, Pid: Environment.ProcessId, StartedAt: "2026-01-01T00:00:00.000Z", Url: new Uri("http://127.0.0.1:4747/"), ApiVersion: 1);

        repository.WriteServerInfo(directory.Path, info);

        string path = directory.Combine(ServerStateRepository.ServerInfoFile);
        Assert.True(File.Exists(path));
        Assert.Equal(info, repository.ReadServerInfo(directory.Path));
        string text = File.ReadAllText(path);
        Assert.Contains("\"port\":4747", text, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"http://127.0.0.1:4747/\"", text, StringComparison.Ordinal);
        Assert.Contains("\"apiVersion\":1", text, StringComparison.Ordinal);

        repository.DeleteServerInfo(directory.Path);
        Assert.Null(repository.ReadServerInfo(directory.Path));
        repository.DeleteServerInfo(directory.Path);
    }

    [Fact]
    public void ServerInfo_IsOwnerReadWriteOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        repository.WriteServerInfo(directory.Path, new ServerInfo(4747, Environment.ProcessId, "2026-01-01T00:00:00.000Z"));

        UnixFileMode mode = File.GetUnixFileMode(directory.Combine(ServerStateRepository.ServerInfoFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void ServerInfo_ReadReturnsNullForMalformedFile()
    {
        File.WriteAllText(directory.Combine(ServerStateRepository.ServerInfoFile), "not json");

        Assert.Null(repository.ReadServerInfo(directory.Path));
    }

    [Fact]
    public void ServerInfo_WriteRejectsInvalidEndpointValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.WriteServerInfo(directory.Path, new ServerInfo(0, Environment.ProcessId, "now")));
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.WriteServerInfo(directory.Path, new ServerInfo(4747, 0, "now")));
        Assert.Throws<ArgumentNullException>(() => repository.WriteServerInfo(directory.Path, null!));
    }

    [Fact]
    public void ServerInfo_RefusesToTraverseASymbolicLink()
    {
        if (OperatingSystem.IsWindows())
            return;

        string realFile = directory.Combine("real.json");
        File.WriteAllText(realFile, "{\"port\":1,\"pid\":1,\"startedAt\":\"x\"}");
        File.CreateSymbolicLink(directory.Combine(ServerStateRepository.ServerInfoFile), realFile);

        Assert.Throws<IOException>(() => repository.WriteServerInfo(directory.Path, new ServerInfo(4747, Environment.ProcessId, "now")));
    }

    [Fact]
    public void JobsLease_TreatsAnUnreadableLockAsHeld()
    {
        File.WriteAllText(directory.Combine(ServerStateRepository.JobsLockFile), "not json");

        Assert.Null(repository.TryAcquireJobsLease(directory.Path));
    }

    [Fact]
    public void JobsLease_HeldByAnotherInstanceOfThisProcessBlocksUntilReleased()
    {
        JobsLease first = Assert.IsType<JobsLease>(repository.TryAcquireJobsLease(directory.Path, pid: null, now: null));

        Assert.Null(repository.TryAcquireJobsLease(directory.Path, pid: null, now: null));

        first.Release();
        using IDisposable? second = repository.TryAcquireJobsLease(directory.Path);
        Assert.NotNull(second);
    }

    [Fact]
    public void JobsLease_BlocksWhileTheRecordedProcessIsAlive()
    {
        int self = Environment.ProcessId;
        string lockPath = directory.Combine(ServerStateRepository.JobsLockFile);
        File.WriteAllText(lockPath, $"{{\"pid\":{self},\"acquiredAt\":\"2026-01-01T00:00:00.000Z\"}}");

        Assert.Null(repository.TryAcquireJobsLease(directory.Path));
        Assert.Contains(
            self.ToString(System.Globalization.CultureInfo.InvariantCulture),
            File.ReadAllText(lockPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void JobsLease_ReleaseRemovesTheLockFile()
    {
        IDisposable lease = Assert.IsAssignableFrom<IDisposable>(repository.TryAcquireJobsLease(directory.Path));

        lease.Dispose();

        Assert.False(File.Exists(directory.Combine(ServerStateRepository.JobsLockFile)));
        using IDisposable? again = repository.TryAcquireJobsLease(directory.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public void JobsLease_ReclaimsALockWhoseOwnerExited()
    {
        string lockPath = directory.Combine(ServerStateRepository.JobsLockFile);
        File.WriteAllText(lockPath, "{\"pid\":999999,\"acquiredAt\":\"2026-01-01T00:00:00.000Z\"}");

        using IDisposable? lease = repository.TryAcquireJobsLease(directory.Path);

        Assert.NotNull(lease);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(lockPath), StringComparison.Ordinal);
    }

    [Fact]
    public void JobsLease_RecordsTheSuppliedOwnerPidAndTimestamp()
    {
        using JobsLease? lease = repository.TryAcquireJobsLease(directory.Path, Environment.ProcessId, DateTimeOffset.UnixEpoch);

        Assert.NotNull(lease);
        Assert.Contains("1970-01-01T00:00:00.000Z", File.ReadAllText(directory.Combine(ServerStateRepository.JobsLockFile)), StringComparison.Ordinal);
        Assert.Null(repository.TryAcquireJobsLease(directory.Path, Environment.ProcessId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task EngineLease_CanBeTakenAgainByTheSameStateDirectory()
    {
        (IEngineOwnerLease? first, _) = await repository.TryAcquireEngineAsync(directory.Path, "0.21.0", "/bin/kyberdash", static () => { });
        await first!.DisposeAsync();

        (IEngineOwnerLease? second, EngineDescriptor? holder) = await repository.TryAcquireEngineAsync(directory.Path, "0.21.0", "/bin/kyberdash", static () => { });

        try
        {
            Assert.Null(holder);
            Assert.NotNull(second);
        }
        finally
        {
            if (second is not null)
                await second.DisposeAsync();
        }
    }

    [Fact]
    public void EngineDescriptor_RoundTripsThroughEngineJson()
    {
        EngineDescriptor descriptor = new(Environment.ProcessId, "/bin/kyberdash", "1.2.3", "2026-01-01T00:00:00.000Z", new Uri("http://127.0.0.1:4747/"));

        repository.WriteEngineDescriptor(directory.Path, descriptor);

        EngineDescriptor? read = repository.ReadEngineDescriptor(directory.Path);
        Assert.Equal(descriptor, read);

        repository.DeleteEngineDescriptor(directory.Path);
        Assert.Null(repository.ReadEngineDescriptor(directory.Path));
        repository.DeleteEngineDescriptor(directory.Path);
    }

    [Fact]
    public void EngineDescriptor_ReadReturnsNullForMalformedFile()
    {
        File.WriteAllText(directory.Combine(ServerStateRepository.EngineDescriptorFile), "{ oops");

        Assert.Null(repository.ReadEngineDescriptor(directory.Path));
    }

    [Fact]
    public void EngineSocketPath_FallsBackToTempWhenTheStatePathIsTooLong()
    {
        string longDirectory = directory.Combine(new string('a', 120));

        string socketPath = ServerStateRepository.EngineSocketPath(longDirectory);

        Assert.StartsWith(Path.GetTempPath(), socketPath, StringComparison.Ordinal);
        Assert.EndsWith(".sock", socketPath, StringComparison.Ordinal);
        Assert.Equal(socketPath, ServerStateRepository.EngineSocketPath(longDirectory));
    }

    [Fact]
    public async Task EngineLease_OwnsTheSocketAndAnswersProbes()
    {
        TaskCompletionSource shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        (IEngineOwnerLease? lease, EngineDescriptor? holder) = await repository.TryAcquireEngineAsync(
            directory.Path,
            "0.21.0",
            "/bin/kyberdash",
            () => shutdownRequested.TrySetResult());

        try
        {
            Assert.NotNull(lease);
            Assert.Null(holder);

            lease!.UpdateUrl(new Uri("http://127.0.0.1:4747/"));

            (EngineProbeState state, EngineDescriptor? descriptor) = await repository.ProbeEngineAsync(directory.Path);
            Assert.Equal(EngineProbeState.Running, state);
            Assert.NotNull(descriptor);
            Assert.Equal(Environment.ProcessId, descriptor!.Pid);
            Assert.Equal("0.21.0", descriptor.Version);
            Assert.Equal(new Uri("http://127.0.0.1:4747/"), descriptor.Url);
            Assert.Equal(descriptor, repository.ReadEngineDescriptor(directory.Path));

            Assert.True(await repository.RequestEngineShutdownAsync(directory.Path));
            await shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }

        Assert.Equal(EngineProbeState.NotRunning, (await repository.ProbeEngineAsync(directory.Path)).State);
        Assert.False(await repository.RequestEngineShutdownAsync(directory.Path));
    }

    [Fact]
    public async Task EngineLease_ReportsTheExistingHolderToASecondProcess()
    {
        (IEngineOwnerLease? lease, _) = await repository.TryAcquireEngineAsync(
            directory.Path,
            "0.21.0",
            "/bin/kyberdash",
            static () => { });

        try
        {
            Assert.NotNull(lease);

            (IEngineOwnerLease? second, EngineDescriptor? holder) = await repository.TryAcquireEngineAsync(
                directory.Path,
                "0.21.0",
                "/bin/kyberdash",
                static () => { });

            Assert.Null(second);
            Assert.NotNull(holder);
            Assert.Equal(Environment.ProcessId, holder!.Pid);
            Assert.Equal("0.21.0", holder.Version);
            Assert.Equal(holder, repository.ReadEngineDescriptor(directory.Path));
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task EngineLease_ProbeReportsNotRunningWithoutASocket()
    {
        (EngineProbeState state, EngineDescriptor? descriptor) = await repository.ProbeEngineAsync(directory.Combine("missing-state"));

        Assert.Equal(EngineProbeState.NotRunning, state);
        Assert.Null(descriptor);
        Assert.False(await repository.RequestEngineShutdownAsync(directory.Combine("missing-state")));
    }

    [Fact]
    public async Task EngineLease_ReplacesAStaleSocketFile()
    {
        await File.WriteAllTextAsync(ServerStateRepository.EngineSocketPath(directory.Path), "stale");

        (IEngineOwnerLease? lease, EngineDescriptor? holder) = await repository.TryAcquireEngineAsync(
            directory.Path,
            "0.21.0",
            "/bin/kyberdash",
            static () => { });

        try
        {
            Assert.Null(holder);
            Assert.NotNull(lease);
        }
        finally
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }
    }
}
