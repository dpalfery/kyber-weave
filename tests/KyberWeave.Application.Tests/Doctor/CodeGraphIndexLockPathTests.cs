using KyberWeave.Application.Doctor;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Which lock file a provisioning run takes, over a fake path resolver and a fake platform, with no disk.</summary>
public sealed class CodeGraphIndexLockPathTests
{
    private readonly InMemoryFileSystem _files = new();

    private string Work => Path.Combine(_files.Root, "kw", "work");

    private string LockDirectory => Path.Combine(_files.Root, "kw", "locks");

    [Fact]
    public async Task Provision_TakesTheSameLockForARelativeRootAsForItsFullPath()
    {
        string relative = await LockTakenFor("repo", currentDirectory: Work);
        string full = await LockTakenFor(Path.Combine(Work, "repo"), currentDirectory: Path.Combine(_files.Root, "elsewhere"));

        Assert.Equal(full, relative);
        Assert.StartsWith(LockDirectory, relative, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provision_TakesDifferentLocksForDifferentRoots()
    {
        Assert.NotEqual(
            await LockTakenFor(Path.Combine(Work, "one"), currentDirectory: Work),
            await LockTakenFor(Path.Combine(Work, "two"), currentDirectory: Work));
    }

    private async Task<string> LockTakenFor(string root, string currentDirectory)
    {
        RecordingWorkspace workspace = new();
        CodeGraphIndexProvisioner provisioner = new(
            new FakeDoctorFileSystem(),
            workspace,
            FakePathResolver.OverEmptyFileSystem(),
            new FakeProcessEnvironment(currentDirectory: currentDirectory),
            TimeProvider.System);

        await provisioner.ProvisionAsync(
            root,
            DoctorOptions.Defaults,
            new ScriptedProcessExecutor(_ => throw new InvalidOperationException("The lock was refused, so no child runs.")),
            LockDirectory,
            TimeSpan.Zero);

        return Assert.Single(workspace.Paths);
    }

    /// <summary>Records every lock path asked for and refuses it, so a run ends before it reaches the disk.</summary>
    private sealed class RecordingWorkspace : ICodeGraphIndexWorkspace
    {
        public List<string> Paths { get; } = [];

        public Task<CodeGraphIndexLockAttempt> TryAcquireLockAsync(
            string path,
            TimeSpan wait,
            CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            return Task.FromResult(new CodeGraphIndexLockAttempt(null, "refused for the test"));
        }

        public bool IsWritable(string directory) => false;

        public bool IsCodeGraphLockHeld(string indexDirectory) => false;
    }
}
