using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Deployment-state contracts: path policy, case semantics and containment. Split from the one class that used to hold all of
/// them, so xUnit runs the themes on different cores.
/// </summary>
public sealed class SquadDeploymentStatePathSafetyTests(ITestOutputHelper output) : SquadDeploymentStateTestBase
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

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
        using TempDirectory fixture = new();

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            RealSquadDeployment.Planner.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "second")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains("portable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aliasPath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
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
        using TempDirectory fixture = new();
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("second"), "codex", false));

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            RealSquadDeployment.Planner.CreateUninstall(
                fixture.Path,
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
        using TempDirectory fixture = new();

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            RealSquadDeployment.Planner.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(relativePath, "escape")],
                [],
                false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    [Fact]
    public void CreateInstallPathThatResolvesThroughSymlinkOutsideRootIsRejected()
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
    public void CreateUninstallReceiptPathThatResolvesOutsideRootIsRejected()
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

    [Theory]
    [MemberData(nameof(PortableInvalidPaths))]
    public void CreateLifecyclePlanWindowsForbiddenDeviceControlAndUnicodeAliasesAreRejected(
        string invalidPath)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        SquadOwnedFile owned = new(invalidPath, Digest("owned"), "codex", false);
        SquadReceipt receipt = Receipt(owned);

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            RealSquadDeployment.Planner.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(invalidPath, "generated")],
                [],
                false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            RealSquadDeployment.Planner.CreateUpdate(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(invalidPath, "updated")],
                receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            RealSquadDeployment.Planner.CreateUninstall(
                fixturePath,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixturePath));
    }

    [Theory]
    [InlineData(".codex/agents/\u00e9.md", ".codex/agents/e\u0301.md")]
    [InlineData(".codex/agents/Reviewer.md", ".codex/agents/reviewer.md")]
    public void CreateLifecyclePlanUnicodeAndCaseOnlyPortablePairsAreRejected(
        string firstPath,
        string aliasPath)
    {
        using TempDirectory fixture = new();
        string fixturePath = fixture.Path;
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("alias"), "codex", false));

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            RealSquadDeployment.Planner.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                [],
                false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            RealSquadDeployment.Planner.CreateUpdate(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                receipt,
                [],
                false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            RealSquadDeployment.Planner.CreateUninstall(
                fixturePath,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixturePath));
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
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Write(fixture.Path, "target-canary.txt", "target boundary canary");
        Write(fixture.Path, "application-canary.txt", "application boundary canary");
        Write(fixture.Path, "beside-target/canary.txt", "sibling canary");
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
        CheckpointFailingObserver observer = new(
            fixture.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            new SquadTransaction(store, observer).Execute(plan));
        RestoreTree(
            fixture.Path,
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
        CorruptCreatedDirectoryAuthority(intent, createdDirectories, fixture.Path, corruption);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixture.Path);

        Exception? exception = Record.Exception(() => new SquadTransaction(store).Recover(
            targetRoot,
            SquadDeploymentScope.Global));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixture.Path));
        Assert.Equal("target boundary canary", Read(fixture.Path, "target-canary.txt"));
        Assert.Equal("application boundary canary", Read(fixture.Path, "application-canary.txt"));
        Assert.Equal("sibling canary", Read(fixture.Path, "beside-target/canary.txt"));
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
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentity.Resolve(actualRoot, SquadFileSystemPathSemantics.Instance);

        Assert.Equal(
            actualEntry.Name,
            Path.GetFileName(actualIdentity.PhysicalPath));
        if (Directory.Exists(caseVariant))
        {
            Assert.Equal(
                actualIdentity,
                SquadPhysicalRootIdentity.Resolve(caseVariant, SquadFileSystemPathSemantics.Instance));
            return;
        }

        Directory.CreateDirectory(caseVariant);
        SquadPhysicalRootIdentity distinctIdentity = SquadPhysicalRootIdentity.Resolve(caseVariant, SquadFileSystemPathSemantics.Instance);
        Assert.NotEqual(actualIdentity.PhysicalPath, distinctIdentity.PhysicalPath);
        Assert.NotEqual(actualIdentity.Key, distinctIdentity.Key);
    }

    [Fact]
    public void ResolvePhysicalRootComparisonPolicyIsNotOperatingSystemWideCaseFolding()
    {
        string identitySource = Corpus.ReadText(
            "src/KyberWeave.Domain/Squad/Deployment/SquadPhysicalRootIdentity.cs");
        string transactionSource = Corpus.ReadText(
            "src/KyberWeave.Data/Squad/Deployment/SquadTransaction.cs");

        Assert.DoesNotContain("physicalPath.ToUpperInvariant()", identitySource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
            transactionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvePhysicalRootWindowsCaseSensitiveSiblingsRemainDistinct()
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
        if (!TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent, out string diagnostic))
        {
            output.WriteLine(
                $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
            return;
        }

        string upperRoot = Path.Combine(caseSensitiveParent, "Root");
        string lowerRoot = Path.Combine(caseSensitiveParent, "root");
        Directory.CreateDirectory(upperRoot);
        Directory.CreateDirectory(lowerRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentity.Resolve(upperRoot, SquadFileSystemPathSemantics.Instance);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentity.Resolve(lowerRoot, SquadFileSystemPathSemantics.Instance);
        SquadStateStore store = Store(Path.Combine(fixturePath, "application-data"));

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        // Global state is home-anchored, so case-distinct roots share one global receipt. The
        // project leases below still separate them, which is the part this test pins.
        Assert.Equal(
            store.ResolveReceiptPath(upperRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(lowerRoot, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = new(store, blocker);
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
            SquadTransaction siblingTransaction = new(store);
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
    public void ResolveFileWindowsCaseSensitiveSiblingSymlinkEscapeIsRejected()
    {
        using TempDirectory fixture = new();
        AssertHomeStoreSymlinkEscapesAreRejected(fixture.Path);

        string caseSensitiveParent = Path.Combine(fixture.Path, "case-sensitive-parent");
        Directory.CreateDirectory(caseSensitiveParent);
        if (OperatingSystem.IsWindows() &&
            !TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent, out string diagnostic))
        {
            output.WriteLine(
                $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
            return;
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

    [Fact]
    public void AllContainmentHelpersUseCanonicalFileSystemSemantics()
    {
        string[] deploymentSources =
        [
            .. Corpus.EnumerateFiles("src", "*.cs")
                .Where(path => path.Contains("Squad", StringComparison.Ordinal)
                               && path.Split('/').Contains("Deployment", StringComparer.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
        const string semanticsPath = "src/KyberWeave.Data/Squad/Deployment/SquadFileSystemPathSemantics.cs";

        Assert.True(
            Corpus.Exists(semanticsPath),
            "Containment must be centralized in SquadFileSystemPathSemantics.");

        List<string> offenders = new();
        foreach (string sourcePath in deploymentSources)
        {
            if (string.Equals(sourcePath, semanticsPath, StringComparison.Ordinal))
                continue;

            string source = Corpus.ReadText(sourcePath);
            if (source.Contains("private static StringComparison PathComparison", StringComparison.Ordinal) ||
                source.Contains("private static bool IsWithin(", StringComparison.Ordinal) ||
                source.Contains("private static bool PathsEqual(", StringComparison.Ordinal) ||
                source.Contains(
                    "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
                    StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(sourcePath));
        }

        Assert.True(
            offenders.Count == 0,
            "Deployment containment helpers must delegate to " +
            $"SquadFileSystemPathSemantics: {string.Join(", ", offenders)}");
    }

    /// <summary>
    ///     A difference in case can appear in the middle of a path, not only in its last segment. Where
    ///     the variant spelling names the directory that is on disk, the result carries the on-disk
    ///     spelling; where it names nothing, only the existing prefix is corrected.
    /// </summary>
    [Fact]
    public void CanonicalizeUsesTheOnDiskSpellingOfAMiddleSegment()
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
    public void CanonicalizeFollowsASymlinkedSegmentAndContainmentHonoursIt()
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
    ///     The trailing segments of a path need not exist: resolution canonicalises the prefix that is
    ///     on disk and keeps the caller's spelling for the rest, which is what lets an install plan a
    ///     file that is not there yet.
    /// </summary>
    [Fact]
    public void CanonicalizeKeepsANotYetExistingTailInItsGivenSpelling()
    {
        using TempDirectory fixture = new();
        string existing = Path.Combine(fixture.Path, "Existing");
        Directory.CreateDirectory(existing);

        string canonicalExisting = SquadFileSystemPathSemantics.Instance.Canonicalize(existing);

        string canonical = SquadFileSystemPathSemantics.Instance.Canonicalize(
            Path.Combine(fixture.Path, "Existing", "new", "deeper", "leaf.md"));

        Assert.Equal(Path.Combine(canonicalExisting, "new", "deeper", "leaf.md"), canonical);
    }

    /// <summary>
    ///     Each check answers from the filesystem as it is at that moment: replacing a directory with a
    ///     symlink between two checks must change the answer the second time.
    /// </summary>
    [Fact]
    public void ContainmentRefusesAPathWhoseIntermediateDirectoryBecameASymlinkBetweenChecks()
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
