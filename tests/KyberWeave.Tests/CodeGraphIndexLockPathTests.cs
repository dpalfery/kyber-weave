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
    private const string LockDirectory = "/kw/locks";

    [Fact]
    public void Provision_TakesTheSameLockForARelativeRootAsForItsFullPath()
    {
        string relative = LockTakenFor("repo", currentDirectory: "/kw/work");
        string full = LockTakenFor("/kw/work/repo", currentDirectory: "/elsewhere");

        Assert.Equal(full, relative);
        Assert.StartsWith(LockDirectory, relative, StringComparison.Ordinal);
    }

    [Fact]
    public void Provision_TakesDifferentLocksForDifferentRoots()
    {
        Assert.NotEqual(
            LockTakenFor("/kw/work/one", currentDirectory: "/kw/work"),
            LockTakenFor("/kw/work/two", currentDirectory: "/kw/work"));
    }

    private static string LockTakenFor(string root, string currentDirectory)
    {
        RecordingWorkspace workspace = new();
        CodeGraphIndexProvisioner provisioner = new(
            new FakeDoctorFileSystem(),
            workspace,
            FakePathResolver.OverEmptyFileSystem(),
            new FakeProcessEnvironment(currentDirectory: currentDirectory),
            TimeProvider.System);

        provisioner.Provision(
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

        public IDisposable? TryAcquireLock(string path, TimeSpan wait, out string? failure)
        {
            Paths.Add(path);
            failure = "refused for the test";
            return null;
        }

        public bool IsWritable(string directory) => false;

        public bool IsCodeGraphLockHeld(string indexDirectory) => false;
    }
}
