using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the owned-block lifecycle (task 11.2): renderers carry block fragments through the
/// registry, install splices them into shared hook files and claims only the block in a
/// <c>kyber-squad.receipt/v3</c> receipt, update rewrites while preserving hand-edited
/// entries unless <c>--replace-managed</c> is given, and uninstall removes only the owned
/// entries. Receipts without blocks stay byte-identical v1 or v2.
/// </summary>
public sealed class SquadOwnedBlockLifecycleTests
{
    /// <summary>
    /// The bare global hook path four of the five formats write beneath their own root; only the
    /// target tells them apart, which is the subject of the global cases below.
    /// </summary>
    private const string GlobalHooksPath = "hooks.json";

    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    // ----------------------------------------------------------------------------------
    // Rendering: blocks ride the render result and merge across targets
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task RegistryMergesBlocksAcrossTargets()
    {
        using SharedIdentitySquadFixture fixture = SharedIdentitySquadFixture.Create();
        SquadRendererRegistry registry = new(
        [
            new BlockStubRenderer(SquadTarget.Cursor, CursorBlock("cursor-one")),
            new BlockStubRenderer(SquadTarget.Codex, CodexBlock("codex-one")),
        ]);

        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            fixture.Path,
            [SquadTarget.Cursor, SquadTarget.Codex],
            SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotNull(result.Blocks);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(".cursor/hooks.json", result.Blocks[0].RelativePath);
        Assert.Equal(".codex/hooks.json", result.Blocks[1].RelativePath);
    }

    // ----------------------------------------------------------------------------------
    // Install: a block is a spliced write; the receipt claims only the block on v3
    // ----------------------------------------------------------------------------------

    [Fact]
    public void InstallClaimsOnlyTheBlockInAV3Receipt()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);

        new SquadTransaction(store).Execute(plan);

        SquadReceipt? receipt = store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.Empty(receipt.Files);
        SquadOwnedBlock block = Assert.Single(receipt.Blocks);
        Assert.Equal(".cursor/hooks.json", block.RelativePath);
        Assert.Equal("cursor", block.Target);
        Assert.True(block.CreatedFile);
        Assert.NotEmpty(block.Entries);

        string content = Read(fixture.Path, ".cursor/hooks.json");
        Assert.Contains("kyber-weave-arbiter hook --harness cursor", content, StringComparison.Ordinal);

        string json = store.SerializeReceipt(receipt);
        Assert.Contains("\"schema\": \"kyber-squad.receipt/v3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"blocks\"", json, StringComparison.Ordinal);
        SquadReceipt roundTripped = store.DeserializeReceipt(json);
        Assert.Equal(receipt.Blocks.Count, roundTripped.Blocks.Count);
        Assert.Equal(
            receipt.Blocks[0].Entries[0].Sha256,
            roundTripped.Blocks[0].Entries[0].Sha256);
    }

    [Fact]
    public void InstallSplicesIntoExistingUserFileWithoutCollision()
    {
        using TempDirectory fixture = new TempDirectory();
        Write(fixture.Path, ".cursor/hooks.json", """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  { "command": "user-linter --fast", "matcher": "Edit", "timeout": 3, "failClosed": false }
                ],
                "postToolUse": []
              }
            }
            """);

        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);

        // An existing user file at a block path is owned content to splice into, never
        // an UnmanagedCollision: the plan builds where CreateInstall used to throw.
        new SquadTransaction(store).Execute(plan);

        string content = Read(fixture.Path, ".cursor/hooks.json");
        Assert.Contains("user-linter --fast", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor", content, StringComparison.Ordinal);

        SquadReceipt? receipt = store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.False(Assert.Single(receipt.Blocks).CreatedFile);
    }

    [Fact]
    public void InstallUsesExactPreconditionOnExistingFileAndMissingWhenAbsent()
    {
        using TempDirectory fixture = new TempDirectory();
        string existing = """
            {
              "version": 1,
              "hooks": { "preToolUse": [], "postToolUse": [] }
            }
            """;
        Write(fixture.Path, ".cursor/hooks.json", existing);
        string existingDigest = Digest(File.ReadAllBytes(
            Path.Combine(fixture.Path, ".cursor/hooks.json")));

        SquadDeploymentPlan existingPlan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);

        SquadFilePrecondition precondition = Assert.Single(existingPlan.FilePreconditions);
        Assert.Equal(SquadFilePreconditionKind.ExactFile, precondition.Kind);
        Assert.Equal(existingDigest, precondition.Sha256);

        using TempDirectory absent = new TempDirectory();
        SquadDeploymentPlan absentPlan = SquadDeploymentPlan.CreateInstall(
            absent.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);

        SquadFilePrecondition missing = Assert.Single(absentPlan.FilePreconditions);
        Assert.Equal(SquadFilePreconditionKind.Missing, missing.Kind);
    }

    // ----------------------------------------------------------------------------------
    // Receipt v3: written only for project scope with blocks; everything else is stable
    // ----------------------------------------------------------------------------------

    [Fact]
    public void ReceiptsWithoutBlocksStayByteIdenticalV1()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
            [new SquadOwnedFile(".codex/agents/conductor.toml", Digest("conductor"), "codex", false)]);

        string json = store.SerializeReceipt(receipt);

        Assert.DoesNotContain("\"blocks\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schema\": \"kyber-squad.receipt/v1\"", json, StringComparison.Ordinal);
        Assert.Equal(
            $$"""
            {
              "schema": "kyber-squad.receipt/v1",
              "scope": "project",
              "targetRoot": ".",
              "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
              "degradations": [
                {
                  "target": "warp",
                  "subject": "conductor",
                  "code": "role-skill-fallback"
                }
              ],
              "files": [
                {
                  "relativePath": ".codex/agents/conductor.toml",
                  "sha256": "{{Digest("conductor")}}",
                  "target": "codex",
                  "adopted": false
                }
              ]
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
    }

    [Fact]
    public void DeserializeV3ChecksTheExactFieldSet()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        string valid = store.SerializeReceipt(plan.Receipt);

        SquadReceipt receipt = store.DeserializeReceipt(valid);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.Single(receipt.Blocks);

        Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(RemoveJsonProperty(valid, "blocks")));

        SquadReceipt withLayoutReceipt = store.DeserializeReceipt(AddJsonProperty(valid, "layout", "per-target-roots"));
        Assert.Equal(SquadDeploymentScope.Project, withLayoutReceipt.Scope);
        Assert.Equal(SquadReceiptLayout.PerTargetRoots, withLayoutReceipt.Layout);
        Assert.Single(withLayoutReceipt.Blocks);

        string globalScopeJson = AddJsonProperty(
            valid.Replace(
                "\"scope\": \"project\"",
                "\"scope\": \"global\"",
                StringComparison.Ordinal),
            "layout",
            "per-target-roots");
        SquadReceipt globalScopeReceipt = store.DeserializeReceipt(globalScopeJson);
        Assert.Equal(SquadDeploymentScope.Global, globalScopeReceipt.Scope);
        Assert.Equal(SquadReceiptLayout.PerTargetRoots, globalScopeReceipt.Layout);
        Assert.Single(globalScopeReceipt.Blocks);
    }

    [Fact]
    public void OlderReaderRefusesV3()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        string v3 = store.SerializeReceipt(plan.Receipt);

        // The pre-v3 reader knows only the v1 and v2 schemas with their exact field sets,
        // so a v3 receipt fails its shape check with InvalidDataException — which the squad
        // commands already map to exit 1 (see Uninstall_WhenCorruptReceipt_ExitsOne).
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => OldDeserialize(v3));
        Assert.Contains("v3", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------------------------
    // Update: rewrites the block, preserves drift unless --replace-managed
    // ----------------------------------------------------------------------------------

    [Fact]
    public void UpdateRewritesTheBlock()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan install = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(install);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));

        SquadDeploymentPlan update = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            previous,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddHours(1)),
            blocks: [CursorBlock("cursor-two")]);
        new SquadTransaction(store).Execute(update);

        string content = Read(fixture.Path, ".cursor/hooks.json");
        Assert.Contains("cursor-two", content, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor-one", content, StringComparison.Ordinal);

        SquadReceipt? receipt = store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.Single(receipt.Blocks);
    }

    [Fact]
    public void UpdatePreservesDriftedEntryUnlessReplaceManagedIsGiven()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan install = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(install);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));

        DriftOwnedEntry(fixture.Path, ".cursor/hooks.json", "cursor-one", "cursor-one drifted by hand");

        SquadDeploymentPlan preserved = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            previous,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddHours(1)),
            blocks: [CursorBlock("cursor-two")]);

        SquadOwnedBlockDrift drift = Assert.Single(preserved.BlockDrifts);
        Assert.Equal(".cursor/hooks.json", drift.RelativePath);
        new SquadTransaction(store).Execute(preserved);
        Assert.Contains("cursor-one drifted by hand", Read(fixture.Path, ".cursor/hooks.json"), StringComparison.Ordinal);

        SquadDeploymentPlan replaced = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            Assert.IsType<SquadReceipt>(store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project)),
            [],
            replaceManaged: true,
            new FixedTimeProvider(InstalledAt.AddHours(2)),
            blocks: [CursorBlock("cursor-two")]);
        Assert.Empty(replaced.BlockDrifts);
        new SquadTransaction(store).Execute(replaced);
        string content = Read(fixture.Path, ".cursor/hooks.json");
        Assert.Contains("cursor-two", content, StringComparison.Ordinal);
        Assert.DoesNotContain("drifted by hand", content, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------
    // Uninstall: removes only the owned entries
    // ----------------------------------------------------------------------------------

    [Fact]
    public void UninstallRemovesOnlyTheOwnedEntries()
    {
        using TempDirectory fixture = new TempDirectory();
        Write(fixture.Path, ".cursor/hooks.json", """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  { "command": "user-linter --fast", "matcher": "Edit", "timeout": 3, "failClosed": false }
                ],
                "postToolUse": []
              }
            }
            """);
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan install = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(install);
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));

        SquadDeploymentPlan uninstall = SquadDeploymentPlan.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);
        new SquadTransaction(store).Execute(uninstall);

        // The user's file survives with only their entries: Squad created nothing here.
        string content = Read(fixture.Path, ".cursor/hooks.json");
        Assert.Contains("user-linter --fast", content, StringComparison.Ordinal);
        Assert.DoesNotContain("kyber-weave-arbiter", content, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallDeletesTheFileOnlyWhenSquadCreatedItAndNoHookRemains()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan install = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        new SquadTransaction(store).Execute(install);
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));
        Assert.True(Assert.Single(receipt.Blocks).CreatedFile);

        SquadDeploymentPlan uninstall = SquadDeploymentPlan.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);
        new SquadTransaction(store).Execute(uninstall);

        Assert.False(File.Exists(Path.Combine(fixture.Path, ".cursor/hooks.json")));
        Assert.Null(store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// A global receipt that loses its last block must drop back to v2, the schema the
    /// writer emits for a block-less global receipt. Carrying the recorded v3 over leaves
    /// a block-less receipt on a schema that requires at least one block.
    /// </summary>
    [Fact]
    public void UninstallGlobalRemovesLastBlockAndDropsRetainedSchemaToV2()
    {
        using TempDirectory fixture = new TempDirectory();
        GlobalBlockDeployment installed = InstallGlobalBlocks(fixture, SquadTarget.Cursor);
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            installed.Store.ReadReceipt(installed.Home, SquadDeploymentScope.Global));
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.Single(receipt.Blocks);
        string hooksFile = GlobalHookFile(installed.Roots, SquadTarget.Cursor);
        Assert.True(File.Exists(hooksFile));

        SquadDeploymentPlan uninstall = SquadDeploymentPlan.CreateUninstall(
            installed.Home,
            SquadDeploymentScope.Global,
            receipt,
            installed.Roots);
        new SquadTransaction(installed.Store).Execute(uninstall);

        SquadReceipt? retained = installed.Store.ReadReceipt(installed.Home, SquadDeploymentScope.Global);
        Assert.Null(retained);
    }

    // ----------------------------------------------------------------------------------
    // Global scope: each block lives beneath its own target's root, and the receipt says so
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// A global update rewrites every block it owns, each in its own target's hook file, and the
    /// receipt it writes records the entries it just wrote — so a later status finds neither block
    /// drifting. Before a global block's identity was qualified by its target, a receipt holding
    /// two <c>hooks.json</c> blocks could not exist to be updated.
    /// </summary>
    [Fact]
    public void UpdateGlobalRewritesEveryBlockWithoutDrift()
    {
        using TempDirectory fixture = new TempDirectory();
        GlobalBlockDeployment installed = InstallGlobalBlocks(fixture);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            installed.Store.ReadReceipt(installed.Home, SquadDeploymentScope.Global));

        SquadDeploymentPlan update = SquadDeploymentPlan.CreateUpdate(
            installed.Home,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            previous,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddHours(1)),
            globalRoots: installed.Roots,
            blocks:
            [
                GlobalHooksBlock(SquadTarget.Cursor, "cursor-two"),
                GlobalHooksBlock(SquadTarget.Codex, "codex-two"),
            ]);

        Assert.Empty(update.BlockDrifts);
        new SquadTransaction(installed.Store).Execute(update);

        SquadReceipt after = Assert.IsType<SquadReceipt>(
            installed.Store.ReadReceipt(installed.Home, SquadDeploymentScope.Global));
        Assert.Equal(["cursor", "codex"], after.Blocks.Select(block => block.Target).ToArray());
        Assert.All(after.Blocks, block => Assert.Equal(GlobalHooksPath, block.RelativePath));
        AssertNoDrift(after, installed.Roots);

        string cursorHookFile = File.ReadAllText(GlobalHookFile(installed.Roots, SquadTarget.Cursor));
        Assert.Contains("probe-cursor-two", cursorHookFile, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-cursor-one", cursorHookFile, StringComparison.Ordinal);
        Assert.Contains(
            "probe-codex-two",
            File.ReadAllText(GlobalHookFile(installed.Roots, SquadTarget.Codex)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A global receipt the shipped 0.1.8-rc.20 binary wrote keeps both of its blocks through an
    /// update. rc.20 could only ever record one block per home, because writing the second is what
    /// the defect refused; the fixture merges two genuine one-block receipts, and an update must
    /// neither refuse it nor quietly drop one of them.
    /// </summary>
    [Fact]
    public void UpdateFromAnRc20GlobalReceiptKeepsBothBlocks()
    {
        using TempDirectory fixture = new TempDirectory();
        FakeSquadUserPaths userPaths = new(fixture.Path);
        string home = userPaths.HomeDirectory;
        Directory.CreateDirectory(home);
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore store = new(userPaths);
        string receiptPath = store.ResolveReceiptPath(home, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, Rc20GlobalReceiptJson());

        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal(["cursor", "codex"], previous.Blocks.Select(block => block.Target).ToArray());

        SquadDeploymentPlan update = SquadDeploymentPlan.CreateUpdate(
            home,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            previous,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddHours(1)),
            globalRoots: globalRoots,
            blocks: [GlobalHooksBlock(SquadTarget.Cursor), GlobalHooksBlock(SquadTarget.Codex)]);

        new SquadTransaction(store).Execute(update);

        SquadReceipt after = Assert.IsType<SquadReceipt>(store.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal("kyber-squad.receipt/v3", after.Schema);
        Assert.Equal(SquadDeploymentScope.Global, after.Scope);
        Assert.Equal(["cursor", "codex"], after.Blocks.Select(block => block.Target).ToArray());
        Assert.All(after.Blocks, block => Assert.Equal(GlobalHooksPath, block.RelativePath));
        AssertNoDrift(after, globalRoots);
    }

    // ----------------------------------------------------------------------------------
    // Duplicate refusals (D6): a receipt is an ownership record, never a list
    // ----------------------------------------------------------------------------------

    /// <summary>
    /// Qualifying a global block's identity by its target widens which blocks may share a receipt;
    /// it does not make a duplicate legal. The same target claiming the same path twice is still
    /// refused, because there is exactly one physical file it could name.
    /// </summary>
    [Fact]
    public void ReceiptRefusesOneTargetClaimingOnePathTwiceAtGlobalScope()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string valid = store.SerializeReceipt(GlobalTwoBlockReceipt());

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => store.DeserializeReceipt(DuplicateBlockForTarget(valid, "cursor", "cursor")));

        Assert.Contains(
            "Squad receipt block path 'hooks.json' for target 'cursor' is not a unique portable deployment path.",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The project-scope counterpart: at one deployment root the path alone is a block's identity,
    /// so one target claiming it twice is refused there too.
    /// </summary>
    [Fact]
    public void ReceiptRefusesOneTargetClaimingOnePathTwiceAtProjectScope()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string valid = store.SerializeReceipt(ProjectCursorBlockReceipt());

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => store.DeserializeReceipt(DuplicateBlockForTarget(valid, "cursor", "cursor")));

        Assert.Contains(
            "Squad receipt block path '.cursor/hooks.json' for target 'cursor' is not a unique portable deployment path.",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Two targets claiming one path at project scope name one physical file, so the receipt is
    /// refused. This is the guarantee the target qualification must not erode: it applies to global
    /// scope alone, where each target writes beneath its own root.
    /// </summary>
    [Fact]
    public void ReceiptRefusesTwoTargetsClaimingOnePathAtProjectScope()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string valid = store.SerializeReceipt(ProjectCursorBlockReceipt());

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => store.DeserializeReceipt(DuplicateBlockForTarget(valid, "cursor", "codex")));

        Assert.Contains(
            "Squad receipt block path '.cursor/hooks.json' for target 'codex' is not a unique portable deployment path.",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One block owns one entry per container. Two entries claiming the same container would record
    /// two digests for one slot, so the receipt is refused rather than leaving a reader to guess
    /// which one is live.
    /// </summary>
    [Fact]
    public void ReceiptRefusesOneBlockClaimingTheSameContainerTwice()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string valid = store.SerializeReceipt(ProjectCursorBlockReceipt());

        InvalidDataException refused = Assert.Throws<InvalidDataException>(
            () => store.DeserializeReceipt(DuplicateFirstEntry(valid)));

        Assert.Contains(
            "Squad receipt block '.cursor/hooks.json' contains duplicate entry '/hooks/preToolUse/0'.",
            refused.Message,
            StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------
    // Lifecycle: the service carries blocks from the render result into the plan
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task InstallServiceCarriesRenderBlocksIntoTheReceipt()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        using FakeSquadReleaseSource releases = new();
        SquadStateStore store = Store(fixture.Path);
        SquadLifecycleService service = new(
            releases,
            new BlockStubRenderer(SquadTarget.Cursor, CursorBlock("cursor-one")),
            store,
            new FixedTimeProvider(InstalledAt));

        SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Cursor],
            Version: "1.2.3"));

        Assert.True(result.Success);
        Assert.NotNull(result.Receipt);
        Assert.Equal("kyber-squad.receipt/v3", result.Receipt.Schema);
        Assert.Single(result.Receipt.Blocks);
        Assert.Contains(
            "kyber-weave-arbiter hook --harness cursor",
            Read(targetRoot, ".cursor/hooks.json"),
            StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------

    private static SquadRenderedBlock CursorBlock(string marker) =>
        new(
            "cursor",
            ".cursor/hooks.json",
            SquadHookBlockFormat.Cursor,
            [new SquadRenderedBlockEntry("preToolUse", CursorEntry("kyber-weave-arbiter hook --harness cursor", $"probe-{marker}"))]);

    private static SquadRenderedBlock CodexBlock(string marker) =>
        new(
            "codex",
            ".codex/hooks.json",
            SquadHookBlockFormat.Codex,
            [new SquadRenderedBlockEntry("PreToolUse", MatcherGroup("kyber-weave-arbiter hook --harness codex", $"probe-{marker}"))]);

    private static JsonObject CursorEntry(string command, string matcher = "Task") =>
        new()
        {
            ["command"] = command,
            ["matcher"] = matcher,
            ["timeout"] = 5,
            ["failClosed"] = true,
        };

    private static JsonObject MatcherGroup(string command, string matcher = "pre-matcher") =>
        new()
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                }),
        };

    /// <summary>
    /// One global block. The marker rides the matcher, never the command: the command is the
    /// ownership signature, so a marker inside it would make the entry unowned and a rewrite would
    /// append beside it instead of replacing it.
    /// </summary>
    private static SquadRenderedBlock GlobalHooksBlock(SquadTarget target, string marker = "one")
    {
        string token = SquadTargetCatalog.GetToken(target);
        string command = $"kyber-weave-arbiter hook --harness {token} --scope global";
        string matcher = $"probe-{marker}";
        return target switch
        {
            SquadTarget.Cursor => new SquadRenderedBlock(
                token,
                GlobalHooksPath,
                SquadHookBlockFormat.Cursor,
                [
                    new SquadRenderedBlockEntry("preToolUse", CursorEntry(command, matcher)),
                    new SquadRenderedBlockEntry("postToolUse", CursorEntry(command, matcher)),
                ]),
            SquadTarget.Codex => new SquadRenderedBlock(
                token,
                GlobalHooksPath,
                SquadHookBlockFormat.Codex,
                [
                    new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(command, matcher)),
                    new SquadRenderedBlockEntry("PostToolUse", MatcherGroup(command, matcher)),
                ]),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Only the targets whose global hook file is named hooks.json are built here."),
        };
    }

    /// <summary>
    /// A home holding one global receipt with a cursor and a codex block, installed through the same
    /// plan and transaction an install uses. Each block is written beneath its own target's root,
    /// which is why both record the bare path <c>hooks.json</c>.
    /// </summary>
    private static GlobalBlockDeployment InstallGlobalBlocks(
        TempDirectory fixture,
        params SquadTarget[] targets)
    {
        if (targets.Length == 0)
            targets = [SquadTarget.Cursor, SquadTarget.Codex];
        FakeSquadUserPaths userPaths = new(fixture.Path);
        string home = userPaths.HomeDirectory;
        Directory.CreateDirectory(home);
        SquadGlobalRoots roots = new(_ => null, home);
        SquadStateStore store = new(userPaths);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            home,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: roots,
            blocks: [.. targets.Select(target => GlobalHooksBlock(target))]);
        new SquadTransaction(store).Execute(plan);
        return new GlobalBlockDeployment(store, roots, home);
    }

    private static string GlobalHookFile(SquadGlobalRoots roots, SquadTarget target) =>
        Path.Combine(roots.ResolveGlobalRoot(target), GlobalHooksPath);

    /// <summary>
    /// Asserts the receipt and the files agree: every recorded entry digest still matches the node
    /// at its container, in every block's own file. This is the same rule status and doctor apply,
    /// so "no drift" here means the same thing it means to the operator.
    /// </summary>
    private static void AssertNoDrift(SquadReceipt receipt, SquadGlobalRoots roots)
    {
        Assert.NotEmpty(receipt.Blocks);
        foreach (SquadOwnedBlock block in receipt.Blocks)
        {
            Assert.True(
                SquadTargetCatalog.TryResolve(block.Target, out SquadTarget target),
                $"The receipt records block target '{block.Target}', which is not a canonical Squad target.");

            SquadHookBlockFormat format = target switch
            {
                SquadTarget.Cursor => SquadHookBlockFormat.Cursor,
                SquadTarget.Codex => SquadHookBlockFormat.Codex,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(receipt),
                    target,
                    "Only cursor and codex blocks are built here."),
            };

            Dictionary<string, string> expected = block.Entries
                .ToDictionary(entry => entry.Container, entry => entry.Sha256, StringComparer.Ordinal);
            IReadOnlyList<SquadHookDrift> drifts = SquadHookJsonBlock.FindDrift(
                format,
                File.ReadAllText(GlobalHookFile(roots, target)),
                expected);

            Assert.True(
                drifts.Count == 0,
                $"The {block.Target} block at '{block.RelativePath}' drifted: "
                + string.Join("; ", drifts.Select(drift => $"{drift.Location} {drift.Reason}")));
        }
    }

    /// <summary>Two global blocks whose recorded paths are both <c>hooks.json</c>.</summary>
    private static SquadReceipt GlobalTwoBlockReceipt() =>
        new("kyber-squad.receipt/v3", SquadDeploymentScope.Global, ".", InstalledAt, [], [])
        {
            Blocks =
            [
                new SquadOwnedBlock(
                    GlobalHooksPath,
                    "cursor",
                    true,
                    [new SquadOwnedBlockEntry("/hooks/preToolUse/0", Digest("cursor-entry"))]),
                new SquadOwnedBlock(
                    GlobalHooksPath,
                    "codex",
                    true,
                    [new SquadOwnedBlockEntry("/hooks/PreToolUse/0", Digest("codex-entry"))]),
            ],
        };

    /// <summary>One project block with two owned containers, so one of them can be claimed twice.</summary>
    private static SquadReceipt ProjectCursorBlockReceipt() =>
        new("kyber-squad.receipt/v3", SquadDeploymentScope.Project, ".", InstalledAt, [], [])
        {
            Blocks =
            [
                new SquadOwnedBlock(
                    ".cursor/hooks.json",
                    "cursor",
                    true,
                    [
                        new SquadOwnedBlockEntry("/hooks/preToolUse/0", Digest("cursor-entry")),
                        new SquadOwnedBlockEntry("/hooks/postToolUse/0", Digest("cursor-entry")),
                    ]),
            ],
        };

    /// <summary>
    /// Copies one block and gives the copy <paramref name="target"/>. Hand-editing the JSON rather
    /// than the model is the point: the refusal lives in the reader, so the receipt has to reach it
    /// as bytes a corrupt or hand-written file could produce.
    /// </summary>
    private static string DuplicateBlockForTarget(string json, string sourceTarget, string target)
    {
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        JsonArray blocks = Assert.IsType<JsonArray>(root["blocks"]);
        JsonObject source = Assert.IsType<JsonObject>(blocks.Single(
            node => string.Equals(
                node!["target"]!.GetValue<string>(),
                sourceTarget,
                StringComparison.Ordinal)));
        JsonObject duplicate = Assert.IsType<JsonObject>(source.DeepClone());
        duplicate["target"] = target;
        blocks.Add(duplicate);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>Copies the first entry of the first block, so that block claims one container twice.</summary>
    private static string DuplicateFirstEntry(string json)
    {
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        JsonObject block = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(root["blocks"])[0]);
        JsonArray entries = Assert.IsType<JsonArray>(block["entries"]);
        entries.Add(entries[0]!.DeepClone());
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// The two-block global receipt 0.1.8-rc.20's own bytes describe, assembled per the recipe in
    /// <see cref="SquadGlobalBlockReceiptTests"/>. Nothing here is invented: it is the shipped
    /// shape, which is what makes it evidence that an upgrade keeps its state.
    /// </summary>
    private static string Rc20GlobalReceiptJson() =>
        File.ReadAllText(
            Path.Combine(
                KyberWeaveTestPaths.ToolRoot,
                "tests",
                "KyberWeave.Tests",
                "Fixtures",
                "squad-rc20-global",
                "two-blocks.receipt.json"),
            Encoding.UTF8);

    private static void DriftOwnedEntry(string root, string relativePath, string marker, string replacement)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        JsonNode root2 = JsonNode.Parse(File.ReadAllText(path))!;
        JsonArray pre = Assert.IsType<JsonObject>(root2)["hooks"]!.AsObject()["preToolUse"]!.AsArray();
        JsonObject owned = Assert.IsType<JsonObject>(pre.Single(
            node => node!.AsObject()["matcher"]!.GetValue<string>().Contains(marker, StringComparison.Ordinal)));
        owned["matcher"] = owned["matcher"]!.GetValue<string>().Replace(
            marker,
            replacement,
            StringComparison.Ordinal);
        File.WriteAllText(path, root2.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string RemoveJsonProperty(string json, string property)
    {
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.True(root.Remove(property));
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string AddJsonProperty(string json, string property, string value)
    {
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        root[property] = value;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// The v1/v2-only shape check the reader performed before v3 existed: only the two
    /// known schemas with their exact field sets are accepted.
    /// </summary>
    private static void OldDeserialize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string? schema = root.TryGetProperty("schema", out JsonElement schemaElement)
            ? schemaElement.GetString()
            : null;
        if (schema is not ("kyber-squad.receipt/v1" or "kyber-squad.receipt/v2"))
        {
            throw new InvalidDataException(
                $"Squad receipt schema '{schema ?? "null"}' is not a recognized canonical schema. " +
                "Update kyber-weave to read this receipt (refuses v3).");
        }

        HashSet<string> actual = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> expected = ["schema", "scope", "targetRoot", "installedAtUtc", "degradations", "files"];
        if (schema == "kyber-squad.receipt/v2")
        {
            expected = ["schema", "layout", "targetRoot", "installedAtUtc", "degradations", "files"];
        }

        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException(
                $"Squad receipt has missing or unknown fields. Found: {string.Join(", ", actual)}.");
        }
    }

    private static SquadStateStore Store(string applicationData) =>
        new(new FakeSquadUserPaths(applicationData));

    private static SquadLock Lock(string version = "1.2.3") =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["cursor"],
            [],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Digest(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class BlockStubRenderer(SquadTarget target, SquadRenderedBlock block) : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [target];

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SquadRenderResult(true, [], [], [], [], Blocks: [block]));
        }
    }

    /// <summary>
    /// An isolated home with a global deployment in it: the store that holds its receipt, the
    /// resolver that finds each target's root, and the home that anchors its state.
    /// </summary>
    private sealed record GlobalBlockDeployment(
        SquadStateStore Store,
        SquadGlobalRoots Roots,
        string Home);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }
}
