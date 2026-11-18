using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

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

    private readonly InMemorySquadState _state = new();
    private readonly string _root;

    public SquadHookBlockReviewFixTests() => _root = _state.CreateRoot("project");

    private const string UserHook = """{ "command": "lint && fmt 'x' <y> é", "matcher": "Edit", "timeout": 3, "failClosed": false }""";

    [Fact]
    public void Install_WhenUserFileDoesNotParse_ThrowsAndLeavesTheFileUntouched()
    {
        const string broken = "{ \"hooks\": [ not json";
        Write(_root, ".cursor/hooks.json", broken);

        SquadDeploymentConflictException ex = Assert.Throws<SquadDeploymentConflictException>(
            () => Install(_root, CursorBlock("one")));

        Assert.Contains(".cursor/hooks.json", ex.Message, StringComparison.Ordinal);
        Assert.Equal(broken, Read(_root, ".cursor/hooks.json"));
    }

    [Fact]
    public void Update_WithUnchangedBlock_DoesNotDuplicateSquadEntries()
    {
        SquadStateStore store = _state.Store;
        SquadDeploymentPlan install = Install(_root, CursorBlock("one"));
        _state.Transaction().Execute(install);
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));

        _state.Transaction().Execute(Update(_root, previous, CursorBlock("one")));

        Assert.Equal(1, CountOccurrences(Read(_root, ".cursor/hooks.json"), "kyber-weave-arbiter hook --harness cursor"));
    }

    [Fact]
    public void Install_IntoABlankUserFile_StartsFromTheMinimalDocument()
    {
        SquadStateStore store = _state.Store;
        Write(_root, ".codex/hooks.json", string.Empty);

        _state.Transaction().Execute(Install(_root, CodexBlock("one")));

        JsonNode parsed = JsonNode.Parse(Read(_root, ".codex/hooks.json"))!;
        Assert.Single(parsed["hooks"]!["PreToolUse"]!.AsArray());
        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));
        Assert.False(Assert.Single(receipt.Blocks).CreatedFile);

        _state.Transaction().Execute(_state.Planner.CreateUninstall(
            _root, SquadDeploymentScope.Project, receipt));

        // Squad did not create the file, so uninstall leaves it behind without our entries.
        string after = Read(_root, ".codex/hooks.json");
        Assert.DoesNotContain("kyber-weave-arbiter", after, StringComparison.Ordinal);
        Assert.NotNull(JsonNode.Parse(after));
    }

    [Fact]
    public void Install_WritesOnlyDocumentedFields()
    {
        JsonObject entry = CursorEntry("kyber-weave-arbiter hook --harness cursor", "probe-one");
        entry["sentinel"] = "must-not-reach-the-file";
        SquadRenderedBlock block = new(
            "cursor",
            ".cursor/hooks.json",
            SquadHookBlockFormat.Cursor,
            [new SquadRenderedBlockEntry("preToolUse", entry)]);

        _state.Transaction().Execute(Install(_root, block));

        Assert.DoesNotContain("sentinel", Read(_root, ".cursor/hooks.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void InstallAndUninstall_KeepTheUsersOwnTextByteFaithful()
    {
        SquadStateStore store = _state.Store;
        Write(_root, ".cursor/hooks.json", UserFile());

        _state.Transaction().Execute(Install(_root, CursorBlock("one")));
        Assert.Contains("lint && fmt 'x' <y> é", Read(_root, ".cursor/hooks.json"), StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));
        _state.Transaction().Execute(_state.Planner.CreateUninstall(
            _root, SquadDeploymentScope.Project, receipt));

        string after = Read(_root, ".cursor/hooks.json");
        Assert.Contains("lint && fmt 'x' <y> é", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0026", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_WhenTheUserInsertsAHookAheadOfSquads_ReportsNoDriftAndRefreshes()
    {
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(_root, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));

        // The user puts their own hook first, shifting Squad's entry from index 0 to 1.
        string path = Path.Combine(_root, ".cursor", "hooks.json");
        JsonNode file = JsonNode.Parse(_state.Files.ReadAllText(path))!;
        file["hooks"]!["preToolUse"]!.AsArray().Insert(0, JsonNode.Parse(UserHook));
        _state.Files.WriteAllText(path, file.ToJsonString());

        SquadDeploymentPlan update = Update(_root, previous, CursorBlock("two"));
        Assert.Empty(update.BlockDrifts);
        _state.Transaction().Execute(update);

        string content = Read(_root, ".cursor/hooks.json");
        Assert.Contains("lint && fmt", content, StringComparison.Ordinal);
        Assert.Contains("\"matcher\": \"probe-two\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"matcher\": \"probe-one\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_WhenAReceiptOwnedFileWasBlanked_StillConflicts()
    {
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(_root, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));
        Write(_root, ".cursor/hooks.json", string.Empty);

        // A blank file is fine for a first install, but Squad's recorded entries cannot
        // vanish silently: the file needs hand repair, so update refuses.
        Assert.Throws<SquadDeploymentConflictException>(
            () => Update(_root, previous, CursorBlock("two")));
    }

    [Fact]
    public void Update_WhenOneOfTwoIdenticalSquadEntriesIsEdited_StillReportsDrift()
    {
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(_root, TwoIdenticalCursorEntries()));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));

        // Two byte-identical owned entries, recorded at index 0 and 1 with one digest.
        // Editing the first left its twin intact, and the twin then satisfied the edited
        // entry's digest: the edit read as "Squad's entry, merely moved", so no drift was
        // reported and the update overwrote the user's edit without a word.
        string path = Path.Combine(_root, ".cursor", "hooks.json");
        JsonNode file = JsonNode.Parse(_state.Files.ReadAllText(path))!;
        JsonArray entries = file["hooks"]!["preToolUse"]!.AsArray();
        Assert.Equal(2, entries.Count);
        ((JsonObject)entries[0]!)["matcher"] = "probe-edited";
        _state.Files.WriteAllText(path, file.ToJsonString());

        IReadOnlyList<SquadOwnedBlockDrift> drifts = Update(_root, previous, TwoIdenticalCursorEntries()).BlockDrifts;

        Assert.Single(drifts);
        Assert.EndsWith("/0", drifts[0].Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_WhenAReceiptOwnedFileWasBlanked_RetainsTheBlockForRepair()
    {
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(_root, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));
        Write(_root, ".cursor/hooks.json", string.Empty);

        // Update already refuses here: Squad's recorded entries cannot vanish silently.
        // Uninstall disagreed, reporting the block removed and dropping it from the
        // receipt, so the file was left blank with nothing recorded to repair it from.
        // The two sides must make the same call.
        SquadDeploymentPlan uninstall = _state.Planner.CreateUninstall(
            _root, SquadDeploymentScope.Project, previous);

        // The retained receipt is rewritten, not deleted: it still owns a block, so it stays
        // on the schema that records blocks and the entries survive for hand repair.
        Assert.NotEqual(SquadStateMutation.Delete, uninstall.ReceiptMutation);
        Assert.NotEmpty(uninstall.Receipt!.Blocks);

        _state.Transaction().Execute(uninstall);

        SquadReceipt after = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));
        Assert.NotEmpty(after.Blocks);
    }

    [Fact]
    public void Uninstall_WhenABlankedFileHasNoReceiptOwnedEntries_IsANoOp()
    {
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(_root, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));

        // A file the receipt owns no entries in has nothing to lose, so a blank one is
        // still just a minimal document and the block is genuinely gone.
        SquadReceipt withoutEntries =
            previous with { Blocks = [.. previous.Blocks.Select(block => block with { Entries = [] })] };
        Write(_root, ".cursor/hooks.json", string.Empty);

        SquadDeploymentPlan uninstall = _state.Planner.CreateUninstall(
            _root, SquadDeploymentScope.Project, withoutEntries);

        Assert.Equal(SquadStateMutation.Delete, uninstall.ReceiptMutation);
        Assert.Empty(uninstall.Receipt!.Blocks);
    }

    [Fact]
    public void Update_WhenTheUserEditsSquadsEntry_StillReportsDrift()
    {
        SquadStateStore store = _state.Store;
        _state.Transaction().Execute(Install(_root, CursorBlock("one")));
        SquadReceipt previous = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(_root, SquadDeploymentScope.Project));

        string path = Path.Combine(_root, ".cursor", "hooks.json");
        _state.Files.WriteAllText(path, _state.Files.ReadAllText(path).Replace("\"matcher\": \"probe-one\"", "\"matcher\": \"probe-edited\"", StringComparison.Ordinal));

        Assert.NotEmpty(Update(_root, previous, CursorBlock("two")).BlockDrifts);
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    public void PlanAndFileLevelSplice_ProduceIdenticalContent(SquadHookBlockFormat format)
    {
        SquadRenderedBlock block = format == SquadHookBlockFormat.Cursor ? CursorBlock("one") : CodexBlock("one");
        _state.Transaction().Execute(Install(_root, block));
        string viaPlan = Read(_root, SquadHookJsonBlock.RelativePath(format));

        string filePath = Path.Combine(_root, "direct", "hooks.json");
        new SquadHookJsonFile(_state.Files, _state.Environment).SpliceFile(
            format,
            filePath,
            [.. block.Entries.Select(e => e.Entry!.DeepClone())],
            []);

        Assert.Equal(viaPlan, _state.Files.ReadAllText(filePath));
    }

    // ----------------------------------------------------------------------------------

    private SquadDeploymentPlan Install(string root, SquadRenderedBlock block) =>
        _state.Planner.CreateInstall(
            root,
            SquadDeploymentScope.Project,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            blocks: [block]);

    private SquadDeploymentPlan Update(string root, SquadReceipt previous, SquadRenderedBlock block) =>
        _state.Planner.CreateUpdate(
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
            [new SquadRenderedBlockEntry("preToolUse", CursorEntry("kyber-weave-arbiter hook --harness cursor", $"probe-{marker}"))]);

    /// <summary>Two owned entries with identical content, so both record the same digest.</summary>
    private static SquadRenderedBlock TwoIdenticalCursorEntries() =>
        new(
            "cursor",
            ".cursor/hooks.json",
            SquadHookBlockFormat.Cursor,
            [
                new SquadRenderedBlockEntry("preToolUse", CursorEntry("kyber-weave-arbiter hook --harness cursor", "probe-one")),
                new SquadRenderedBlockEntry("preToolUse", CursorEntry("kyber-weave-arbiter hook --harness cursor", "probe-one")),
            ]);

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

    private void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllText(path, content);
    }

    private string Read(string root, string relativePath) =>
        _state.Files.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
