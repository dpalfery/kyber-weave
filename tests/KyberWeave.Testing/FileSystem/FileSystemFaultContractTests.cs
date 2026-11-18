using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Tests.FileSystem;

/// <summary>The contract a fault-injecting file system must satisfy, run against the in-memory fake.</summary>
/// <remarks>
/// Only the fake can inject faults, so this suite runs in the unit project alone. Its tests pin the
/// two behaviours crash-safety tests rely on: a failure leaves the tree exactly as the previous
/// operation left it, and a kill stops the fake at that operation while keeping the tree for
/// inspection after a simulated restart.
/// </remarks>
public abstract class FileSystemFaultContractTests
{
    /// <summary>Creates the fault-injecting implementation under test.</summary>
    /// <returns>The file system.</returns>
    protected abstract InMemoryFileSystem CreateFileSystem();

    private static string At(string directory, string relative) => Path.Combine(directory, relative);

    [Fact]
    public void FailingAtAnOrdinalStopsThatOperationAndLeavesTheTree()
    {
        InMemoryFileSystem files = CreateFileSystem();
        string root = files.CreateTemporaryDirectory("kw-fs-fault-");
        string kept = At(root, "kept.txt");
        files.WriteAllText(kept, "kept");
        files.FailAt(files.OperationCount + 1);

        _ = Assert.Throws<IOException>(() => files.WriteAllText(At(root, "target.txt"), "never"));

        files.CreateDirectory(At(root, "later"));

        Assert.False(files.FileExists(At(root, "target.txt")));
        Assert.True(files.DirectoryExists(At(root, "later")));
        Assert.Equal("kept", files.ReadAllText(kept));
    }

    [Fact]
    public void AFailureLeavesAnEarlierFileUntouchedAndARetrySucceeds()
    {
        InMemoryFileSystem files = CreateFileSystem();
        string root = files.CreateTemporaryDirectory("kw-fs-fault-");
        string path = At(root, "document.txt");
        files.WriteAllText(path, "old");
        files.FailAt(files.OperationCount + 1);

        _ = Assert.Throws<IOException>(() => files.WriteAllText(path, "new"));

        Assert.Equal("old", files.ReadAllText(path));

        files.ClearFaults();
        files.WriteAllText(path, "new");

        Assert.Equal("new", files.ReadAllText(path));
    }

    [Fact]
    public void AFailureCanThrowTheTypeATestAsksFor()
    {
        InMemoryFileSystem files = CreateFileSystem();
        string root = files.CreateTemporaryDirectory("kw-fs-fault-");
        files.FailAt(files.OperationCount + 1, new UnauthorizedAccessException("denied by the test"));

        _ = Assert.Throws<UnauthorizedAccessException>(() => files.CreateDirectory(At(root, "blocked")));

        Assert.False(files.DirectoryExists(At(root, "blocked")));
    }

    [Fact]
    public void KillingAtAnOrdinalStopsEveryLaterOperationUntilRestarted()
    {
        InMemoryFileSystem files = CreateFileSystem();
        string root = files.CreateTemporaryDirectory("kw-fs-fault-");
        string kept = At(root, "kept.txt");
        files.WriteAllText(kept, "kept");
        string marker = At(root, "marker.txt");
        files.KillAt(files.OperationCount + 1);

        _ = Assert.Throws<InMemoryFileSystemKilledException>(() => files.CreateNewFile(marker));

        Assert.True(files.IsKilled);
        _ = Assert.Throws<InMemoryFileSystemKilledException>(() => files.ReadAllText(kept));
        _ = Assert.Throws<InMemoryFileSystemKilledException>(() => files.PathExists(root));

        files.ClearFaults();

        Assert.False(files.IsKilled);
        Assert.False(files.FileExists(marker));
        Assert.Equal("kept", files.ReadAllText(kept));
    }

    [Fact]
    public void APredicateFailsOnlyTheOperationsItMatches()
    {
        InMemoryFileSystem files = CreateFileSystem();
        string root = files.CreateTemporaryDirectory("kw-fs-fault-");
        string blocked = At(root, "blocked.txt");
        files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.WriteAllText) && operation.Path == blocked,
            new IOException("blocked by the test"));

        _ = Assert.Throws<IOException>(() => files.WriteAllText(blocked, "never"));
        files.WriteAllText(At(root, "allowed.txt"), "written");

        Assert.False(files.FileExists(blocked));
        Assert.Equal("written", files.ReadAllText(At(root, "allowed.txt")));
    }

    [Fact]
    public void APredicateCanKillAtOneNamedOperation()
    {
        InMemoryFileSystem files = CreateFileSystem();
        string root = files.CreateTemporaryDirectory("kw-fs-fault-");
        string marker = At(root, "marker.txt");
        files.KillWhen(operation => operation.Name == nameof(InMemoryFileSystem.CreateNewFile) && operation.Path == marker);

        _ = Assert.Throws<InMemoryFileSystemKilledException>(() => files.CreateNewFile(marker));

        files.ClearFaults();
        Assert.False(files.FileExists(marker));
    }

    [Fact]
    public void OperationCountCountsEverySeamCall()
    {
        InMemoryFileSystem files = CreateFileSystem();

        Assert.Equal(0, files.OperationCount);

        _ = files.PathExists("/absent");
        string root = files.CreateTemporaryDirectory("kw-fs-fault-");
        files.WriteAllText(At(root, "counted.txt"), "x");

        Assert.Equal(3, files.OperationCount);
    }
}
