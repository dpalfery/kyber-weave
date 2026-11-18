using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The deployment planner over an in-memory file system: the writes, deletions and preconditions it plans for
/// install, update and uninstall, and the collisions it refuses. No test here touches a disk.
/// </summary>
public sealed class SquadDeploymentPlannerTests
{
    private const string ConductorPath = ".codex/agents/conductor.toml";
    private const string SecondPath = ".codex/agents/second.toml";
    private const string CodexHookPath = ".codex/hooks.json";

    private readonly InMemorySquadState _state = new();

    private SquadDeploymentPlanner Planner =>
        new(_state.Files, _state.Semantics, _state.Paths, _state.Environment);

    [Fact]
    public void CreateInstallPlansAWriteForAMissingFileAndRecordsItsDigest()
    {
        string root = _state.CreateRoot("install");

        SquadDeploymentPlan plan = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(ConductorPath, "installed body")],
            [],
            adopt: false,
            _state.Time);

        SquadFileMutation mutation = Assert.Single(plan.FileMutations);
        Assert.Equal(SquadFileMutationKind.Write, mutation.Kind);
        Assert.Equal(ConductorPath, mutation.RelativePath);
        Assert.Equal(SquadFilePreconditionKind.Missing, Assert.Single(plan.FilePreconditions).Kind);
        SquadOwnedFile owned = Assert.Single(plan.Receipt.Files);
        Assert.Equal(Digest("installed body"), owned.Sha256);
        Assert.False(owned.Adopted);
    }

    [Fact]
    public void CreateInstallRefusesAFileAtARenderedPathWhoseBytesDiffer()
    {
        string root = _state.CreateRoot("install-collision");
        Write(root, ConductorPath, "operator wrote this");

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Planner.CreateInstall(
                root,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(ConductorPath, "installed body")],
                [],
                adopt: false,
                _state.Time));

        Assert.Contains(ConductorPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInstallAdoptsAnIdenticalExistingFileWithoutPlanningToWriteIt()
    {
        string root = _state.CreateRoot("install-adopt");
        Write(root, ConductorPath, "installed body");

        SquadDeploymentPlan plan = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(ConductorPath, "installed body")],
            [],
            adopt: true,
            _state.Time);

        Assert.Empty(plan.FileMutations);
        Assert.Equal(SquadFilePreconditionKind.ExactFile, Assert.Single(plan.FilePreconditions).Kind);
        Assert.True(Assert.Single(plan.Receipt.Files).Adopted);
    }

    [Fact]
    public void CreateUpdateKeepsALocallyEditedManagedFileWhenItIsNotReplacingManagedFiles()
    {
        string root = _state.CreateRoot("update-keep");
        Write(root, ConductorPath, "operator edit");

        SquadDeploymentPlan plan = Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.5"),
            [Rendered(ConductorPath, "release body")],
            Receipt((ConductorPath, "installed body", false)),
            [],
            replaceManaged: false,
            _state.Time);

        Assert.Empty(plan.FileMutations);
        Assert.Equal(Digest("installed body"), Assert.Single(plan.Receipt.Files).Sha256);
    }

    [Fact]
    public void CreateUpdateReplacesALocallyEditedManagedFileWhenItIsAskedTo()
    {
        string root = _state.CreateRoot("update-replace");
        Write(root, ConductorPath, "operator edit");

        SquadDeploymentPlan plan = Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.5"),
            [Rendered(ConductorPath, "release body")],
            Receipt((ConductorPath, "installed body", false)),
            [],
            replaceManaged: true,
            _state.Time);

        SquadFileMutation mutation = Assert.Single(plan.FileMutations);
        Assert.Equal(SquadFileMutationKind.Write, mutation.Kind);
        Assert.Equal(Digest("release body"), Assert.Single(plan.Receipt.Files).Sha256);
    }

    [Fact]
    public void CreateUninstallDeletesAnUneditedOwnedFileAndRetainsAnEditedOne()
    {
        string root = _state.CreateRoot("uninstall");
        Write(root, ConductorPath, "installed body");
        Write(root, SecondPath, "operator edit");

        SquadDeploymentPlan plan = Planner.CreateUninstall(
            root,
            SquadDeploymentScope.Project,
            Receipt((ConductorPath, "installed body", false), (SecondPath, "installed second", false)));

        SquadFileMutation mutation = Assert.Single(plan.FileMutations);
        Assert.Equal(SquadFileMutationKind.Delete, mutation.Kind);
        Assert.Equal(ConductorPath, mutation.RelativePath);
        Assert.Equal(SecondPath, Assert.Single(plan.Receipt.Files).RelativePath);
    }

    [Fact]
    public void CreateUninstallRetainsAnOwnedPathThatBecameADirectory()
    {
        string root = _state.CreateRoot("uninstall-directory");
        _state.Files.CreateDirectory(Path.Combine(root, ConductorPath));

        SquadDeploymentPlan plan = Planner.CreateUninstall(
            root,
            SquadDeploymentScope.Project,
            Receipt((ConductorPath, "installed body", false)));

        Assert.Empty(plan.FileMutations);
        Assert.Equal(ConductorPath, Assert.Single(plan.Receipt.Files).RelativePath);
    }

    [Fact]
    public void CollectUnmanagedCollisionsReportsOnlyTheForeignBytesAndSkipsReceiptOwnedPaths()
    {
        string root = _state.CreateRoot("collisions");
        Write(root, ConductorPath, "operator wrote this");
        Write(root, SecondPath, "release second");
        Write(root, ".codex/agents/owned.toml", "operator edit");

        IReadOnlyList<SquadUnmanagedPathCollision> collisions = Planner.CollectUnmanagedCollisions(
            root,
            SquadDeploymentScope.Project,
            [
                Rendered(ConductorPath, "release body"),
                Rendered(SecondPath, "release second"),
                Rendered(".codex/agents/owned.toml", "release owned")
            ],
            globalRoots: null,
            Receipt((".codex/agents/owned.toml", "installed owned", false)));

        SquadUnmanagedPathCollision collision = Assert.Single(collisions);
        Assert.Equal(ConductorPath, collision.RelativePath);
    }

    [Fact]
    public void CreateInstallSplicesARenderedBlockIntoAHookFileSquadCreates()
    {
        string root = _state.CreateRoot("block-install");

        SquadDeploymentPlan plan = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [],
            [],
            adopt: false,
            _state.Time,
            blocks: [CodexBlock("one")]);

        SquadFileMutation mutation = Assert.Single(plan.FileMutations);
        Assert.Equal(CodexHookPath, mutation.RelativePath);
        Assert.Contains("probe-one", Encoding.UTF8.GetString(mutation.Content!.Value.Span), StringComparison.Ordinal);
        SquadOwnedBlock owned = Assert.Single(plan.Receipt.Blocks);
        Assert.True(owned.CreatedFile);
        Assert.Single(owned.Entries);
        Assert.Equal(SquadFilePreconditionKind.Missing, Assert.Single(plan.FilePreconditions).Kind);
    }

    [Fact]
    public void CreateInstallSplicesABlockIntoAUserHookFileAndKeepsTheUsersEntries()
    {
        string root = _state.CreateRoot("block-install-user-file");
        Write(
            root,
            CodexHookPath,
            """
            {"hooks":{"PreToolUse":[{"matcher":"operator","hooks":[{"type":"command","command":"operator-hook"}]}]}}
            """);

        SquadDeploymentPlan plan = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [],
            [],
            adopt: false,
            _state.Time,
            blocks: [CodexBlock("one")]);

        SquadFileMutation mutation = Assert.Single(plan.FileMutations);
        string spliced = Encoding.UTF8.GetString(mutation.Content!.Value.Span);
        Assert.Contains("operator-hook", spliced, StringComparison.Ordinal);
        Assert.Contains("probe-one", spliced, StringComparison.Ordinal);
        Assert.False(Assert.Single(plan.Receipt.Blocks).CreatedFile);
        Assert.Equal(SquadFilePreconditionKind.ExactFile, Assert.Single(plan.FilePreconditions).Kind);
    }

    [Fact]
    public void CreateUpdateKeepsADriftedBlockWhenItIsNotReplacingManagedFiles()
    {
        string root = _state.CreateRoot("block-update-keep");
        SquadDeploymentPlan install = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [],
            [],
            adopt: false,
            _state.Time,
            blocks: [CodexBlock("one")]);
        Write(
            root,
            CodexHookPath,
            Encoding.UTF8.GetString(Assert.Single(install.FileMutations).Content!.Value.Span)
                .Replace("probe-one", "operator-edit", StringComparison.Ordinal));

        SquadDeploymentPlan update = Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.5"),
            [],
            install.Receipt,
            [],
            replaceManaged: false,
            _state.Time,
            blocks: [CodexBlock("one")]);

        Assert.NotEmpty(update.BlockDrifts);
        Assert.DoesNotContain(update.FileMutations, mutation => mutation.RelativePath == CodexHookPath);
    }

    [Fact]
    public void CreateUpdateReplacesADriftedBlockWhenItIsAskedTo()
    {
        string root = _state.CreateRoot("block-update-replace");
        SquadDeploymentPlan install = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [],
            [],
            adopt: false,
            _state.Time,
            blocks: [CodexBlock("one")]);
        Write(
            root,
            CodexHookPath,
            Encoding.UTF8.GetString(Assert.Single(install.FileMutations).Content!.Value.Span)
                .Replace("probe-one", "operator-edit", StringComparison.Ordinal));

        SquadDeploymentPlan update = Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.5"),
            [],
            install.Receipt,
            [],
            replaceManaged: true,
            _state.Time,
            blocks: [CodexBlock("one")]);

        Assert.Empty(update.BlockDrifts);
        Assert.Contains(update.FileMutations, mutation => mutation.RelativePath == CodexHookPath);
    }

    [Fact]
    public void CreateUninstallDeletesAHookFileSquadCreatedOnceItsOwnedEntriesAreRemoved()
    {
        string root = _state.CreateRoot("block-uninstall-created");
        SquadDeploymentPlan install = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [],
            [],
            adopt: false,
            _state.Time,
            blocks: [CodexBlock("one")]);
        Write(root, CodexHookPath, Encoding.UTF8.GetString(Assert.Single(install.FileMutations).Content!.Value.Span));

        SquadDeploymentPlan uninstall = Planner.CreateUninstall(root, SquadDeploymentScope.Project, install.Receipt);

        SquadFileMutation mutation = Assert.Single(uninstall.FileMutations);
        Assert.Equal(SquadFileMutationKind.Delete, mutation.Kind);
        Assert.Empty(uninstall.Receipt.Blocks);
    }

    [Fact]
    public void CreateUninstallKeepsAUserHookFileAndRemovesOnlyTheOwnedEntries()
    {
        string root = _state.CreateRoot("block-uninstall-user-file");
        Write(
            root,
            CodexHookPath,
            """
            {"hooks":{"PreToolUse":[{"matcher":"operator","hooks":[{"type":"command","command":"operator-hook"}]}]}}
            """);
        SquadDeploymentPlan install = Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [],
            [],
            adopt: false,
            _state.Time,
            blocks: [CodexBlock("one")]);
        Write(root, CodexHookPath, Encoding.UTF8.GetString(Assert.Single(install.FileMutations).Content!.Value.Span));

        SquadDeploymentPlan uninstall = Planner.CreateUninstall(root, SquadDeploymentScope.Project, install.Receipt);

        SquadFileMutation mutation = Assert.Single(uninstall.FileMutations);
        Assert.Equal(SquadFileMutationKind.Write, mutation.Kind);
        string retained = Encoding.UTF8.GetString(mutation.Content!.Value.Span);
        Assert.Contains("operator-hook", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-one", retained, StringComparison.Ordinal);
        Assert.Empty(uninstall.Receipt.Blocks);
    }

    private void Write(string root, string relativePath, string content)
    {
        CreateDirectory(root, relativePath);
        _state.Files.WriteAllText(Path.Combine(root, relativePath), content);
    }

    private void CreateDirectory(string root, string relativePath)
    {
        string fullPath = Path.Combine(root, relativePath);
        _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    }

    private static SquadRenderedBlock CodexBlock(string marker) =>
        new(
            "codex",
            CodexHookPath,
            SquadHookBlockFormat.Codex,
            [new SquadRenderedBlockEntry("PreToolUse", MatcherGroup("kyber-weave-arbiter hook --harness codex", $"probe-{marker}"))]);

    private static JsonObject MatcherGroup(string command, string matcher) =>
        new()
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5
                })
        };

    private static SquadDeploymentFile Rendered(string relativePath, string content) =>
        new(relativePath, Encoding.UTF8.GetBytes(content), "codex");

    private static SquadReceipt Receipt(params (string RelativePath, string Content, bool Adopted)[] files) =>
        new(
            SquadStateSchemas.ReceiptSchemaV1,
            SquadDeploymentScope.Project,
            ".",
            InMemorySquadState.Start,
            [],
            [.. files.Select(file => new SquadOwnedFile(file.RelativePath, Digest(file.Content), "codex", file.Adopted))]);

    private static SquadLock Lock(string version) =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex"],
            [],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
