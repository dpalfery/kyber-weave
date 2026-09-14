using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins Pi through the real Kyber-Squad lifecycle composition:
/// <see cref="SquadLifecycleService.InstallAsync"/> driven by
/// <see cref="SquadCommandComposition.ResolveRenderer"/> against the real
/// <c>products/kyber-squad</c> corpus (via <see cref="CorpusSquadReleaseSource"/>), rather than
/// the isolated <c>PiRendererContractTests</c> render path.
/// </summary>
/// <remarks>
/// Covers docs/plans/2026-09-14-pi-harness-target.md section 7, row T3 (R13, R14, R15, R17):
/// a dry-run Pi install renders the whole corpus under the <c>pi</c> target only; Pi and
/// Antigravity coexist with disjoint output trees; a real Pi install never touches this
/// repository's pre-existing <c>.pi/subagents.json</c>; and <c>squad doctor</c> lists <c>pi</c>.
/// Until T5 registers <c>PiRenderer</c>, every test here fails at the
/// <c>RequireRenderableTargets</c> preflight inside <c>SquadLifecycleService.InstallAsync</c>,
/// which names <c>pi</c> and <c>docs/todo</c> in a <see cref="SquadRenderValidationException"/>.
/// </remarks>
public sealed class PiSquadLifecycleTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task InstallAsync_DryRun_Pi_RendersRealCorpusEntirelyUnderPiWithoutTouchingTheRoot()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "pi-dry-run");
        Directory.CreateDirectory(targetRoot);

        // Act
        SquadLifecycleResult result = await DryRunInstallAsync(targetRoot, [SquadTarget.Pi]);

        // Assert
        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Receipt);

        int expectedFileCount = ExpectedRenderedFileCount();
        Assert.Equal(expectedFileCount, result.Receipt.Files.Count);
        Assert.All(result.Receipt.Files, f => Assert.Equal("pi", f.Target));

        // A dry run preflights and renders in memory only; the target root must stay empty.
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetRoot));
    }

    [Fact]
    public async Task InstallAsync_DryRun_AntigravityAndPi_ProduceDisjointPathsSummingToTheIndividualCounts()
    {
        // Arrange
        string antigravityRoot = Path.Combine(_temp.Path, "antigravity-only");
        string piRoot = Path.Combine(_temp.Path, "pi-only");
        string combinedRoot = Path.Combine(_temp.Path, "antigravity-and-pi");
        Directory.CreateDirectory(antigravityRoot);
        Directory.CreateDirectory(piRoot);
        Directory.CreateDirectory(combinedRoot);

        // Act
        SquadLifecycleResult antigravityResult = await DryRunInstallAsync(antigravityRoot, [SquadTarget.Antigravity]);
        SquadLifecycleResult piResult = await DryRunInstallAsync(piRoot, [SquadTarget.Pi]);
        SquadLifecycleResult combinedResult = await DryRunInstallAsync(
            combinedRoot,
            [SquadTarget.Antigravity, SquadTarget.Pi]);

        // Assert
        Assert.True(antigravityResult.Success, string.Join("; ", antigravityResult.Errors ?? Array.Empty<string>()));
        Assert.True(piResult.Success, string.Join("; ", piResult.Errors ?? Array.Empty<string>()));
        Assert.True(combinedResult.Success, string.Join("; ", combinedResult.Errors ?? Array.Empty<string>()));
        Assert.NotNull(antigravityResult.Receipt);
        Assert.NotNull(piResult.Receipt);
        Assert.NotNull(combinedResult.Receipt);

        HashSet<string> antigravityPaths = antigravityResult.Receipt.Files
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> piPaths = piResult.Receipt.Files
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> combinedPaths = combinedResult.Receipt.Files
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(antigravityPaths.Intersect(piPaths));
        Assert.Equal(antigravityPaths.Count + piPaths.Count, combinedResult.Receipt.Files.Count);
        Assert.Equal(antigravityPaths.Count + piPaths.Count, combinedPaths.Count);
    }

    [Fact]
    public async Task InstallAsync_RealInstall_Pi_LeavesPreExistingPiSubagentsJsonByteIdenticalAndUnlisted()
    {
        // Arrange
        string targetRoot = Path.Combine(_temp.Path, "pi-real-install");
        Directory.CreateDirectory(targetRoot);

        string subagentsPath = Path.Combine(targetRoot, ".pi", "subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(subagentsPath)!);
        string subagentsContent = "{\n  \"maxSubagentDepth\": 2,\n  \"fallbackSubagent\": \"none\"\n}\n";
        await File.WriteAllTextAsync(subagentsPath, subagentsContent);
        byte[] subagentsBytesBefore = await File.ReadAllBytesAsync(subagentsPath);

        string userData = Path.Combine(_temp.Path, "pi-real-install-user-data");
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Pi],
            Version: "1.2.3");

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success, string.Join("; ", result.Errors ?? Array.Empty<string>()));
        Assert.NotNull(result.Receipt);

        byte[] subagentsBytesAfter = await File.ReadAllBytesAsync(subagentsPath);
        Assert.Equal(subagentsBytesBefore, subagentsBytesAfter);

        Assert.DoesNotContain(
            result.Receipt.Files,
            f => string.Equals(f.RelativePath, ".pi/subagents.json", StringComparison.Ordinal));
    }

    private static async Task<SquadLifecycleResult> DryRunInstallAsync(
        string targetRoot,
        IReadOnlyList<SquadTarget> targets)
    {
        string userData = targetRoot + "-user-data";
        FakeSquadUserPaths userPaths = new(userData);
        SquadStateStore stateStore = new(userPaths);
        using CorpusSquadReleaseSource releaseSource = new();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadLifecycleService service = new(releaseSource, renderer, stateStore);

        SquadInstallRequest request = new(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: targets,
            Version: "1.2.3",
            DryRun: true);

        return await service.InstallAsync(request);
    }

    /// <summary>
    /// R17: <c>Agents.Count + Σ agent resources + Skills.Count − shared-identity skills +
    /// Σ non-suppressed skill resources</c>, read from the loaded corpus rather than a
    /// hardcoded literal. Mirrors the derived-count pattern in
    /// <c>ClaudeRendererContractTests.RenderAsync_Claude_RendersTheRealCanonicalCorpus</c>: the
    /// formula is target-agnostic because a lowered primary agent (Pi's <c>conductor</c>, R3)
    /// still contributes exactly one principal, whether it renders as an agent or a skill.
    /// </summary>
    private static int ExpectedRenderedFileCount()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);
        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));

        return source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);
    }
}
