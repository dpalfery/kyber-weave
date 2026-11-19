using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Deployment;

/// <summary>
/// Deployment-state contracts: path policy, case semantics and containment. Split from the one class that used to hold
/// all of them, so xUnit runs the themes on different cores. The cases that need a real volume, a real symbolic link
/// or a Windows per-directory case probe stay in the integration project.
/// </summary>
public sealed class SquadDeploymentStatePathSafetyTests : InMemoryDeploymentStateTestBase
{
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Generates a deliberately case-varied path fixture; the lowercasing "
                        + "is the test input, not a normalization.")]
    public static IEnumerable<object[]> PortableInvalidPaths()
    {
        foreach (char forbidden in "<>:\"|?*")
            yield return [$".codex/agents/bad{forbidden}name.md"];

        yield return [".codex//agents/repeated-separator.md"];
        yield return [".codex\\agents\\noncanonical-separators.md"];
        yield return [".codex/agents/trailing-dot."];
        yield return [".codex/agents/trailing-space "];
        yield return [".codex/agents/e\u0301.md"];

        for (int value = 0; value <= 0x1f; value++)
            yield return [$".codex/agents/control{(char)value}name.md"];
        for (int value = 0x7f; value <= 0x9f; value++)
            yield return [$".codex/agents/control{(char)value}name.md"];

        string[] deviceNames =
        [
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3"
        ];
        foreach (string deviceName in deviceNames)
        {
            yield return [$".codex/agents/{deviceName}/SKILL.md"];
            yield return [$".codex/agents/{deviceName.ToLowerInvariant()}.txt"];
            yield return [$".codex/agents/{ToggleAsciiCase(deviceName)}.agent.md"];
        }
    }

    [Theory]
    [InlineData(".codex/agents/Reviewer.toml", ".codex/agents/reviewer.toml")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml.")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml ")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents./reviewer.toml")]
    public void CreateInstallPortablePathAliasesAreRejectedBeforeWrites(
        string firstPath,
        string aliasPath)
    {
        DeploymentScope fixture = NewScope();
        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Planner.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "second")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains("portable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aliasPath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData(".codex/agents/Reviewer.toml", ".codex/agents/reviewer.toml")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml.")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml ")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents./reviewer.toml")]
    public void CreateUninstallReceiptPortablePathAliasesAreRejected(
        string firstPath,
        string aliasPath)
    {
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("second"), "codex", false));

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Planner.CreateUninstall(
                Root,
                SquadDeploymentScope.Project,
                receipt));

        Assert.Contains("portable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aliasPath, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData(".codex/../../outside.md")]
    public void CreateInstallLexicalPathEscapeIsRejectedBeforeWrites(string relativePath)
    {
        DeploymentScope fixture = NewScope();
        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            Planner.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(relativePath, "escape")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(CaptureTree(fixture.Path));
    }

    [Theory]
    [MemberData(nameof(PortableInvalidPaths))]
    public void CreateLifecyclePlanWindowsForbiddenDeviceControlAndUnicodeAliasesAreRejected(
        string invalidPath)
    {
        DeploymentScope fixture = NewScope();
        SquadOwnedFile owned = new(invalidPath, Digest("owned"), "codex", false);
        SquadReceipt receipt = Receipt(owned);

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            Planner.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(invalidPath, "generated")],
                [],
                false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            Planner.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(invalidPath, "updated")],
                receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            Planner.CreateUninstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData(".codex/agents/\u00e9.md", ".codex/agents/e\u0301.md")]
    [InlineData(".codex/agents/Reviewer.md", ".codex/agents/reviewer.md")]
    public void CreateLifecyclePlanUnicodeAndCaseOnlyPortablePairsAreRejected(
        string firstPath,
        string aliasPath)
    {
        DeploymentScope fixture = NewScope();
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("alias"), "codex", false));

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            Planner.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                [],
                false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            Planner.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            Planner.CreateUninstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("wrong-area")]
    [InlineData("non-ancestor")]
    [InlineData("duplicate")]
    [InlineData("unordered")]
    [InlineData("absolute")]
    [InlineData("traversal")]
    [InlineData("sibling")]
    [InlineData("outside-root")]
    [InlineData("legacy-count")]
    [InlineData("negative-count")]
    [InlineData("maximum-count")]
    public void RecoverCorruptCreatedDirectoryAuthorityCannotEscapeBoundRoots(string corruption)
    {
        DeploymentScope workspace = NewScope();
        string targetRoot = NewDirectory(Path.Combine(workspace.Path, "target"));
        string applicationData = Path.Combine(workspace.Path, "application-data");
        Write(workspace.Path, "target-canary.txt", "target boundary canary");
        Write(workspace.Path, "application-canary.txt", "application boundary canary");
        Write(workspace.Path, "beside-target/canary.txt", "sibling canary");
        SquadStateStore store = StoreAt(applicationData);
        SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
        CheckpointFailingObserver observer = new(
            Files,
            workspace.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            TransactionAt(applicationData, observer).Execute(plan));
        RestoreTree(
            workspace.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string intentPath = Path.Combine(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        Assert.False(intent.ContainsKey("missingTransactionDirectories"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        Assert.NotEmpty(createdDirectories);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);
        CorruptCreatedDirectoryAuthority(intent, createdDirectories, workspace.Path, corruption);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(workspace.Path);

        Exception? exception = Record.Exception(() => TransactionAt(applicationData).Recover(
            targetRoot,
            SquadDeploymentScope.Global));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(workspace.Path));
        Assert.Equal("target boundary canary", Read(workspace.Path, "target-canary.txt"));
        Assert.Equal("application boundary canary", Read(workspace.Path, "application-canary.txt"));
        Assert.Equal("sibling canary", Read(workspace.Path, "beside-target/canary.txt"));
    }

    /// <summary>
    ///     The trailing segments of a path need not exist: resolution canonicalises the prefix that is
    ///     on disk and keeps the caller's spelling for the rest, which is what lets an install plan a
    ///     file that is not there yet.
    /// </summary>
    [Fact]
    public void CanonicalizeKeepsANotYetExistingTailInItsGivenSpelling()
    {
        string existing = NewDirectory(Path.Combine(Root, "Existing"));

        string canonicalExisting = Semantics.Canonicalize(existing);

        string canonical = Semantics.Canonicalize(
            Path.Combine(Root, "Existing", "new", "deeper", "leaf.md"));

        Assert.Equal(Path.Combine(canonicalExisting, "new", "deeper", "leaf.md"), canonical);
    }

    /// <summary>Builds a global install plan over the in-memory planner.</summary>
    /// <param name="targetRoot">The target root.</param>
    /// <returns>The plan.</returns>
    private SquadDeploymentPlan CreateGlobalInstallPlan(string targetRoot) =>
        Planner.CreateInstall(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "global conductor")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
}
