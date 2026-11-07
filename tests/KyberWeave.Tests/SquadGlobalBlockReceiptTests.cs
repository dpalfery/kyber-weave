using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins one thing: under global scope a stored hook block is identified by its target as well as
/// its portable path, because every block-bearing target writes beneath its own root and four of
/// the five formats name the same bare path <c>hooks.json</c> there.
/// </summary>
/// <remarks>
/// <para>
/// The defect this class exists for shipped in 0.1.8-rc.20: a first global target installed
/// cleanly, and the second block-bearing target failed and rolled back with
/// <c>Squad receipt block path 'hooks.json' for target 'cursor' is not a unique portable
/// deployment path.</c> The receipt validator keyed block uniqueness on the path alone, so four
/// of the five targets could never share one global receipt.
/// </para>
/// <para>
/// A receipt rc.20 already wrote must keep working, so the rc.20 case below reads real bytes the
/// shipped binary produced rather than a hand-built document. That receipt is the only proof the
/// upgrade path has: the stored shape cannot change, whatever the fix does internally.
/// </para>
/// </remarks>
public sealed class SquadGlobalBlockReceiptTests
{
    private const string FixtureRelativePath = "tests/KyberWeave.Tests/Fixtures/squad-rc20-global";
    private const string SharedHooksPath = "hooks.json";

    private static readonly DateTimeOffset InstalledAt = new(2026, 11, 06, 9, 30, 00, TimeSpan.Zero);
    private static readonly string[] TwoBlockTargets = ["cursor", "codex"];
    private static readonly string[] ThreeBlockTargets = ["cursor", "codex", "factory"];

    /// <summary>
    /// Two global blocks that name the same path under different targets install together and
    /// land in one global receipt; adding a third block keeps all three. The identical path is
    /// the point: four formats write <c>hooks.json</c> beneath their own global root, so identity
    /// is the target beside the path, exactly as stored files are already identified.
    /// </summary>
    [Fact]
    public void GlobalInstallOfTwoThenThreeBlocksSharingOnePathRecordsEveryTargetInOneReceipt()
    {
        using TempDirectory fixture = new();
        string home = Path.Combine(fixture.Path, "home");
        Directory.CreateDirectory(home);
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore store = Store(fixture.Path);
        string targetRoot = Path.Combine(fixture.Path, "install-root");
        Directory.CreateDirectory(targetRoot);

        SquadDeploymentPlan two = SquadDeploymentPlan.CreateInstall(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt),
            globalRoots: globalRoots,
            blocks: [HooksBlock(SquadTarget.Cursor), HooksBlock(SquadTarget.Codex)]);

        new SquadTransaction(store).Execute(two);

        SquadReceipt afterTwo = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(targetRoot, SquadDeploymentScope.Global));
        Assert.Equal("kyber-squad.receipt/v3", afterTwo.Schema);
        Assert.Equal(SquadDeploymentScope.Global, afterTwo.Scope);
        AssertBlocks(afterTwo, TwoBlockTargets);
        AssertGlobalHookFile(globalRoots, SquadTarget.Cursor);
        AssertGlobalHookFile(globalRoots, SquadTarget.Codex);

        SquadDeploymentPlan three = SquadDeploymentPlan.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [],
            afterTwo,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddMinutes(1)),
            globalRoots: globalRoots,
            blocks:
            [
                HooksBlock(SquadTarget.Cursor),
                HooksBlock(SquadTarget.Codex),
                HooksBlock(SquadTarget.Factory),
            ]);

        new SquadTransaction(store).Execute(three);

        SquadReceipt afterThree = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(targetRoot, SquadDeploymentScope.Global));
        AssertBlocks(afterThree, ThreeBlockTargets);
        AssertGlobalHookFile(globalRoots, SquadTarget.Factory);
    }

    /// <summary>
    /// A global receipt the shipped 0.1.8-rc.20 binary wrote reads back with both of its blocks,
    /// their targets and their entry digests intact, and serializes to the same bytes. The second
    /// block is what an upgrade must not lose: rc.20 wrote one block per receipt, and the fix has
    /// to read many.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recipe, run with the shipped binary (<c>kyber-weave --version</c> printed
    /// <c>kyber-weave 0.1.8-rc.20</c> on 2026-11-06):
    /// </para>
    /// <code>
    /// H1=$(mktemp -d); env HOME="$H1" /Users/dave/.local/bin/kyber-weave arbiter install --target cursor
    /// H2=$(mktemp -d); env HOME="$H2" /Users/dave/.local/bin/kyber-weave arbiter install --target codex
    /// </code>
    /// <para>
    /// Each run wrote <c>$HOME/.kyber-weave/squad/global/squad.receipt.json</c>. The fixture below
    /// is <c>H1</c>'s receipt byte for byte, with <c>H2</c>'s single <c>blocks</c> element copied
    /// verbatim after <c>H1</c>'s — a genuine two-block receipt cannot exist, because the defect
    /// is what stops rc.20 from writing one. Nothing else in the document was edited. The homes
    /// were named so the recorded target root reads as a fixture path rather than a scratch one.
    /// </para>
    /// <para>
    /// The recorded target root is an absolute POSIX path, which the receipt contract accepts as
    /// fully qualified on the Linux and macOS legs this class runs on.
    /// </para>
    /// </remarks>
    [Fact]
    public void Rc20GlobalReceiptCarryingTwoBlocksReadsBackAndReserializesIdentically()
    {
        using TempDirectory fixture = new();
        SquadStateStore store = Store(fixture.Path);
        string json = ReadFixture("two-blocks.receipt.json");

        SquadReceipt receipt = store.DeserializeReceipt(json);

        Assert.Equal("kyber-squad.receipt/v3", receipt.Schema);
        Assert.Equal(SquadDeploymentScope.Global, receipt.Scope);
        Assert.Equal(SquadReceiptLayout.PerTargetRoots, receipt.Layout);
        AssertBlocks(receipt, TwoBlockTargets);

        SquadOwnedBlock cursor = receipt.Blocks[0];
        Assert.Equal(SharedHooksPath, cursor.RelativePath);
        Assert.True(cursor.CreatedFile, "rc.20 recorded that it created the cursor hook file.");
        Assert.All(
            cursor.Entries,
            entry => Assert.Equal(
                "5db0bf0b390001853e2b0d3daf3042716d8d93093c0e11b47c18681819eee3e7",
                entry.Sha256));
        Assert.Collection(
            cursor.Entries,
            entry => Assert.Equal("/hooks/preToolUse/0", entry.Container),
            entry => Assert.Equal("/hooks/postToolUse/0", entry.Container));

        SquadOwnedBlock codex = receipt.Blocks[1];
        Assert.Equal(SharedHooksPath, codex.RelativePath);
        Assert.True(codex.CreatedFile, "rc.20 recorded that it created the codex hook file.");
        Assert.All(
            codex.Entries,
            entry => Assert.Equal(
                "f548daa3559b6733a1763889eb729f8e50410b8968d7aa62bcd12aed3dcb72d6",
                entry.Sha256));
        Assert.Collection(
            codex.Entries,
            entry => Assert.Equal("/hooks/PreToolUse/0", entry.Container),
            entry => Assert.Equal("/hooks/PostToolUse/0", entry.Container));

        string serialized = store.SerializeReceipt(receipt);

        Assert.Equal(NormalizeLineEndings(json), NormalizeLineEndings(serialized));
    }

    private static void AssertBlocks(SquadReceipt receipt, IReadOnlyList<string> expectedTargets)
    {
        List<string> actualTargets = [.. receipt.Blocks.Select(block => block.Target)];
        Assert.Equal(expectedTargets, actualTargets);
        Assert.All(receipt.Blocks, block => Assert.Equal(SharedHooksPath, block.RelativePath));
        Assert.All(receipt.Blocks, block => Assert.NotEmpty(block.Entries));
    }

    private static void AssertGlobalHookFile(SquadGlobalRoots globalRoots, SquadTarget target)
    {
        string token = SquadTargetCatalog.GetToken(target);
        string path = Path.Combine(globalRoots.ResolveGlobalRoot(target), SharedHooksPath);
        Assert.True(File.Exists(path), $"Expected the {token} global hook file at '{path}'.");
        Assert.Contains(
            $"kyber-weave-arbiter hook --harness {token} --scope global",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    private static SquadRenderedBlock HooksBlock(SquadTarget target)
    {
        string token = SquadTargetCatalog.GetToken(target);
        string command = $"kyber-weave-arbiter hook --harness {token} --scope global";
        return target switch
        {
            SquadTarget.Cursor => new SquadRenderedBlock(
                token,
                SharedHooksPath,
                SquadHookBlockFormat.Cursor,
                [new SquadRenderedBlockEntry("preToolUse", CursorEntry(command))]),
            SquadTarget.Codex => new SquadRenderedBlock(
                token,
                SharedHooksPath,
                SquadHookBlockFormat.Codex,
                [new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(command))]),
            SquadTarget.Factory => new SquadRenderedBlock(
                token,
                SharedHooksPath,
                SquadHookBlockFormat.Factory,
                [new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(command))]),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Only the targets whose global hook file is named hooks.json are built here."),
        };
    }

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
            ["matcher"] = "Task",
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                }),
        };

    private static string ReadFixture(string name) =>
        File.ReadAllText(
            Path.Combine(RepositoryRoot(), FixtureRelativePath, name),
            Encoding.UTF8);

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
