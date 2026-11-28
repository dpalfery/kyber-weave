using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Deployment-state contracts: path policy, containment and canonical spelling on a real volume. The rest of the theme
/// runs over the in-memory fakes in <c>KyberWeave.Application.Tests</c>; these cases stay because only a real symbolic
/// link, a real volume's case rule or a Windows per-directory case probe can produce their subject.
/// </summary>
public sealed class SquadDeploymentStatePathSafetyTests(ITestOutputHelper output) : SquadDeploymentStateTestBase
{
    [Fact]
    public void CreateInstallOnRealDiskRejectsPathThroughSymlinkLeavingRoot()
    {
        using TempDirectory fixture = new();
        using TempDirectory outside = new();
        string codex = Path.Combine(fixture.Path, ".codex");
        Directory.CreateDirectory(codex);
        Directory.CreateSymbolicLink(Path.Combine(codex, "agents"), outside.Path);

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            RealSquadDeployment.Planner.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/conductor.toml", "escape")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public void CreateUninstallOnRealDiskRejectsPathThroughSymlinkLeavingRoot()
    {
        using TempDirectory fixture = new();
        using TempDirectory outside = new();
        string codex = Path.Combine(fixture.Path, ".codex");
        Directory.CreateDirectory(codex);
        Directory.CreateSymbolicLink(Path.Combine(codex, "agents"), outside.Path);
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("outside"),
                "codex",
                false));

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            RealSquadDeployment.Planner.CreateUninstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                receipt));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePhysicalRootUsesActualOnDiskSpellingAndDirectoryCaseSemantics()
    {
        using TempDirectory fixture = new();
        string actualRoot = Path.Combine(fixture.Path, "ActualSpelling");
        string caseVariant = Path.Combine(fixture.Path, "actualspelling");
        Directory.CreateDirectory(actualRoot);
        DirectoryInfo actualEntry = new DirectoryInfo(fixture.Path).EnumerateDirectories()
            .Single(directory => string.Equals(
                directory.Name,
                "ActualSpelling",
                StringComparison.Ordinal));
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentities.Resolve(actualRoot, SquadFileSystemPathSemantics.Instance);

        Assert.Equal(
            actualEntry.Name,
            Path.GetFileName(actualIdentity.PhysicalPath));
        if (Directory.Exists(caseVariant))
        {
            Assert.Equal(
                actualIdentity,
                SquadPhysicalRootIdentities.Resolve(caseVariant, SquadFileSystemPathSemantics.Instance));
            return;
        }

        Directory.CreateDirectory(caseVariant);
        SquadPhysicalRootIdentity distinctIdentity = SquadPhysicalRootIdentities.Resolve(caseVariant, SquadFileSystemPathSemantics.Instance);
        Assert.NotEqual(actualIdentity.PhysicalPath, distinctIdentity.PhysicalPath);
        Assert.NotEqual(actualIdentity.Key, distinctIdentity.Key);
    }

    [Fact]
    public async Task ResolvePhysicalRootWindowsCaseSensitiveSiblingsRemainDistinctOnRealDisk()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine(
                "Windows per-directory case-sensitivity branch is exercised only on Windows; " +
                "the host volume semantics remain covered by the paired cross-platform probes.");
            return;
        }

        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        string caseSensitiveParent = Path.Combine(fixturePath, "case-sensitive-parent");
        Directory.CreateDirectory(caseSensitiveParent);
        (bool enabled, string diagnostic) = await TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent);
        if (!enabled)
        {
            output.WriteLine(
                $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
            return;
        }

        string upperRoot = Path.Combine(caseSensitiveParent, "Root");
        string lowerRoot = Path.Combine(caseSensitiveParent, "root");
        Directory.CreateDirectory(upperRoot);
        Directory.CreateDirectory(lowerRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentities.Resolve(upperRoot, SquadFileSystemPathSemantics.Instance);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentities.Resolve(lowerRoot, SquadFileSystemPathSemantics.Instance);
        SquadStateStore store = Store(Path.Combine(fixturePath, "application-data"));

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        // Global state is home-anchored, so case-distinct roots share one global receipt. The
        // project leases below still separate them, which is the part this test pins.
        Assert.Equal(
            store.ResolveReceiptPath(upperRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(lowerRoot, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = RealSquadDeployment.Transaction(store, blocker);
        SquadDeploymentPlan upperPlan = RealSquadDeployment.Planner.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Task active = BlockedTransaction.Start(() => activeTransaction.Execute(upperPlan));
        BlockedTransaction.WaitForStep(
            blocker.Reached,
            active,
            "The first Windows case-sensitive sibling did not acquire its lease.");
        Exception? siblingFailure;
        try
        {
            SquadTransaction siblingTransaction = RealSquadDeployment.Transaction(store);
            SquadDeploymentPlan lowerPlan = RealSquadDeployment.Planner.CreateInstall(
                lowerRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/lower.toml", "lower")],
                [],
                false,
                new FixedTimeProvider(InstalledAt));
            siblingFailure = Record.Exception(() => siblingTransaction.Execute(lowerPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await active;
        Assert.Null(siblingFailure);
    }

    [Fact]
    public async Task ResolveFileOnWindowsCaseSensitiveRealDiskRejectsSiblingSymlinkEscape()
    {
        using TempDirectory fixture = new();
        AssertHomeStoreSymlinkEscapesAreRejected(fixture.Path);

        string caseSensitiveParent = Path.Combine(fixture.Path, "case-sensitive-parent");
        Directory.CreateDirectory(caseSensitiveParent);
        if (OperatingSystem.IsWindows())
        {
            (bool enabled, string diagnostic) = await TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent);
            if (!enabled)
            {
                output.WriteLine(
                    $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
                return;
            }
        }

        string upperRoot = Path.Combine(caseSensitiveParent, "Root");
        string lowerRoot = Path.Combine(caseSensitiveParent, "root");
        Directory.CreateDirectory(upperRoot);
        if (Directory.Exists(lowerRoot))
        {
            output.WriteLine(
                "The temporary volume is case-insensitive; the case-distinct sibling branch " +
                "is covered by the Linux and capable Windows filesystem-contract runners.");
            return;
        }

        Directory.CreateDirectory(lowerRoot);
        const string canaryRelativePath = "outside-canary.txt";
        Write(lowerRoot, canaryRelativePath, "outside canary");
        SortedDictionary<string, TreeEntry> outsideBefore = CaptureTree(lowerRoot);

        string directAlias = Path.Combine(upperRoot, "escape");
        Directory.CreateSymbolicLink(directAlias, lowerRoot);
        Assert.Throws<SquadPathContainmentException>(() =>
            SquadPathPolicy.ResolveFile(upperRoot, $"escape/{canaryRelativePath}", SquadFileSystemPathSemantics.Instance));

        Assert.Throws<SquadPathContainmentException>(() =>
            RealSquadDeployment.Planner.CreateInstall(
                upperRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered("escape/generated.md", "must remain contained")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Directory.Delete(directAlias);
        Directory.CreateDirectory(Path.Combine(upperRoot, ".kyber-weave"));
        SquadDeploymentPlan plan = RealSquadDeployment.Planner.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "contained")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        Directory.Delete(Path.Combine(upperRoot, ".kyber-weave"));
        Directory.CreateSymbolicLink(Path.Combine(upperRoot, ".kyber-weave"), lowerRoot);

        // Project state lives under the home store, so the in-root link is reached only through the
        // project transaction directory: the journal and its work directory beside the root.
        SquadStateStore projectStore = Store(Path.Combine(fixture.Path, "application-data"));
        Assert.Throws<SquadPathContainmentException>(() =>
            Transaction(upperRoot).Execute(plan));
        Assert.Throws<SquadPathContainmentException>(() =>
            projectStore.ResolveTransactionDirectory(upperRoot, SquadDeploymentScope.Project));
        Assert.Throws<SquadPathContainmentException>(() =>
            projectStore.ResolveTransactionWorkDirectory(upperRoot, SquadDeploymentScope.Project));
        Assert.Throws<SquadPathContainmentException>(() =>
            Transaction(upperRoot).Recover(upperRoot, SquadDeploymentScope.Project));
        AssertTreesEqual(outsideBefore, CaptureTree(lowerRoot));
    }

    /// <summary>
    ///     A difference in case can appear in the middle of a path, not only in its last segment. Where
    ///     the variant spelling names the directory that is on disk, the result carries the on-disk
    ///     spelling; where it names nothing, only the existing prefix is corrected.
    /// </summary>
    [Fact]
    public void CanonicalizeUsesTheOnDiskSpellingOfAMiddleSegmentOnARealVolume()
    {
        using TempDirectory fixture = new();
        string onDiskMiddle = Path.Combine(fixture.Path, "MiddleSegment");
        string onDiskLeaf = Path.Combine(onDiskMiddle, "leaf.md");
        Directory.CreateDirectory(onDiskMiddle);
        File.WriteAllText(onDiskLeaf, "leaf");
        string variantPath = Path.Combine(fixture.Path, "middlesegment", "leaf.md");
        string canonicalRoot = SquadFileSystemPathSemantics.Instance.Canonicalize(fixture.Path);

        string canonical = SquadFileSystemPathSemantics.Instance.Canonicalize(variantPath);

        if (Directory.Exists(Path.Combine(fixture.Path, "middlesegment")))
        {
            // Case-insensitive volume: the variant names the entry on disk, so it converges.
            Assert.Equal(
                Path.Combine(canonicalRoot, "MiddleSegment", "leaf.md"),
                canonical);
            Assert.Equal(
                SquadFileSystemPathSemantics.Instance.Canonicalize(onDiskLeaf),
                canonical);
            return;
        }

        // Case-sensitive volume: the variant names nothing, so only the existing prefix is corrected
        // and the missing tail keeps the caller's spelling.
        Assert.Equal(
            Path.Combine(canonicalRoot, "middlesegment", "leaf.md"),
            canonical);
    }

    /// <summary>
    ///     A symlink in the middle of a path is followed for the canonical spelling and honoured by the
    ///     containment decisions, so a link pointing outside the root cannot carry a path inside it.
    /// </summary>
    [Fact]
    public void CanonicalizeFollowsARealSymlinkedSegmentAndContainmentHonoursIt()
    {
        using TempDirectory fixture = new();
        using TempDirectory outside = new();
        string realDirectory = Path.Combine(fixture.Path, "real");
        string realLeaf = Path.Combine(realDirectory, "leaf.md");
        Directory.CreateDirectory(realDirectory);
        File.WriteAllText(realLeaf, "leaf");
        string insideLink = Path.Combine(fixture.Path, "inside-link");
        Directory.CreateSymbolicLink(insideLink, realDirectory);
        string outsideLeaf = Path.Combine(outside.Path, "leaf.md");
        File.WriteAllText(outsideLeaf, "outside");
        string outsideLink = Path.Combine(fixture.Path, "outside-link");
        Directory.CreateSymbolicLink(outsideLink, outside.Path);
        string canonicalRealLeaf =
            Path.Combine(SquadFileSystemPathSemantics.Instance.Canonicalize(realDirectory), "leaf.md");

        Assert.Equal(
            canonicalRealLeaf,
            SquadFileSystemPathSemantics.Instance.Canonicalize(Path.Combine(insideLink, "leaf.md")));
        Assert.True(
            SquadFileSystemPathSemantics.Instance.IsWithin(
                fixture.Path,
                Path.Combine(insideLink, "leaf.md")));
        Assert.False(
            SquadFileSystemPathSemantics.Instance.IsWithin(
                fixture.Path,
                Path.Combine(outsideLink, "leaf.md")));
    }

    /// <summary>
    ///     Each check answers from the filesystem as it is at that moment: replacing a directory with a
    ///     symlink between two checks must change the answer the second time.
    /// </summary>
    [Fact]
    public void ContainmentRefusesARealSymlinkThatReplacedAnIntermediateDirectoryBetweenChecks()
    {
        using TempDirectory fixture = new();
        using TempDirectory outside = new();
        string middle = Path.Combine(fixture.Path, "middle");
        string leaf = Path.Combine(middle, "leaf.md");
        Directory.CreateDirectory(middle);
        File.WriteAllText(leaf, "leaf");
        File.WriteAllText(Path.Combine(outside.Path, "leaf.md"), "outside");

        Assert.True(SquadFileSystemPathSemantics.Instance.IsWithin(fixture.Path, leaf));

        Directory.Delete(middle, recursive: true);
        Directory.CreateSymbolicLink(middle, outside.Path);

        Assert.False(SquadFileSystemPathSemantics.Instance.IsWithin(fixture.Path, leaf));
        Assert.Equal(
            Path.Combine(SquadFileSystemPathSemantics.Instance.Canonicalize(outside.Path), "leaf.md"),
            SquadFileSystemPathSemantics.Instance.Canonicalize(leaf));
    }
}
