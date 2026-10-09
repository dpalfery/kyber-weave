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
        Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(AddJsonProperty(valid, "layout", "per-target-roots")));
        Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(valid.Replace(
            "\"scope\": \"project\"",
            "\"scope\": \"global\"",
            StringComparison.Ordinal)));
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
            [new SquadRenderedBlockEntry("preToolUse", CursorEntry($"kyber-weave-arbiter hook --harness cursor --probe {marker}"))]);

    private static SquadRenderedBlock CodexBlock(string marker) =>
        new(
            "codex",
            ".codex/hooks.json",
            SquadHookBlockFormat.Codex,
            [new SquadRenderedBlockEntry("PreToolUse", MatcherGroup($"kyber-weave-arbiter hook --harness codex --probe {marker}"))]);

    private static JsonObject CursorEntry(string command) =>
        new()
        {
            ["command"] = command,
            ["matcher"] = "Task",
            ["timeout"] = 5,
            ["failClosed"] = true,
        };

    private static JsonObject MatcherGroup(string command) =>
        new()
        {
            ["matcher"] = "pre-matcher",
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                }),
        };

    private static void DriftOwnedEntry(string root, string relativePath, string marker, string replacement)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        JsonNode root2 = JsonNode.Parse(File.ReadAllText(path))!;
        JsonArray pre = Assert.IsType<JsonObject>(root2)["hooks"]!.AsObject()["preToolUse"]!.AsArray();
        JsonObject owned = Assert.IsType<JsonObject>(pre.Single(
            node => node!.AsObject()["command"]!.GetValue<string>().Contains(marker, StringComparison.Ordinal)));
        owned["command"] = owned["command"]!.GetValue<string>().Replace(
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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }
}
