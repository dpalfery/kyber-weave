using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// K3 RED contract for deterministic Squad target parsing and selection. The resolver is
/// intentionally a pure decision service: callers decide how to render a chooser or exit.
/// </summary>
public sealed class SquadTargetResolutionTests : IDisposable
{
    private static readonly SquadTarget[] TenTargets =
    [
        SquadTarget.Codex,
        SquadTarget.Cursor,
        SquadTarget.Claude,
        SquadTarget.Copilot,
        SquadTarget.OpenCode,
        SquadTarget.Kilo,
        SquadTarget.Gemini,
        SquadTarget.Antigravity,
        SquadTarget.Warp,
        SquadTarget.Factory
    ];

    private readonly TempDirectory _temp = new();

    public static TheoryData<string, bool, SquadTarget> PositiveMarkers => new()
    {
        { ".codex", true, SquadTarget.Codex },
        { ".cursor", true, SquadTarget.Cursor },
        { ".claude", true, SquadTarget.Claude },
        { ".github/copilot-instructions.md", false, SquadTarget.Copilot },
        { ".github/instructions", true, SquadTarget.Copilot },
        { ".github/agents", true, SquadTarget.Copilot },
        { ".github/prompts", true, SquadTarget.Copilot },
        { ".github/hooks", true, SquadTarget.Copilot },
        { ".opencode", true, SquadTarget.OpenCode },
        { ".kilo", true, SquadTarget.Kilo },
        { ".gemini", true, SquadTarget.Gemini },
        { ".warp", true, SquadTarget.Warp },
        { ".factory", true, SquadTarget.Factory }
    };

    [Fact]
    public void Catalog_ContainsExactlyTenTargetsInStableOrder()
    {
        Assert.Equal(TenTargets, SquadTargetCatalog.All);
        Assert.Equal(
            ["codex", "cursor", "claude", "copilot", "opencode", "kilo", "gemini", "antigravity", "warp", "factory"],
            SquadTargetCatalog.All.Select(SquadTargetCatalog.GetToken));
    }

    [Fact]
    public void Parse_RepeatedCommaSeparatedTargetsAndAliasesNormalizeToFirstSeenOrderedSet()
    {
        var targets = SquadTargetCatalog.Parse(
            [" cursor, github-copilot ", "CODEX", "cursor", "factory-droids,opencode"]);

        Assert.Equal(
            [SquadTarget.Cursor, SquadTarget.Copilot, SquadTarget.Codex, SquadTarget.Factory, SquadTarget.OpenCode],
            targets);
    }

    [Fact]
    public void Parse_AllExpandsToTheApprovedTenTargetRoster()
    {
        var targets = SquadTargetCatalog.Parse(["all"]);

        Assert.Equal(TenTargets, targets);
    }

    [Fact]
    public void Parse_UnknownTargetFailsWithKnownTargetHint()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SquadTargetCatalog.Parse(["not-a-harness"]));

        Assert.Contains("not-a-harness", exception.Message, StringComparison.Ordinal);
        Assert.Contains("codex", exception.Message, StringComparison.Ordinal);
        Assert.Contains("all", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ExplicitTargetsReplaceConfigurationAndMarkers()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".claude"));

        var decision = ResolveInstall(
            explicitTargets: ["codex"],
            configuredTargets: [SquadTarget.Cursor]);

        AssertResolved(decision, SquadTargetResolutionSource.Explicit, SquadTarget.Codex);
    }

    [Fact]
    public void Install_ConfiguredTargetsReplaceMarkerDetectionWhenExplicitTargetsAreAbsent()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".claude"));

        var decision = ResolveInstall(configuredTargets: [SquadTarget.Cursor]);

        AssertResolved(decision, SquadTargetResolutionSource.Configuration, SquadTarget.Cursor);
    }

    [Fact]
    public void Install_StrongMarkersAreUsedWhenExplicitAndConfiguredTargetsAreAbsent()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".warp"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".codex"));

        var decision = ResolveInstall();

        AssertResolved(
            decision,
            SquadTargetResolutionSource.Markers,
            SquadTarget.Codex,
            SquadTarget.Warp);
    }

    [Fact]
    public void Install_ConfigAndCliExclusionsUnionAfterAllExpansionAndOnlyNarrow()
    {
        var decision = ResolveInstall(
            explicitTargets: ["all"],
            explicitExclusions: ["cursor,warp"],
            configuredExclusions: [SquadTarget.Copilot, SquadTarget.Warp]);

        AssertResolved(
            decision,
            SquadTargetResolutionSource.Explicit,
            SquadTarget.Codex,
            SquadTarget.Claude,
            SquadTarget.OpenCode,
            SquadTarget.Kilo,
            SquadTarget.Gemini,
            SquadTarget.Antigravity,
            SquadTarget.Factory);
    }

    [Theory]
    [MemberData(nameof(PositiveMarkers))]
    public void Install_EachStrongMarkerSelectsItsTarget(
        string relativePath,
        bool isDirectory,
        SquadTarget expected)
    {
        CreateFixture(relativePath, isDirectory);

        var decision = ResolveInstall();

        AssertResolved(decision, SquadTargetResolutionSource.Markers, expected);
    }

    [Theory]
    [InlineData("AGENTS.md", false)]
    [InlineData("CLAUDE.md", false)]
    [InlineData("GEMINI.md", false)]
    [InlineData(".github", true)]
    [InlineData(".github/workflows", true)]
    [InlineData(".agents/skills", true)]
    [InlineData(".agents/skills/example/SKILL.md", false)]
    [InlineData(".codex", false)]
    [InlineData(".cursor", false)]
    [InlineData(".claude", false)]
    [InlineData(".gemini", false)]
    public void Install_GenericInstructionAndWrongKindFixturesDoNotSelectATarget(
        string relativePath,
        bool isDirectory)
    {
        CreateFixture(relativePath, isDirectory);

        var decision = ResolveInstall(isInteractive: false);

        Assert.Equal(SquadTargetResolutionKind.Failure, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, decision.Source);
        Assert.Equal(2, decision.ExitCode);
    }

    [Fact]
    public void Install_AntigravityHasNoFilesystemMarkerAndRequiresExplicitOrConfiguredSelection()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".agents"));

        var markerDecision = ResolveInstall(isInteractive: false);
        var explicitDecision = ResolveInstall(explicitTargets: ["antigravity"]);

        Assert.Equal(SquadTargetResolutionKind.Failure, markerDecision.Kind);
        AssertResolved(explicitDecision, SquadTargetResolutionSource.Explicit, SquadTarget.Antigravity);
    }

    [Theory]
    [InlineData(SquadTargetOperation.Update)]
    [InlineData(SquadTargetOperation.Uninstall)]
    public void LifecycleOperationsUseReceiptAndNeverRedetect(SquadTargetOperation operation)
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".codex"));
        var request = new SquadTargetResolutionRequest
        {
            RootPath = _temp.Path,
            Operation = operation,
            ConfiguredTargets = [SquadTarget.Cursor],
            ReceiptTargets = [SquadTarget.Factory, SquadTarget.Warp],
            IsInteractive = true
        };

        var decision = SquadTargetResolver.Resolve(request);

        AssertResolved(
            decision,
            SquadTargetResolutionSource.Receipt,
            SquadTarget.Factory,
            SquadTarget.Warp);
    }

    [Fact]
    public void Install_NoTargetInInteractiveTerminalReturnsChooserDecisionWithoutReadingConsole()
    {
        var decision = ResolveInstall(isInteractive: true);

        Assert.Equal(SquadTargetResolutionKind.InteractiveSelectionRequired, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, decision.Source);
        Assert.Null(decision.ExitCode);
        Assert.Null(decision.RecoveryCommand);
    }

    [Fact]
    public void Install_NoTargetInNonInteractiveTerminalReturnsExitTwoAndExactRecoveryCommand()
    {
        var decision = ResolveInstall(isInteractive: false);

        Assert.Equal(SquadTargetResolutionKind.Failure, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, decision.Source);
        Assert.Equal(2, decision.ExitCode);
        Assert.Equal("kyber-weave squad install --target <target>", decision.RecoveryCommand);
    }

    public void Dispose() => _temp.Dispose();

    private SquadTargetResolutionDecision ResolveInstall(
        IReadOnlyList<string>? explicitTargets = null,
        IReadOnlyList<SquadTarget>? configuredTargets = null,
        IReadOnlyList<string>? explicitExclusions = null,
        IReadOnlyList<SquadTarget>? configuredExclusions = null,
        bool isInteractive = false)
    {
        var request = new SquadTargetResolutionRequest
        {
            RootPath = _temp.Path,
            Operation = SquadTargetOperation.Install,
            ExplicitTargets = explicitTargets ?? [],
            ConfiguredTargets = configuredTargets ?? [],
            ExplicitExclusions = explicitExclusions ?? [],
            ConfiguredExclusions = configuredExclusions ?? [],
            IsInteractive = isInteractive
        };

        return SquadTargetResolver.Resolve(request);
    }

    private void CreateFixture(string relativePath, bool isDirectory)
    {
        var path = Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (isDirectory)
        {
            Directory.CreateDirectory(path);
            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is not null)
            Directory.CreateDirectory(parent);
        File.WriteAllText(path, "fixture");
    }

    private static void AssertResolved(
        SquadTargetResolutionDecision decision,
        SquadTargetResolutionSource source,
        params SquadTarget[] expected)
    {
        Assert.Equal(SquadTargetResolutionKind.Resolved, decision.Kind);
        Assert.Equal(source, decision.Source);
        Assert.Equal(expected, decision.Targets);
        Assert.Null(decision.ExitCode);
        Assert.Null(decision.RecoveryCommand);
    }
}
