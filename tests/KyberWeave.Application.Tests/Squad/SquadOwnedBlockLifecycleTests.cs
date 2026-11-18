using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
/// Pins the owned-block lifecycle (task 11.2): renderers carry block fragments through the
/// registry, install splices them into shared hook files and claims only the block in a
/// <c>kyber-squad.receipt/v3</c> receipt, update rewrites while preserving hand-edited
/// entries unless <c>--replace-managed</c> is given, and uninstall removes only the owned
/// entries. Receipts without blocks stay byte-identical v1 or v2.
/// </summary>
public sealed class SquadOwnedBlockLifecycleTests
{
    private readonly InMemorySquadState _state = new();

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
        string source = CanonicalSquadTree.CopyInto(_state.Files, Path.Combine(_state.Files.Root, "release", "kyber-squad"));
        SquadRendererRegistry registry = new(
        [
            FakeSquadRenderer.WithBlocks(SquadTarget.Cursor, CursorBlock("cursor-one")),
            FakeSquadRenderer.WithBlocks(SquadTarget.Codex, CodexBlock("codex-one")),
        ],
        new InMemorySquadServices(_state.Files).RenderInputReader());

        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            source,
            [SquadTarget.Cursor, SquadTarget.Codex],
            SquadDeploymentScope.Project).Prepared(_state.Files));

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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);

        _state.Transaction().Execute(plan);

        SquadReceipt? receipt = store.ReadReceipt(root, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.Empty(receipt.Files);
        SquadOwnedBlock block = Assert.Single(receipt.Blocks);
        Assert.Equal(".cursor/hooks.json", block.RelativePath);
        Assert.Equal("cursor", block.Target);
        Assert.True(block.CreatedFile);
        Assert.NotEmpty(block.Entries);

        string content = Read(root, ".cursor/hooks.json");
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
        string root = ProjectRoot();
        Write(root, ".cursor/hooks.json", """
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

        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);

        // An existing user file at a block path is owned content to splice into, never
        // an UnmanagedCollision: the plan builds where CreateInstall used to throw.
        _state.Transaction().Execute(plan);

        string content = Read(root, ".cursor/hooks.json");
        Assert.Contains("user-linter --fast", content, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor", content, StringComparison.Ordinal);

        SquadReceipt? receipt = store.ReadReceipt(root, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.False(Assert.Single(receipt.Blocks).CreatedFile);
    }

    [Fact]
    public void InstallUsesExactPreconditionOnExistingFileAndMissingWhenAbsent()
    {
        string root = ProjectRoot();
        string existing = """
            {
              "version": 1,
              "hooks": { "preToolUse": [], "postToolUse": [] }
            }
            """;
        Write(root, ".cursor/hooks.json", existing);
        string existingDigest = Digest(_state.Files.ReadAllBytes(
            Path.Combine(root, ".cursor/hooks.json")));

        SquadDeploymentPlan existingPlan = _state.Planner.CreateInstall(
            root,
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

        string absent = ProjectRoot("absent");
        SquadDeploymentPlan absentPlan = _state.Planner.CreateInstall(
            absent,
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan plan = _state.Planner.CreateInstall(
            root,
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan install = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(install);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Project));

        SquadDeploymentPlan update = _state.Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            previous,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddHours(1)),
            blocks: [CursorBlock("cursor-two")]);
        _state.Transaction().Execute(update);

        string content = Read(root, ".cursor/hooks.json");
        Assert.Contains("cursor-two", content, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor-one", content, StringComparison.Ordinal);

        SquadReceipt? receipt = store.ReadReceipt(root, SquadDeploymentScope.Project);
        Assert.NotNull(receipt);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.Single(receipt.Blocks);
    }

    [Fact]
    public void UpdatePreservesDriftedEntryUnlessReplaceManagedIsGiven()
    {
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan install = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(install);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Project));

        DriftOwnedEntry(root, ".cursor/hooks.json", "cursor-one", "cursor-one drifted by hand");

        SquadDeploymentPlan preserved = _state.Planner.CreateUpdate(
            root,
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
        _state.Transaction().Execute(preserved);
        Assert.Contains("cursor-one drifted by hand", Read(root, ".cursor/hooks.json"), StringComparison.Ordinal);

        SquadDeploymentPlan replaced = _state.Planner.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            Assert.IsType<SquadReceipt>(store.ReadReceipt(root, SquadDeploymentScope.Project)),
            [],
            replaceManaged: true,
            new FixedTimeProvider(InstalledAt.AddHours(2)),
            blocks: [CursorBlock("cursor-two")]);
        Assert.Empty(replaced.BlockDrifts);
        _state.Transaction().Execute(replaced);
        string content = Read(root, ".cursor/hooks.json");
        Assert.Contains("cursor-two", content, StringComparison.Ordinal);
        Assert.DoesNotContain("drifted by hand", content, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------
    // Uninstall: removes only the owned entries
    // ----------------------------------------------------------------------------------

    [Fact]
    public void UninstallRemovesOnlyTheOwnedEntries()
    {
        string root = ProjectRoot();
        Write(root, ".cursor/hooks.json", """
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
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan install = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(install);
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Project));

        SquadDeploymentPlan uninstall = _state.Planner.CreateUninstall(
            root,
            SquadDeploymentScope.Project,
            receipt);
        _state.Transaction().Execute(uninstall);

        // The user's file survives with only their entries: Squad created nothing here.
        string content = Read(root, ".cursor/hooks.json");
        Assert.Contains("user-linter --fast", content, StringComparison.Ordinal);
        Assert.DoesNotContain("kyber-weave-arbiter", content, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallDeletesTheFileOnlyWhenSquadCreatedItAndNoHookRemains()
    {
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan install = _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [CursorBlock("cursor-one")]);
        _state.Transaction().Execute(install);
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.True(Assert.Single(receipt.Blocks).CreatedFile);

        SquadDeploymentPlan uninstall = _state.Planner.CreateUninstall(
            root,
            SquadDeploymentScope.Project,
            receipt);
        _state.Transaction().Execute(uninstall);

        Assert.False(_state.Files.FileExists(Path.Combine(root, ".cursor/hooks.json")));
        Assert.Null(store.ReadReceipt(root, SquadDeploymentScope.Project));
    }

    [Fact]
    public void UninstallOneHarnessSplicesOnlyItsEntriesAndKeepsUserEmptyArraysAndTheOtherBlock()
    {
        string root = ProjectRoot();
        Write(root, "settings.json", """
            {
              "theme": "dark",
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "user-linter" } ] }
                ],
                "Stop": []
              }
            }
            """);
        SquadStateStore store = InstallProjectClaudeAndCursor(root);
        string cursorBefore = Read(root, ".cursor/hooks.json");

        UninstallOnly(store, root, SquadTarget.Claude);

        JsonObject settings = Assert.IsType<JsonObject>(JsonNode.Parse(Read(root, "settings.json")));
        JsonObject hooks = Assert.IsType<JsonObject>(settings["hooks"]);
        Assert.Equal("dark", settings["theme"]!.GetValue<string>());
        Assert.False(hooks.ContainsKey("PreToolUse"));
        Assert.Contains("user-linter", Read(root, "settings.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("kyber-weave-arbiter", Read(root, "settings.json"), StringComparison.Ordinal);
        Assert.Single(hooks["PostToolUse"]!.AsArray());
        Assert.Empty(hooks["Stop"]!.AsArray());
        Assert.Equal(cursorBefore, Read(root, ".cursor/hooks.json"));
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Equal("cursor", Assert.Single(receipt.Blocks).Target);
        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
    }

    [Fact]
    public void UninstallOneHarnessPrunesAnEmptiedHooksObjectButKeepsTheUserFile()
    {
        string root = ProjectRoot();
        Write(root, "settings.json", "{ \"theme\": \"dark\" }");
        SquadStateStore store = InstallProjectClaudeAndCursor(root);
        Assert.Contains("kyber-weave-arbiter", Read(root, "settings.json"), StringComparison.Ordinal);

        UninstallOnly(store, root, SquadTarget.Claude);

        JsonObject settings = Assert.IsType<JsonObject>(JsonNode.Parse(Read(root, "settings.json")));
        Assert.False(settings.ContainsKey("hooks"), settings.ToJsonString());
        Assert.Equal("dark", settings["theme"]!.GetValue<string>());
        Assert.Contains("kyber-weave-arbiter", Read(root, ".cursor/hooks.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallOneHarnessDeletesTheHookFileSquadCreatedAndTheLastHarnessRemovesTheReceipt()
    {
        string root = ProjectRoot();
        SquadStateStore store = InstallProjectClaudeAndCursor(root);

        UninstallOnly(store, root, SquadTarget.Cursor);

        Assert.False(_state.Files.FileExists(Path.Combine(root, ".cursor/hooks.json")));
        Assert.Contains("kyber-weave-arbiter", Read(root, "settings.json"), StringComparison.Ordinal);
        Assert.Equal(
            "claude",
            Assert.Single(Assert.IsType<SquadReceipt>(
                store.ReadReceipt(root, SquadDeploymentScope.Project)).Blocks).Target);

        UninstallOnly(store, root, SquadTarget.Claude);

        Assert.False(_state.Files.FileExists(Path.Combine(root, "settings.json")));
        Assert.Null(store.ReadReceipt(root, SquadDeploymentScope.Project));
    }

    /// <summary>
    /// The retained-schema rule the uninstall plan applies: a receipt that still owns a
    /// block stays on v3, while one whose blocks are all gone drops to the block-less
    /// schema for its scope (v2 at global scope, v1 at project scope). Blocks are uninstalled
    /// through FormatForTarget; conflicted hook files retain their block on v3, while cleanly
    /// removed blocks transition a v3 receipt to v2.
    /// </summary>
    [Fact]
    public void UninstallGlobalRetainedSchemaFollowsTheWriterRule()
    {
        GlobalBlockDeployment installedCorrupted = InstallGlobalBlocks(
            _state, SquadTarget.Cursor, SquadTarget.Codex);
        SquadReceipt twoBlocks = Assert.IsType<SquadReceipt>(
            installedCorrupted.Store.ReadReceipt(installedCorrupted.Home, SquadDeploymentScope.Global));
        Assert.Equal("kyber-squad.receipt/v3", twoBlocks.Schema);
        Assert.Equal(2, twoBlocks.Blocks.Count);

        // Corrupt both hook files so RemoveBlockContent throws SquadDeploymentConflictException,
        // exercising FormatForTarget during uninstall while retaining both blocks.
        _state.Files.WriteAllText(GlobalHookFile(installedCorrupted.Roots, SquadTarget.Cursor), "{ unclosed json");
        _state.Files.WriteAllText(GlobalHookFile(installedCorrupted.Roots, SquadTarget.Codex), "{ unclosed json");

        SquadDeploymentPlan keepBoth = _state.Planner.CreateUninstall(
            installedCorrupted.Home,
            SquadDeploymentScope.Global,
            twoBlocks,
            installedCorrupted.Roots);

        GlobalBlockDeployment installedClean = InstallGlobalBlocks(
            new InMemorySquadState(), SquadTarget.Cursor, SquadTarget.Codex);
        SquadReceipt cleanReceipt = Assert.IsType<SquadReceipt>(
            installedClean.Store.ReadReceipt(installedClean.Home, SquadDeploymentScope.Global));
        Assert.Equal("kyber-squad.receipt/v3", cleanReceipt.Schema);
        Assert.Equal(2, cleanReceipt.Blocks.Count);

        SquadDeploymentPlan keepNone = installedClean.State.Planner.CreateUninstall(
            installedClean.Home,
            SquadDeploymentScope.Global,
            cleanReceipt,
            installedClean.Roots);

        Assert.Equal(2, keepBoth.Receipt.Blocks.Count);
        Assert.Equal("kyber-squad.receipt/v3", keepBoth.Receipt.Schema);
        Assert.Empty(keepNone.Receipt.Blocks);
        Assert.Equal("kyber-squad.receipt/v2", keepNone.Receipt.Schema);
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
        string root = ProjectRoot();
        GlobalBlockDeployment installed = InstallGlobalBlocks(_state, SquadTarget.Cursor, SquadTarget.Codex);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            installed.Store.ReadReceipt(installed.Home, SquadDeploymentScope.Global));

        SquadDeploymentPlan update = _state.Planner.CreateUpdate(
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
        _state.Transaction().Execute(update);

        SquadReceipt after = Assert.IsType<SquadReceipt>(
            installed.Store.ReadReceipt(installed.Home, SquadDeploymentScope.Global));
        Assert.Equal(["cursor", "codex"], after.Blocks.Select(block => block.Target).ToArray());
        Assert.All(after.Blocks, block => Assert.Equal(GlobalHooksPath, block.RelativePath));
        AssertNoDrift(after, installed.Roots);

        string cursorHookFile = _state.Files.ReadAllText(GlobalHookFile(installed.Roots, SquadTarget.Cursor));
        Assert.Contains("probe-cursor-two", cursorHookFile, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-cursor-one", cursorHookFile, StringComparison.Ordinal);
        Assert.Contains(
            "probe-codex-two",
            _state.Files.ReadAllText(GlobalHookFile(installed.Roots, SquadTarget.Codex)),
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
        string root = ProjectRoot();
        string home = _state.UserPaths.HomeDirectory;
        _state.Files.CreateDirectory(home);
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore store = _state.Store;
        string receiptPath = store.ResolveReceiptPath(home, SquadDeploymentScope.Global);
        _state.Files.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        _state.Files.WriteAllText(receiptPath, Rc20GlobalReceiptJson());

        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal(["cursor", "codex"], previous.Blocks.Select(block => block.Target).ToArray());

        SquadDeploymentPlan update = _state.Planner.CreateUpdate(
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

        _state.Transaction().Execute(update);

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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
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
        string root = ProjectRoot();
        SquadStateStore store = _state.Store;
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
        string root = ProjectRoot();
        string targetRoot = Path.Combine(root, "project");
        _state.Files.CreateDirectory(targetRoot);
        string source = CanonicalSquadTree.CopyInto(_state.Files, Path.Combine(_state.Files.Root, "release", "kyber-squad"));
        using FakeSquadReleaseSource releases = FakeSquadReleaseSource.FromDirectory(source, _state.Files);
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(
            _state,
            releases,
            FakeSquadRenderer.WithBlocks(SquadTarget.Cursor, CursorBlock("cursor-one")));

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

    private static SquadRenderedBlock ClaudeBlock(string marker) =>
        new(
            "claude",
            "settings.json",
            SquadHookBlockFormat.Claude,
            [
                new SquadRenderedBlockEntry("PreToolUse", MatcherGroup("kyber-weave-arbiter hook --harness claude", $"probe-{marker}")),
                new SquadRenderedBlockEntry("PostToolUse", MatcherGroup("kyber-weave-arbiter hook --harness claude", $"probe-{marker}")),
            ]);

    private SquadStateStore InstallProjectClaudeAndCursor(string root)
    {
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(_state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [ClaudeBlock("claude-one"), CursorBlock("cursor-one")]));
        return store;
    }

    private void UninstallOnly(SquadStateStore store, string root, SquadTarget target)
    {
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(root, SquadDeploymentScope.Project));
        _state.Transaction().Execute(_state.Planner.CreateUninstall(
            root,
            SquadDeploymentScope.Project,
            receipt,
            onlyTarget: target));
    }

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
        InMemorySquadState state,
        params SquadTarget[] targets)
    {
        string home = state.UserPaths.HomeDirectory;
        state.Files.CreateDirectory(home);
        SquadGlobalRoots roots = new(_ => null, home);
        SquadDeploymentPlan plan = state.Planner.CreateInstall(
            home,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: roots,
            blocks: [.. targets.Select(target => GlobalHooksBlock(target))]);
        state.Transaction().Execute(plan);
        return new GlobalBlockDeployment(state, state.Store, roots, home);
    }

    private static string GlobalHookFile(SquadGlobalRoots roots, SquadTarget target) =>
        Path.Combine(roots.ResolveGlobalRoot(target), GlobalHooksPath);

    /// <summary>
    /// Asserts the receipt and the files agree: every recorded entry digest still matches the node
    /// at its container, in every block's own file. This is the same rule status and doctor apply,
    /// so "no drift" here means the same thing it means to the operator.
    /// </summary>
    private void AssertNoDrift(SquadReceipt receipt, SquadGlobalRoots roots)
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
                _state.Files.ReadAllText(GlobalHookFile(roots, target)),
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
    /// <c>SquadGlobalBlockReceiptTests</c>. Nothing here is invented: it is the shipped
    /// shape, which is what makes it evidence that an upgrade keeps its state.
    /// </summary>
    private static string Rc20GlobalReceiptJson() =>
        SquadRc20Fixtures.TwoBlocksReceipt();

    private void DriftOwnedEntry(string root, string relativePath, string marker, string replacement)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        JsonNode root2 = JsonNode.Parse(_state.Files.ReadAllText(path))!;
        JsonArray pre = Assert.IsType<JsonObject>(root2)["hooks"]!.AsObject()["preToolUse"]!.AsArray();
        JsonObject owned = Assert.IsType<JsonObject>(pre.Single(
            node => node!.AsObject()["matcher"]!.GetValue<string>().Contains(marker, StringComparison.Ordinal)));
        owned["matcher"] = owned["matcher"]!.GetValue<string>().Replace(
            marker,
            replacement,
            StringComparison.Ordinal);
        _state.Files.WriteAllText(path, root2.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
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

    private string ProjectRoot(string name = "project")
    {
        string path = Path.Combine(_state.Files.Root, "work", name);
        _state.Files.CreateDirectory(path);
        return path;
    }

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

    private void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllText(path, content);
    }

    private string Read(string root, string relativePath) =>
        _state.Files.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// An isolated home with a global deployment in it: the store that holds its receipt, the
    /// resolver that finds each target's root, and the home that anchors its state.
    /// </summary>
    private sealed record GlobalBlockDeployment(
        InMemorySquadState State,
        SquadStateStore Store,
        SquadGlobalRoots Roots,
        string Home);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }
}
