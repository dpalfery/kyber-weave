using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins what the test temp root does to the real process and disk: the module initializer moved
///     <c>TMPDIR</c>, and the next run removes the folders of runs whose process is gone.
/// </summary>
/// <remarks>
///     <see cref="TestTempRoot" /> is the adapter that redirects the process, so these cases need the real
///     environment and real folders. Its pure rules are tested in the unit project.
/// </remarks>
public sealed class TestTempRootRealDiskTests
{
    private const string Key = "0123456789abcdef";

    /// <summary>
    ///     The running process actually moved its temp directory to the per-checkout root; this is
    ///     what proves the module initializer ran, not just that the pure rule is right.
    /// </summary>
    [Fact]
    public void TheRunningProcessTempDirectoryIsItsOwnRoot()
    {
        string tmpdir = Environment.GetEnvironmentVariable("TMPDIR")!;

        Assert.EndsWith(
            $"/kw-{TestTempRoot.CheckoutKey()}/{Environment.ProcessId}",
            tmpdir,
            StringComparison.Ordinal);

        if (OperatingSystem.IsMacOS() &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TestTempRoot.OverrideVariable)))
        {
            Assert.StartsWith("/var/folders/", tmpdir, StringComparison.Ordinal);
            Assert.Contains("/kw-test-temp/kw-", tmpdir, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A killed run leaves its PID folder behind; the next run removes only those folders
    ///     whose process is gone, and never its own or a concurrent run's.
    /// </summary>
    [Fact]
    public void DeadSiblingFoldersAreRemovedAndLiveOnesAreKept()
    {
        using TempDirectory temp = new();
        string parent = Path.Combine(temp.Path, "kw-" + Key);
        string live = Path.Combine(parent, "111");
        string dead = Path.Combine(parent, "222");
        string own = Path.Combine(parent, "999");
        string notAProcess = Path.Combine(parent, "notes");

        Directory.CreateDirectory(live);
        Directory.CreateDirectory(dead);
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(notAProcess);

        TestTempRoot.RemoveDeadSiblings(parent, ownProcessId: 999, processId => processId == 111);

        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(own));
        Assert.True(Directory.Exists(notAProcess));
    }

    /// <summary>Cleanup runs at assembly load, before any test creates the parent: no parent is fine.</summary>
    [Fact]
    public void DeadSiblingCleanupAcceptsAMissingParent()
    {
        using TempDirectory temp = new();

        TestTempRoot.RemoveDeadSiblings(
            Path.Combine(temp.Path, "kw-" + Key),
            ownProcessId: 999,
            _ => true);
    }

    /// <summary>
    /// A sibling folder that is only seconds old may belong to a concurrent run whose PID has just been
    /// recycled, so a minimum age spares it while a folder older than that threshold is still removed.
    /// </summary>
    [Fact]
    public void DeadSiblingCleanupRespectsMinimumAge()
    {
        using TempDirectory temp = new();
        string parent = Path.Combine(temp.Path, "kw-" + Key);
        string recent = Path.Combine(parent, "111");
        string stale = Path.Combine(parent, "222");
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-15));

        TestTempRoot.RemoveDeadSiblings(parent, ownProcessId: 999, _ => false, minAge: TimeSpan.FromMinutes(5));

        Assert.True(Directory.Exists(recent));
        Assert.False(Directory.Exists(stale));
    }
}
