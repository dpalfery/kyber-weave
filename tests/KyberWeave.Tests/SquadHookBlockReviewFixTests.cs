using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the production properties of the owned-block lifecycle that the Phase 2 council
/// review (15.1) found asserted only against dead code: the deployment plan and the
/// file-level splice share one implementation, an unusable user file is never written,
/// re-splicing never duplicates, blank files work, user text round-trips byte-faithfully,
/// and a user hook inserted ahead of Squad's entry is not mistaken for drift.
/// </summary>
public sealed class SquadHookBlockReviewFixTests
{
    private static readonly DateTimeOffset InstalledAt = new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    private const string UserHook = """{ "command": "lint && fmt 'x' <y> é", "matcher": "Edit", "timeout": 3, "failClosed": false }""";

    [Fact]
    public void Install_WhenUserFileDoesNotParse_ThrowsAndLeavesTheFileUntouched()
    {
        using TempDirectory fixture = new();
        const string broken = "{ \"hooks\": [ not json";
        Write(fixture.Path, ".cursor/hooks.json", broken);

        SquadDeploymentConflictException ex = Assert.Throws<SquadDeploymentConflictException>(
            () => Install(fixture.Path, CursorBlock("one")));

        Assert.Contains(".cursor/hooks.json", ex.Message, StringComparison.Ordinal);
        Assert.Equal(broken, Read(fixture.Path, ".cursor/hooks.json"));
    }

    [Fact]
    public void Update_WithUnchangedBlock_DoesNotDuplicateSquadEntries()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan install = Install(fixture.Path, CursorBlock("one"));
        new SquadTransaction(store).Execute(install);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));

        new SquadTransaction(store).Execute(Update(fixture.Path, previous, CursorBlock("one")));

        Assert.Equal(1, CountOccurrences(Read(fixture.Path, ".cursor/hooks.json"), "kyber-weave-arbiter hook --harness cursor"));
    }

    [Fact]
    public void Install_IntoABlankUserFile_StartsFromTheMinimalDocument()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        Write(fixture.Path, ".codex/hooks.json", string.Empty);

        new SquadTransaction(store).Execute(Install(fixture.Path, CodexBlock("one")));

        JsonNode parsed = JsonNode.Parse(Read(fixture.Path, ".codex/hooks.json"))!;
        Assert.Single(parsed["hooks"]!["PreToolUse"]!.AsArray());
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));
        Assert.False(Assert.Single(receipt.Blocks).CreatedFile);

        new SquadTransaction(store).Execute(SquadDeploymentPlan.CreateUninstall(
            fixture.Path, SquadDeploymentScope.Project, receipt));

        // Squad did not create the file, so uninstall leaves it behind without our entries.
        string after = Read(fixture.Path, ".codex/hooks.json");
        Assert.DoesNotContain("kyber-weave-arbiter", after, StringComparison.Ordinal);
        Assert.NotNull(JsonNode.Parse(after));
    }

    [Fact]
    public void Install_WritesOnlyDocumentedFields()
    {
        using TempDirectory fixture = new();
        JsonObject entry = CursorEntry("kyber-weave-arbiter hook --harness cursor --probe one");
        entry["sentinel"] = "must-not-reach-the-file";
        SquadRenderedBlock block = new(
            "cursor",
            ".cursor/hooks.json",
            SquadHookBlockFormat.Cursor,
            [new SquadRenderedBlockEntry("preToolUse", entry)]);

        new SquadTransaction(Store(fixture.Path)).Execute(Install(fixture.Path, block));

        Assert.DoesNotContain("sentinel", Read(fixture.Path, ".cursor/hooks.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void InstallAndUninstall_KeepTheUsersOwnTextByteFaithful()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        Write(fixture.Path, ".cursor/hooks.json", UserFile());

        new SquadTransaction(store).Execute(Install(fixture.Path, CursorBlock("one")));
        Assert.Contains("lint && fmt 'x' <y> é", Read(fixture.Path, ".cursor/hooks.json"), StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));
        new SquadTransaction(store).Execute(SquadDeploymentPlan.CreateUninstall(
            fixture.Path, SquadDeploymentScope.Project, receipt));

        string after = Read(fixture.Path, ".cursor/hooks.json");
        Assert.Contains("lint && fmt 'x' <y> é", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0026", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_WhenTheUserInsertsAHookAheadOfSquads_ReportsNoDriftAndRefreshes()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        new SquadTransaction(store).Execute(Install(fixture.Path, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));

        // The user puts their own hook first, shifting Squad's entry from index 0 to 1.
        string path = Path.Combine(fixture.Path, ".cursor", "hooks.json");
        JsonNode file = JsonNode.Parse(File.ReadAllText(path))!;
        file["hooks"]!["preToolUse"]!.AsArray().Insert(0, JsonNode.Parse(UserHook));
        File.WriteAllText(path, file.ToJsonString());

        SquadDeploymentPlan update = Update(fixture.Path, previous, CursorBlock("two"));
        Assert.Empty(update.BlockDrifts);
        new SquadTransaction(store).Execute(update);

        string content = Read(fixture.Path, ".cursor/hooks.json");
        Assert.Contains("lint && fmt", content, StringComparison.Ordinal);
        Assert.Contains("--probe two", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--probe one", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_WhenAReceiptOwnedFileWasBlanked_StillConflicts()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        new SquadTransaction(store).Execute(Install(fixture.Path, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));
        Write(fixture.Path, ".cursor/hooks.json", string.Empty);

        // A blank file is fine for a first install, but Squad's recorded entries cannot
        // vanish silently: the file needs hand repair, so update refuses.
        Assert.Throws<SquadDeploymentConflictException>(
            () => Update(fixture.Path, previous, CursorBlock("two")));
    }

    [Fact]
    public void Update_WhenTheUserEditsSquadsEntry_StillReportsDrift()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        new SquadTransaction(store).Execute(Install(fixture.Path, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(fixture.Path, SquadDeploymentScope.Project));

        string path = Path.Combine(fixture.Path, ".cursor", "hooks.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("--probe one", "--probe edited", StringComparison.Ordinal));

        Assert.NotEmpty(Update(fixture.Path, previous, CursorBlock("two")).BlockDrifts);
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    public void PlanAndFileLevelSplice_ProduceIdenticalContent(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        SquadRenderedBlock block = format == SquadHookBlockFormat.Cursor ? CursorBlock("one") : CodexBlock("one");
        new SquadTransaction(Store(fixture.Path)).Execute(Install(fixture.Path, block));
        string viaPlan = Read(fixture.Path, SquadHookJsonBlock.RelativePath(format));

        string filePath = Path.Combine(fixture.Path, "direct", "hooks.json");
        SquadHookJsonBlock.SpliceFile(
            format,
            filePath,
            [.. block.Entries.Select(e => e.Entry!.DeepClone())],
            []);

        Assert.Equal(viaPlan, File.ReadAllText(filePath));
    }

    // ----------------------------------------------------------------------------------

    private static SquadDeploymentPlan Install(string root, SquadRenderedBlock block) =>
        SquadDeploymentPlan.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [block]);

    private static SquadDeploymentPlan Update(string root, SquadReceipt previous, SquadRenderedBlock block) =>
        SquadDeploymentPlan.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            previous,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddHours(1)),
            blocks: [block]);

    private static string UserFile() =>
        $$"""
        { "version": 1, "hooks": { "preToolUse": [ {{UserHook}} ], "postToolUse": [] } }
        """;

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

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

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
