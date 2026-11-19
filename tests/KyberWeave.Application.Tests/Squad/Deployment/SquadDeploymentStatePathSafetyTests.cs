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

    /// <summary>
    ///     The in-memory twin of the real-symlink install case: a directory link in the target tree that leaves
    ///     the root stops the plan before anything is written on the other side.
    /// </summary>
    [Fact]
    public void CreateInstallThroughDirectoryLinkLeavingTheRootIsRejectedInMemory()
    {
        DeploymentScope workspace = NewScope();
        string targetRoot = NewDirectory(Path.Combine(workspace.Path, "target"));
        string outside = NewDirectory(Path.Combine(workspace.Path, "outside"));
        string codex = NewDirectory(Path.Combine(targetRoot, ".codex"));
        Files.CreateDirectoryLink(
            Path.Combine(codex, "agents"),
            Path.GetRelativePath(codex, outside));

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            Planner.CreateInstall(
                targetRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/conductor.toml", "escape")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(".codex/agents/conductor.toml", exception.Message, StringComparison.Ordinal);
        Assert.Empty(CaptureTree(outside));
    }

    /// <summary>
    ///     The in-memory twin of the real-symlink uninstall case: a directory link in the target tree that leaves
    ///     the root stops the plan before anything outside it is deleted.
    /// </summary>
    [Fact]
    public void CreateUninstallThroughDirectoryLinkLeavingTheRootIsRejectedInMemory()
    {
        DeploymentScope workspace = NewScope();
        string targetRoot = NewDirectory(Path.Combine(workspace.Path, "target"));
        string outside = NewDirectory(Path.Combine(workspace.Path, "outside"));
        string codex = NewDirectory(Path.Combine(targetRoot, ".codex"));
        Files.CreateDirectoryLink(
            Path.Combine(codex, "agents"),
            Path.GetRelativePath(codex, outside));
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("outside"),
                "codex",
                false));

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            Planner.CreateUninstall(
                targetRoot,
                SquadDeploymentScope.Project,
                receipt));

        Assert.Contains(".codex/agents/conductor.toml", exception.Message, StringComparison.Ordinal);
        Assert.Empty(CaptureTree(outside));
    }

    /// <summary>
    ///     The in-memory twin of the on-disk spelling case: the resolved identity carries the spelling the
    ///     entry was created with, and a case-distinct name is a distinct root.
    /// </summary>
    [Fact]
    public void ResolvePhysicalRootUsesTheCreatedSpellingInMemory()
    {
        string actualRoot = NewDirectory(Path.Combine(Root, "ActualSpelling"));
        string caseVariant = NewDirectory(Path.Combine(Root, "actualspelling"));
        SquadPhysicalRootIdentity actualIdentity =
            SquadPhysicalRootIdentities.Resolve(actualRoot, Semantics);
        SquadPhysicalRootIdentity distinctIdentity =
            SquadPhysicalRootIdentities.Resolve(caseVariant, Semantics);

        Assert.Equal("ActualSpelling", Path.GetFileName(actualIdentity.PhysicalPath));
        Assert.NotEqual(actualIdentity.PhysicalPath, distinctIdentity.PhysicalPath);
        Assert.NotEqual(actualIdentity.Key, distinctIdentity.Key);
    }

    /// <summary>
    ///     The in-memory twin of the home-store symlink escape case: a link planted inside the home store that
    ///     leads out of it stops every home-store resolution it sits on.
    /// </summary>
    [Fact]
    public void ResolveHomeStoreDirectoryLinksThatLeaveTheHomeAreRejectedInMemory()
    {
        const string canaryRelativePath = "outside-canary.txt";
        (string Label, string LinkRelativePath, bool JournalOnPath)[] plants =
        [
            ("kyber-weave", ".kyber-weave", true),
            ("squad", Path.Combine(".kyber-weave", "squad"), false)
        ];

        foreach ((string label, string linkRelativePath, bool journalOnPath) in plants)
        {
            DeploymentScope workspace = NewScope();
            string project = NewDirectory(Path.Combine(workspace.Path, "home-escape-project"));
            string applicationData = Path.Combine(workspace.Path, $"home-escape-{label}");
            string home = NewDirectory(Path.Combine(applicationData, "home"));
            string outside = NewDirectory(Path.Combine(workspace.Path, $"home-outside-{label}"));
            string linkPath = Path.Combine(home, linkRelativePath);
            string linkParent = Path.GetDirectoryName(linkPath)!;
            Files.CreateDirectory(linkParent);
            Write(outside, canaryRelativePath, "outside canary");
            SortedDictionary<string, TreeEntry> outsideBefore = CaptureTree(outside);
            Files.CreateDirectoryLink(
                linkPath,
                Path.GetRelativePath(linkParent, outside));

            SquadStateStore store = StoreAt(applicationData);
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveReceiptPath(project, SquadDeploymentScope.Project));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveLockPath(project, SquadDeploymentScope.Project));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveReceiptPath(project, SquadDeploymentScope.Global));
            Assert.Throws<SquadPathContainmentException>(() =>
                store.ResolveLockPath(project, SquadDeploymentScope.Global));
            if (journalOnPath)
                Assert.Throws<SquadPathContainmentException>(() =>
                    store.ResolveTransactionDirectory(project, SquadDeploymentScope.Global));

            AssertTreesEqual(outsideBefore, CaptureTree(outside));
        }
    }

    /// <summary>
    ///     The in-memory twin of the middle-segment case: a case-insensitive volume converges on the created
    ///     spelling, and a case-sensitive one keeps the caller's spelling for the segment that names nothing.
    /// </summary>
    [Fact]
    public void CanonicalizeUsesTheCreatedSpellingOfAMiddleSegmentInMemory()
    {
        string onDiskMiddle = NewDirectory(Path.Combine(Root, "MiddleSegment"));
        Files.WriteAllText(Path.Combine(onDiskMiddle, "leaf.md"), "leaf");
        string variantPath = Path.Combine(Root, "middlesegment", "leaf.md");

        string caseSensitive = Semantics.Canonicalize(variantPath);

        Assert.Equal(
            Path.Combine(Semantics.Canonicalize(Root), "middlesegment", "leaf.md"),
            caseSensitive);

        InMemorySquadState insensitive = NewState(caseSensitive: false);
        string insensitiveMiddle = Path.Combine(insensitive.Files.Root, "MiddleSegment");
        insensitive.Files.CreateDirectory(insensitiveMiddle);
        insensitive.Files.WriteAllText(Path.Combine(insensitiveMiddle, "leaf.md"), "leaf");

        string converged = insensitive.Semantics.Canonicalize(
            Path.Combine(insensitive.Files.Root, "middlesegment", "leaf.md"));

        Assert.Equal(
            Path.Combine(insensitive.Semantics.Canonicalize(insensitive.Files.Root), "MiddleSegment", "leaf.md"),
            converged);
    }

    /// <summary>
    ///     The in-memory twin of the symlinked-segment case: a link inside the root is followed for the
    ///     canonical spelling and honoured by containment, so one pointing outside cannot carry a path in.
    /// </summary>
    [Fact]
    public void CanonicalizeFollowsADirectoryLinkAndContainmentHonoursItInMemory()
    {
        DeploymentScope workspace = NewScope();
        DeploymentScope outsideScope = NewScope();
        string realDirectory = NewDirectory(Path.Combine(workspace.Path, "real"));
        Files.WriteAllText(Path.Combine(realDirectory, "leaf.md"), "leaf");
        string insideLink = Path.Combine(workspace.Path, "inside-link");
        Files.CreateDirectoryLink(
            insideLink,
            Path.GetRelativePath(workspace.Path, realDirectory));
        string outside = outsideScope.Path;
        Files.WriteAllText(Path.Combine(outside, "leaf.md"), "outside");
        string outsideLink = Path.Combine(workspace.Path, "outside-link");
        Files.CreateDirectoryLink(
            outsideLink,
            Path.GetRelativePath(workspace.Path, outside));
        string canonicalRealLeaf =
            Path.Combine(Semantics.Canonicalize(realDirectory), "leaf.md");

        Assert.Equal(
            canonicalRealLeaf,
            Semantics.Canonicalize(Path.Combine(insideLink, "leaf.md")));
        Assert.True(Semantics.IsWithin(workspace.Path, Path.Combine(insideLink, "leaf.md")));
        Assert.False(Semantics.IsWithin(workspace.Path, Path.Combine(outsideLink, "leaf.md")));
    }

    /// <summary>
    ///     The in-memory twin of the replaced-intermediate-directory case: replacing a directory with a link
    ///     between two checks changes the second answer.
    /// </summary>
    [Fact]
    public void ContainmentRefusesADirectoryLinkThatReplacedAnIntermediateDirectoryInMemory()
    {
        DeploymentScope workspace = NewScope();
        DeploymentScope outsideScope = NewScope();
        string middle = NewDirectory(Path.Combine(workspace.Path, "middle"));
        string leaf = Path.Combine(middle, "leaf.md");
        Files.WriteAllText(leaf, "leaf");
        string outside = outsideScope.Path;
        Files.WriteAllText(Path.Combine(outside, "leaf.md"), "outside");

        Assert.True(Semantics.IsWithin(workspace.Path, leaf));

        Files.DeleteTreeNoFollow(middle);
        Files.CreateDirectoryLink(
            middle,
            Path.GetRelativePath(workspace.Path, outside));

        Assert.False(Semantics.IsWithin(workspace.Path, leaf));
        Assert.Equal(
            Path.Combine(Semantics.Canonicalize(outside), "leaf.md"),
            Semantics.Canonicalize(leaf));
    }

    /// <summary>
    ///     The in-memory twin of the Windows per-directory case probe: a directory whose names are
    ///     case-sensitive keeps two sibling roots distinct, so a link from one into the other escapes it.
    /// </summary>
    [Fact]
    public void ResolveCaseDistinctSiblingsAndTheirLinkEscapeOnACaseSensitiveDirectoryInMemory()
    {
        InMemorySquadState state = NewState(caseSensitive: false);
        string parent = Path.Combine(state.Files.Root, "case-sensitive-parent");
        state.Files.CreateDirectory(parent, caseSensitiveNames: true);
        string upperRoot = Path.Combine(parent, "Root");
        string lowerRoot = Path.Combine(parent, "root");
        state.Files.CreateDirectory(upperRoot);
        state.Files.CreateDirectory(lowerRoot);

        Assert.NotEqual(
            state.Semantics.Canonicalize(upperRoot),
            state.Semantics.Canonicalize(lowerRoot));

        const string canaryRelativePath = "outside-canary.txt";
        state.Files.WriteAllText(Path.Combine(lowerRoot, canaryRelativePath), "outside canary");
        SortedDictionary<string, TreeEntry> outsideBefore = CaptureTree(state.Files, lowerRoot);
        state.Files.CreateDirectoryLink(
            Path.Combine(upperRoot, "escape"),
            Path.Combine("..", Path.GetFileName(lowerRoot)));

        Assert.Throws<SquadPathContainmentException>(() =>
            SquadPathPolicy.ResolveFile(
                upperRoot,
                $"escape/{canaryRelativePath}",
                state.Semantics));
        Assert.Throws<SquadPathContainmentException>(() =>
            state.Planner.CreateInstall(
                upperRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered("escape/generated.md", "must remain contained")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));
        AssertTreesEqual(outsideBefore, CaptureTree(state.Files, lowerRoot));
    }
}
