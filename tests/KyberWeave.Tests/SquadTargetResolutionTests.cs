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
        SquadTarget.Antigravity,
        SquadTarget.Warp,
        SquadTarget.Factory,
        SquadTarget.Pi
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
        { ".warp", true, SquadTarget.Warp },
        { ".factory", true, SquadTarget.Factory }
    };

    /// <summary>
    /// Receipts persist target tokens, so existing members keep their positions and a new
    /// target is appended: Pi follows Factory.
    /// </summary>
    [Fact]
    public void CatalogContainsExactlyTenTargetsInStableOrder()
    {
        Assert.Equal(TenTargets, SquadTargetCatalog.All);
        Assert.Equal(
            ["codex", "cursor", "claude", "copilot", "opencode", "kilo", "antigravity", "warp", "factory", "pi"],
            SquadTargetCatalog.All.Select(SquadTargetCatalog.GetToken));
    }

    [Fact]
    public void ParseRepeatedCommaSeparatedTargetsAndAliasesNormalizeToFirstSeenOrderedSet()
    {
        IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse(
            [" cursor, github-copilot ", "CODEX", "cursor", "factory-droids,opencode"]);

        Assert.Equal(
            [SquadTarget.Cursor, SquadTarget.Copilot, SquadTarget.Codex, SquadTarget.Factory, SquadTarget.OpenCode],
            targets);
    }

    [Fact]
    public void ParseAllExpandsToTheApprovedTenTargetRoster()
    {
        IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse(["all"]);

        Assert.Equal(TenTargets, targets);
    }

    [Theory]
    [InlineData("pi")]
    [InlineData("PI")]
    public void ParsePiTokenSelectsPiCaseInsensitively(string token)
    {
        IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([token]);

        Assert.Equal(SquadTarget.Pi, Assert.Single(targets));
    }

    [Fact]
    public void ParseUnknownTargetFailsWithKnownTargetHint()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => SquadTargetCatalog.Parse(["not-a-harness"]));

        Assert.Contains("not-a-harness", exception.Message, StringComparison.Ordinal);
        Assert.Contains("codex", exception.Message, StringComparison.Ordinal);
        // Whole word: "copilot" already contains "pi", so a substring check passes vacuously.
        Assert.Matches(@"\bpi\b", exception.Message);
        Assert.Contains("all", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallExplicitTargetsReplaceConfigurationAndMarkers()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".claude"));

        SquadTargetResolutionDecision decision = ResolveInstall(
            explicitTargets: ["codex"],
            configuredTargets: [SquadTarget.Cursor]);

        AssertResolved(decision, SquadTargetResolutionSource.Explicit, SquadTarget.Codex);
    }

    [Fact]
    public void InstallConfiguredTargetsReplaceMarkerDetectionWhenExplicitTargetsAreAbsent()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".claude"));

        SquadTargetResolutionDecision decision = ResolveInstall(configuredTargets: [SquadTarget.Cursor]);

        AssertResolved(decision, SquadTargetResolutionSource.Configuration, SquadTarget.Cursor);
    }

    [Fact]
    public void InstallStrongMarkersAreUsedWhenExplicitAndConfiguredTargetsAreAbsent()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".warp"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".codex"));

        SquadTargetResolutionDecision decision = ResolveInstall();

        AssertResolved(
            decision,
            SquadTargetResolutionSource.Markers,
            SquadTarget.Codex,
            SquadTarget.Warp);
    }

    [Fact]
    public void InstallConfigAndCliExclusionsUnionAfterAllExpansionAndOnlyNarrow()
    {
        SquadTargetResolutionDecision decision = ResolveInstall(
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
            SquadTarget.Antigravity,
            SquadTarget.Factory,
            SquadTarget.Pi);
    }

    [Theory]
    [MemberData(nameof(PositiveMarkers))]
    public void InstallEachStrongMarkerSelectsItsTarget(
        string relativePath,
        bool isDirectory,
        SquadTarget expected)
    {
        CreateFixture(relativePath, isDirectory);

        SquadTargetResolutionDecision decision = ResolveInstall();

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
    public void InstallGenericInstructionAndWrongKindFixturesDoNotSelectATarget(
        string relativePath,
        bool isDirectory)
    {
        CreateFixture(relativePath, isDirectory);

        SquadTargetResolutionDecision decision = ResolveInstall(isInteractive: false);

        Assert.Equal(SquadTargetResolutionKind.Failure, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, decision.Source);
        Assert.Equal(2, decision.ExitCode);
    }

    [Fact]
    public void InstallAntigravityHasNoFilesystemMarkerAndRequiresExplicitOrConfiguredSelection()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".agents"));

        SquadTargetResolutionDecision markerDecision = ResolveInstall(isInteractive: false);
        SquadTargetResolutionDecision explicitDecision = ResolveInstall(explicitTargets: ["antigravity"]);

        Assert.Equal(SquadTargetResolutionKind.Failure, markerDecision.Kind);
        AssertResolved(explicitDecision, SquadTargetResolutionSource.Explicit, SquadTarget.Antigravity);
    }

    /// <summary>
    /// This repository carries <c>.pi/subagents.json</c>, the pi-subagents extension's project
    /// settings. Pi is selected by explicit or configured targets only, so a <c>.pi/</c> tree,
    /// even one that already holds agents, must never select it on its own.
    /// </summary>
    [Fact]
    public void InstallPiHasNoFilesystemMarkerAndRequiresExplicitOrConfiguredSelection()
    {
        CreateFixture(".pi/subagents.json", isDirectory: false);
        CreateFixture(".pi/agents", isDirectory: true);

        SquadTargetResolutionDecision markerDecision = ResolveInstall(isInteractive: false);
        SquadTargetResolutionDecision explicitDecision = ResolveInstall(explicitTargets: ["pi"]);
        SquadTargetResolutionDecision configuredDecision = ResolveInstall(configuredTargets: [SquadTarget.Pi]);

        Assert.Equal(SquadTargetResolutionKind.Failure, markerDecision.Kind);
        Assert.Empty(markerDecision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, markerDecision.Source);
        AssertResolved(explicitDecision, SquadTargetResolutionSource.Explicit, SquadTarget.Pi);
        AssertResolved(configuredDecision, SquadTargetResolutionSource.Configuration, SquadTarget.Pi);
    }

    [Theory]
    [InlineData(SquadTargetOperation.Update)]
    [InlineData(SquadTargetOperation.Uninstall)]
    public void LifecycleOperationsUseReceiptAndNeverRedetect(SquadTargetOperation operation)
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".codex"));
        SquadTargetResolutionRequest request = new SquadTargetResolutionRequest
        {
            RootPath = _temp.Path,
            Operation = operation,
            ConfiguredTargets = [SquadTarget.Cursor],
            ReceiptTargets = [SquadTarget.Factory, SquadTarget.Warp],
            IsInteractive = true
        };

        SquadTargetResolutionDecision decision = SquadTargetResolver.Resolve(request);

        AssertResolved(
            decision,
            SquadTargetResolutionSource.Receipt,
            SquadTarget.Factory,
            SquadTarget.Warp);
    }

    [Fact]
    public void InstallNoTargetInInteractiveTerminalReturnsChooserDecisionWithoutReadingConsole()
    {
        SquadTargetResolutionDecision decision = ResolveInstall(isInteractive: true);

        Assert.Equal(SquadTargetResolutionKind.InteractiveSelectionRequired, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, decision.Source);
        Assert.Null(decision.ExitCode);
        Assert.Null(decision.RecoveryCommand);
    }

    [Fact]
    public void InstallNoTargetInNonInteractiveTerminalReturnsExitTwoAndExactRecoveryCommand()
    {
        SquadTargetResolutionDecision decision = ResolveInstall(isInteractive: false);

        Assert.Equal(SquadTargetResolutionKind.Failure, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, decision.Source);
        Assert.Equal(2, decision.ExitCode);
        Assert.Equal("kyber-weave squad install --target <target>", decision.RecoveryCommand);
    }

    [Theory]
    [InlineData(SquadTargetOperation.Install, "kyber-weave squad install --target <target>")]
    [InlineData(SquadTargetOperation.Update, "kyber-weave squad update --target <target>")]
    [InlineData(SquadTargetOperation.Uninstall, "kyber-weave squad uninstall --target <target>")]
    public void NonInteractiveNoTargetReturnsExitTwoAndOperationSpecificRecoveryCommand(
        SquadTargetOperation operation,
        string expectedCommand)
    {
        SquadTargetResolutionRequest request = new SquadTargetResolutionRequest
        {
            RootPath = _temp.Path,
            Operation = operation,
            IsInteractive = false
        };

        SquadTargetResolutionDecision decision = SquadTargetResolver.Resolve(request);

        Assert.Equal(SquadTargetResolutionKind.Failure, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.None, decision.Source);
        Assert.Equal(2, decision.ExitCode);
        Assert.Equal(expectedCommand, decision.RecoveryCommand);
    }

    public void Dispose() => _temp.Dispose();

    private SquadTargetResolutionDecision ResolveInstall(
        IReadOnlyList<string>? explicitTargets = null,
        IReadOnlyList<SquadTarget>? configuredTargets = null,
        IReadOnlyList<string>? explicitExclusions = null,
        IReadOnlyList<SquadTarget>? configuredExclusions = null,
        bool isInteractive = false)
    {
        SquadTargetResolutionRequest request = new SquadTargetResolutionRequest
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
        string path = Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (isDirectory)
        {
            Directory.CreateDirectory(path);
            return;
        }

        string? parent = Path.GetDirectoryName(path);
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
