using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using KyberWeave.Application.Arbiter;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The uninstaller over the shared path, file-system, environment and state fakes: the root it works in is the one the
/// test's working directory names, and the files it removes are the ones in memory.
/// </summary>
public sealed class ArbiterUninstallerSeamsTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string Home = "/kw/home";
    private const string ProjectRoot = "/kw/work/repo";
    private const string OwnedFile = ".kilo/plugin/kyber-arbiter.ts";
    private const string StateDirectory = "/kw/state";
    private const string Digest = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory, homeDirectory: Home);

    public ArbiterUninstallerSeamsTests()
    {
        _files.CreateDirectory(WorkingDirectory);
        _files.CreateDirectory(Home);
        _files.CreateDirectory("/kw/work/repo/.kilo/plugin");
        _files.CreateDirectory(StateDirectory);
        _paths = new FakePathResolver(_files);
    }

    /// <summary>
    /// A relative root is resolved against the working directory the environment seam names, so the file the target
    /// owns is found and removed. Before the class took the path seam it resolved the root against the process's own
    /// working directory, and a test could only prove this by starting in the directory it wanted.
    /// </summary>
    [Fact]
    public void Uninstall_RemovesTheOwnedFileUnderTheRootResolvedFromTheInjectedWorkingDirectory()
    {
        _files.WriteAllText(ProjectRoot + "/" + OwnedFile, "// kyber-arbiter");

        Uninstaller(Store()).Uninstall("repo", SquadDeploymentScope.Project, "kilo");

        Assert.False(_files.FileExists(ProjectRoot + "/" + OwnedFile), "The target's owned file was left behind.");
    }

    /// <summary>
    /// A receipt that owns the file is honoured, and a receipt left with nothing of this target is removed with it,
    /// through the state directory the store answers.
    /// </summary>
    [Fact]
    public void Uninstall_DeletesTheReceiptWhenNoOtherTargetRemains()
    {
        _files.WriteAllText(ProjectRoot + "/" + OwnedFile, "// kyber-arbiter");
        _files.WriteAllText(StateDirectory + "/receipt.json", "{}");
        FakeSquadStateStore store = new(new FakeSquadUserPaths(Home), _files);
        store.AddStateDirectory(ProjectRoot, SquadDeploymentScope.Project, StateDirectory);
        store.Add(new SquadReceipt(
            "kyber-squad.receipt/v3",
            SquadDeploymentScope.Project,
            ProjectRoot,
            DateTimeOffset.UnixEpoch,
            [],
            [new SquadOwnedFile(OwnedFile, Digest, "kilo", Adopted: false)]));

        Uninstaller(store).Uninstall(ProjectRoot, SquadDeploymentScope.Project, "kilo");

        Assert.False(_files.FileExists(ProjectRoot + "/" + OwnedFile), "The target's owned file was left behind.");
        Assert.False(_files.FileExists(StateDirectory + "/receipt.json"), "The receipt with nothing left was kept.");
        Assert.Null(store.ReadReceipt(ProjectRoot, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// A registered project spelled by a relative path is the project being asked about, so a receipt that holds only
    /// this target's files does not read as another target's deployment.
    /// </summary>
    [Fact]
    public void AnotherTargetIsDeployed_TreatsTheRegisteredProjectAsCurrent_WhateverItsSpelling()
    {
        FakeSquadStateStore store = Store();
        store.AddProject(new SquadProjectRegistryEntry(ProjectRoot, ProjectRoot, DateTimeOffset.UnixEpoch, [], Stale: false, StaleReason: null));
        store.Add(new SquadReceipt(
            "kyber-squad.receipt/v3",
            SquadDeploymentScope.Project,
            ProjectRoot,
            DateTimeOffset.UnixEpoch,
            [],
            [new SquadOwnedFile(OwnedFile, Digest, "kilo", Adopted: false)]));

        bool another = Uninstaller(store).AnotherTargetIsDeployed("repo", SquadTarget.Kilo);

        Assert.False(another, "The project's own Kilo files were read as another target's deployment.");
    }

    /// <summary>
    /// On a volume that keeps names apart, <c>/repo</c> and <c>/REPO</c> are two projects: the one under <c>/REPO</c>
    /// must not be read as the registered project, or another target's deployment goes unseen.
    /// </summary>
    [Fact]
    public void AnotherTargetIsDeployed_TreatsADifferentlyCasedRootAsAnotherProject_OnACaseSensitiveVolume()
    {
        InMemoryFileSystem files = new(caseSensitive: true);
        files.CreateDirectory("/repo");
        FakeSquadStateStore store = Store();
        store.AddProject(new SquadProjectRegistryEntry("/repo", "/repo", DateTimeOffset.UnixEpoch, [], Stale: false, StaleReason: null));
        store.Add(new SquadReceipt(
            "kyber-squad.receipt/v3",
            SquadDeploymentScope.Project,
            "/repo",
            DateTimeOffset.UnixEpoch,
            [],
            [new SquadOwnedFile(OwnedFile, Digest, "kilo", Adopted: false)]));
        ArbiterUninstaller uninstaller = new(
            store,
            new FakeSquadGlobalRootResolver(Home),
            files,
            new NoGlobalTargets(),
            _environment,
            new FakePathResolver(files));

        bool another = uninstaller.AnotherTargetIsDeployed("/REPO", SquadTarget.Kilo);

        Assert.True(another, "A different project's Kilo files were read as the current project's own.");
    }

    /// <summary>
    /// The project's own receipt is read at the root the environment seam resolves, so a project the registry does not
    /// list still reports another target's deployment when its receipt names one.
    /// </summary>
    [Fact]
    public void AnotherTargetIsDeployed_ReadsTheCurrentProjectsReceiptThroughTheResolvedRoot()
    {
        FakeSquadStateStore store = Store();
        store.Add(new SquadReceipt(
            "kyber-squad.receipt/v3",
            SquadDeploymentScope.Project,
            ProjectRoot,
            DateTimeOffset.UnixEpoch,
            [],
            [new SquadOwnedFile(OwnedFile, Digest, "claude", Adopted: false)]));

        bool another = Uninstaller(store).AnotherTargetIsDeployed("repo", SquadTarget.Kilo);

        Assert.True(another, "The project's own receipt names another target and was not read.");
    }

    /// <summary>
    /// The state fake reads a receipt the way the store does: once the receipt file is gone the receipt is gone, so a
    /// test that removes a deployment cannot be answered from memory afterwards.
    /// </summary>
    [Fact]
    public void ReadReceipt_AnswersNoReceiptOnceTheReceiptFileIsGone()
    {
        FakeSquadStateStore store = new(new FakeSquadUserPaths(Home), _files);
        store.AddStateDirectory(ProjectRoot, SquadDeploymentScope.Project, StateDirectory);
        store.Add(new SquadReceipt(
            "kyber-squad.receipt/v3",
            SquadDeploymentScope.Project,
            ProjectRoot,
            DateTimeOffset.UnixEpoch,
            [],
            []));
        _files.WriteAllText(StateDirectory + "/receipt.json", "{}");
        Assert.NotNull(store.ReadReceipt(ProjectRoot, SquadDeploymentScope.Project));

        _files.DeleteFile(StateDirectory + "/receipt.json");

        Assert.Null(store.ReadReceipt(ProjectRoot, SquadDeploymentScope.Project));
    }

    private ArbiterUninstaller Uninstaller(FakeSquadStateStore store) =>
        new(store, new FakeSquadGlobalRootResolver(Home), _files, new NoGlobalTargets(), _environment, _paths);

    private static FakeSquadStateStore Store() => new(new FakeSquadUserPaths(Home));

    /// <summary>The user configuration lists no global target, so only the state store decides what is deployed.</summary>
    private sealed class NoGlobalTargets : IUserHostConfigStore
    {
        public string EnsureUserConfig(string home) => string.Empty;

        public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
        {
        }

        public void RemoveGlobalTarget(string home, string target)
        {
        }

        public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
    }
}
