using System.Runtime.Versioning;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// One state directory the process may not read must not hide the others, on the real disk: the project registry
/// rebuild and the legacy-root scan skip the unreadable child and carry on with the rest.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class SquadStateUnreadableChildTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly List<string> _locked = [];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (string path in _locked)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _directory.Dispose();
    }

    /// <summary>A project state directory that cannot be read leaves the other projects in the rebuilt listing.</summary>
    [Fact]
    public void AnUnreadableProjectStateDirectoryDoesNotHideTheOtherProjects()
    {
        if (!CanDenyRead())
        {
            Assert.Skip("This platform or user cannot make a directory unreadable.");
        }

        (SquadStateStore store, SquadStateTestLayout layout) = Build();
        string good = Directory.CreateDirectory(Path.Combine(_directory.Path, "projects", "good")).FullName;
        string bad = Directory.CreateDirectory(Path.Combine(_directory.Path, "projects", "bad")).FullName;
        WriteReceipt(store, layout, good);
        WriteReceipt(store, layout, bad);
        Deny(layout.HomeProjectDirectory(bad));

        IReadOnlyList<SquadProjectRegistryEntry> listed = store.ListProjects();

        Assert.Equal([layout.PhysicalOf(good)], listed.Select(entry => entry.Path));
    }

    /// <summary>A legacy root that cannot be listed is not counted and the readable one is still migrated.</summary>
    [Fact]
    public void AnUnreadableLegacyRootDoesNotHideTheReadableOne()
    {
        if (!CanDenyRead())
        {
            Assert.Skip("This platform or user cannot make a directory unreadable.");
        }

        (SquadStateStore store, SquadStateTestLayout layout) = Build();
        SquadStateMigration migration = RealSquadState.Migration(store);
        string good = Directory.CreateDirectory(Path.Combine(_directory.Path, "projects", "good")).FullName;
        string bad = Directory.CreateDirectory(Path.Combine(_directory.Path, "projects", "bad")).FullName;
        layout.WriteLegacyGlobal(good, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock());
        layout.WriteLegacyGlobal(bad, SquadStateTestLayout.PerTargetRootsReceipt(), SquadStateTestLayout.Lock("1.2.4"));
        Deny(layout.LegacyGlobalDirectory(bad));

        SquadMigrationReport report = migration.MigrateGlobal(invokingCwd: null);

        Assert.Single(report.MigratedItems);
        Assert.Empty(report.UnmigratedLegacyRoots);
    }

    private (SquadStateStore Store, SquadStateTestLayout Layout) Build()
    {
        string home = Directory.CreateDirectory(Path.Combine(_directory.Path, "home")).FullName;
        string applicationData = Directory.CreateDirectory(Path.Combine(_directory.Path, "application-data")).FullName;
        FakeSquadUserPaths userPaths = new(applicationData, home);
        SquadStateStore store = RealSquadState.Store(userPaths);
        PhysicalFileSystem disk = new();
        return (store, new SquadStateTestLayout(store, disk, new SquadFileSystemPathSemantics(new PhysicalPathResolver(disk))));
    }

    private static void WriteReceipt(SquadStateStore store, SquadStateTestLayout layout, string root)
    {
        SquadReceipt receipt = new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            layout.PhysicalOf(root),
            SquadStateTestLayout.InstalledAt,
            [],
            [SquadStateTestLayout.OwnedFile(".codex/agents/alpha.toml", "alpha")]);
        layout.WriteText(Path.Combine(layout.HomeProjectDirectory(root), "squad.receipt.json"), store.SerializeReceipt(receipt));
    }

    private void Deny(string directory)
    {
        _locked.Add(directory);
        File.SetUnixFileMode(directory, UnixFileMode.None);
    }

    private bool CanDenyRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        string probe = Directory.CreateDirectory(Path.Combine(_directory.Path, "probe")).FullName;
        Directory.CreateDirectory(Path.Combine(probe, "inner"));
        File.SetUnixFileMode(probe, UnixFileMode.None);
        bool denied = !Directory.Exists(Path.Combine(probe, "inner"));
        File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return denied;
    }
}
