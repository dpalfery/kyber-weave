using System.Collections.Concurrent;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Pins the opt-in operator channel overlay for the Copilot CLI conductor, rendered at the renderer
/// seam. The overlay is gated by the process environment variable <c>KYBER_SQUAD_OPERATOR_CHANNEL</c>
/// (exactly <c>true</c>), applies only to the Copilot global scope, and names its MCP server from the
/// second variable <c>KYBER_SQUAD_OPERATOR_CHANNEL_SERVER</c>, which must be a valid name and must appear
/// under <c>mcpServers</c> in <c>$COPILOT_HOME/mcp-config.json</c>.
/// </summary>
/// <remarks>
/// Facts are black-box through the shipped renderers over the shipped Squad source copied into memory, and
/// <see cref="SquadRenderRequest"/>. The default-off parity facts guard against regressions: with the gate
/// unset or set to any value other than the exact string <c>true</c>, every output must match a render with
/// the gate unset, and no <c>operator-channel</c> text may appear anywhere. Environment variables are read
/// through injectable delegates rather than process mutation, allowing safe parallel test execution.
/// </remarks>
public sealed class SquadOperatorChannelOptInTests
{
    private static readonly CanonicalSquadTree Tree = CanonicalSquadTree.Create();

    private static readonly Lazy<SquadSource> Source = new(Tree.Load);

    private static readonly (string CopilotHome, string HomeDirectory) DefaultOffFixture = CreateDefaultOffFixture();

    private static readonly ConcurrentDictionary<SquadDeploymentScope, Task<SquadRenderResult>> DefaultOffBaselines =
        new();

    private static readonly ConcurrentDictionary<SquadTarget, Task<SquadRenderResult>> NonCopilotGlobalOffRenders =
        new();

    private static (string CopilotHome, string HomeDirectory) CreateDefaultOffFixture()
    {
        string root = Path.Combine(Tree.Files.Root, "default-off");
        string copilotHome = Path.Combine(root, "copilot-home");
        string homeDirectory = Path.Combine(root, "home");
        Tree.Files.CreateDirectory(copilotHome);
        Tree.Files.CreateDirectory(homeDirectory);
        Tree.Files.WriteAllText(
            Path.Combine(copilotHome, "mcp-config.json"),
            SquadOperatorChannelFixture.ServerPresentConfig);
        return (copilotHome, homeDirectory);
    }

    /// <summary>
    /// Creates an isolated COPILOT_HOME with an optional <c>mcp-config.json</c> and a fake user home.
    /// Each call gets its own directory, so theory cases never share state.
    /// </summary>
    private (string CopilotHome, string HomeDirectory) NewFixture(string? mcpConfigJson)
    {
        string root = Path.Combine(Tree.Files.Root, Guid.NewGuid().ToString("N"));
        string copilotHome = Path.Combine(root, "copilot-home");
        string homeDirectory = Path.Combine(root, "home");
        Tree.Files.CreateDirectory(copilotHome);
        Tree.Files.CreateDirectory(homeDirectory);
        if (mcpConfigJson is not null)
        {
            Tree.Files.WriteAllText(Path.Combine(copilotHome, "mcp-config.json"), mcpConfigJson);
        }

        return (copilotHome, homeDirectory);
    }

    /// <summary>
    /// Renders the shipped source in <paramref name="tree"/> through every shipped renderer, with the operator-channel
    /// environment the arguments describe; the render reads the Copilot server config from the tree's file system.
    /// </summary>
    private static async Task<SquadRenderResult> RenderAsync(
        string copilotHome,
        string homeDirectory,
        string? gateValue,
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName,
        ISquadGlobalRootResolver? roots = null,
        CanonicalSquadTree? tree = null)
    {
        CanonicalSquadTree used = tree ?? Tree;
        SquadRenderRequest request = used.Prepare(new SquadRenderRequest(
            used.ProductRoot,
            targets,
            scope,
            GlobalRoots: roots ?? SquadOperatorChannelFixture.GlobalRoots(copilotHome, homeDirectory),
            PreloadedSource: Source.Value,
            EnvironmentReader: SquadOperatorChannelFixture.CreateEnvironmentReader(gateValue, copilotHome, serverName)));
        SquadRenderResult result = await used.Registry(SquadHostDefaults.CreateRenderers()).RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    [Fact]
    public async Task OperatorChannelGate_ExactTrueWithServerFound_AddsExactlyOneToolEntryToCopilotGlobalConductor()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await RenderAsync(
            copilot, home, null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

        IReadOnlyList<string> offTools =
            SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(off));
        IReadOnlyList<string> onTools =
            SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(on));

        Assert.Equal(
            offTools.Append(SquadOperatorChannelFixture.ToolEntry).ToArray(),
            onTools.ToArray());
    }

    [Fact]
    public async Task OperatorChannelGate_ExactTrueWithServerFound_InsertsOverlayTextIntoCopilotGlobalConductorBody()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);
        string body = SquadOperatorChannelFixture.ConductorText(on);

        // The overlay file is the source of truth; its first prose paragraph must reach the body with the
        // server name substituted for the placeholder.
        string firstParagraph = SquadOperatorChannelFixture.OverlayFirstParagraph();
        Assert.Contains(firstParagraph, body, StringComparison.Ordinal);

        foreach (string eventLabel in SquadOperatorChannelFixture.EventLabels)
        {
            Assert.Contains(eventLabel, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OperatorChannelGate_ExactTrueWithServerFound_ConductorBodyGrowsByTheOverlayAndStaysWithinBudget()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await RenderAsync(
            copilot, home, null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

        int offLength = SquadOperatorChannelFixture.ConductorText(off).Length;
        int onLength = SquadOperatorChannelFixture.ConductorText(on).Length;

        Assert.True(
            onLength > offLength,
            $"The overlay must add text to the conductor body (default {offLength} chars, gated {onLength} chars).");
        Assert.True(
            onLength <= SquadOperatorChannelFixture.ConductorBodyLimit,
            $"The gated conductor body is {onLength} chars, over the {SquadOperatorChannelFixture.ConductorBodyLimit} limit.");
    }

    [Fact]
    public async Task OperatorChannelGate_ExactTrueWithServerFound_LeavesEveryOtherCopilotAgentUnchanged()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await RenderAsync(
            copilot, home, null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off).Where(IsNotConductor).ToArray(),
            SquadOperatorChannelFixture.FileDigests(on).Where(IsNotConductor).ToArray());
        Assert.Equal(
            off.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray(),
            on.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray());

        static bool IsNotConductor(string digest)
        {
            return !digest.Contains("conductor.agent.md", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A valid server name renders its tool entry and its overlay with the name substituted. No placeholder
    /// survives in any file, and the retired fixed alias never appears as a tool allow-list entry. Valid
    /// names cover the 64-character limit and each allowed character class.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidServerNames))]
    public async Task OperatorChannelGate_ValidServerName_SubstitutesThePlaceholderAndLeavesNoPlaceholderOrAlias(
        string serverName)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerConfig(serverName));

        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global,
            serverName);

        string conductor = SquadOperatorChannelFixture.ConductorText(on);
        Assert.Contains($"'{serverName}/*'", SquadOperatorChannelFixture.ToolEntries(conductor),
            StringComparer.Ordinal);
        Assert.Contains(serverName, conductor, StringComparison.Ordinal);

        string[] texts = SquadOperatorChannelFixture.FileTexts(on);
        Assert.Empty(texts.Where(t => t.Contains(SquadOperatorChannelFixture.Placeholder, StringComparison.Ordinal))
            .ToArray());
        Assert.Empty(texts.Where(t => t.Contains("operator-channel/*", StringComparison.Ordinal)).ToArray());
    }

    public static IEnumerable<object[]> ValidServerNames()
    {
        yield return [new string('a', 64)];
        yield return ["ops.relay_1-2"];
        yield return ["OPS-Relay"];
    }

    [Theory]
    [MemberData(nameof(ServerAbsentCases))]
    public async Task OperatorChannelServerAbsent_RendersDefaultOutputPlusAbsentWarningAndNoOverlay(
        string variant,
        string? mcpConfigJson)
    {
        (string copilot, string home) = NewFixture(mcpConfigJson);

        SquadRenderResult off = await RenderAsync(
            copilot, home, null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

        Assert.True(on.Success, $"Variant '{variant}' must still render.");
        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off),
            SquadOperatorChannelFixture.FileDigests(on));
        Assert.Equal(off.Degradations, on.Degradations);
        Assert.Equal(
            off.Warnings.ToArray(),
            on.Warnings.Where(w => w.Code != SquadOperatorChannelFixture.ServerAbsentWarning).ToArray());
        Assert.Single(
            on.Warnings,
            w => w.Code == SquadOperatorChannelFixture.ServerAbsentWarning);
        Assert.Equal(
            SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(off)).ToArray(),
            SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(on)).ToArray());
        Assert.DoesNotContain(
            SquadOperatorChannelFixture.ToolEntry,
            SquadOperatorChannelFixture.ConductorText(on),
            StringComparison.Ordinal);
    }

    public static IEnumerable<object?[]> ServerAbsentCases()
    {
        yield return ["no-config-file", null];
        yield return ["no-mcp-servers-key", "{\"servers\":{\"ops-relay\":{\"command\":\"x\"}}}"];
        yield return
        [
            "other-server-names-only",
            "{\"mcpServers\":{\"codegraph\":{\"command\":\"x\"},\"other-relay\":{\"command\":\"y\"}}}"
        ];
        yield return ["malformed-json", "{\"mcpServers\": {\"ops-relay\": "];
    }

    /// <summary>
    /// With the gate on but <c>KYBER_SQUAD_OPERATOR_CHANNEL_SERVER</c> unset or empty, the channel stays off:
    /// output is the default plus <c>operator-channel-server-name-missing</c>, and no tool entry or overlay is
    /// rendered even though the config declares a server.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OperatorChannelGate_ServerNameMissing_RendersDefaultOutputPlusNameMissingWarning(
        string? serverName)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await RenderAsync(
            copilot, home, null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global,
            serverName);

        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off),
            SquadOperatorChannelFixture.FileDigests(on));
        Assert.Equal(off.Degradations, on.Degradations);
        Assert.Equal(
            off.Warnings.ToArray(),
            on.Warnings.Where(w => w.Code != SquadOperatorChannelFixture.NameMissingWarning).ToArray());
        Assert.Single(on.Warnings, w => w.Code == SquadOperatorChannelFixture.NameMissingWarning);
    }

    /// <summary>
    /// A server name outside <c>^[A-Za-z0-9._-]{1,64}$</c> turns the channel off with
    /// <c>operator-channel-server-name-invalid</c>, and the raw value must never appear in any rendered file.
    /// The markers are distinctive so a match means the raw value leaked, not coincidence.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidServerNames))]
    public async Task OperatorChannelGate_ServerNameInvalid_RendersDefaultOutputAndNeverEchoesTheRawValue(
        string serverName)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await RenderAsync(
            copilot, home, null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global,
            serverName);

        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off),
            SquadOperatorChannelFixture.FileDigests(on));
        Assert.Equal(
            off.Warnings.ToArray(),
            on.Warnings.Where(w => w.Code != SquadOperatorChannelFixture.NameInvalidWarning).ToArray());
        Assert.Single(on.Warnings, w => w.Code == SquadOperatorChannelFixture.NameInvalidWarning);

        string[] offenders = SquadOperatorChannelFixture.FileTexts(on)
            .Where(t => t.Contains(serverName, StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offenders);
    }

    public static IEnumerable<object[]> InvalidServerNames()
    {
        yield return ["bad name zz1"];
        yield return ["bad/slash-zz1"];
        yield return ["bad'quote-zz1"];
        yield return ["bad:colon-zz1"];
        yield return ["bad\nnewline-zz1"];
        yield return [new string('q', 65)];
        yield return ["bäd-zz1"];
    }

    [Fact]
    public async Task OperatorChannelGate_CopilotProjectScope_RecordsNotRenderedDegradationAndKeepsDefaultOutput()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await RenderAsync(
            copilot, home, null, [SquadTarget.Copilot], SquadDeploymentScope.Project);
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Project);

        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off),
            SquadOperatorChannelFixture.FileDigests(on));
        Assert.Equal(
            off.Degradations.ToArray(),
            on.Degradations.Where(d => d.Code != SquadOperatorChannelFixture.NotRenderedDegradation).ToArray());

        SquadDegradationRecord record = Assert.Single(
            on.Degradations,
            d => d.Code == SquadOperatorChannelFixture.NotRenderedDegradation);
        Assert.Equal("copilot", record.Target);
        Assert.Contains("Copilot CLI global scope only", record.Details ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(SquadOperatorChannelFixture.OperatorChannelMentions(
            on,
            SquadOperatorChannelFixture.NotRenderedDegradation));
    }

    [Theory]
    [MemberData(nameof(NonCopilotTargets))]
    public async Task OperatorChannelGate_EveryOtherTargetRecordsNotRenderedDegradationAndKeepsDefaultOutput(
        SquadTarget target)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await NonCopilotGlobalOffRenders.GetOrAdd(
            target,
            t => RenderAsync(
                copilot, home, null, [t], SquadDeploymentScope.Global));
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [target], SquadDeploymentScope.Global);

        string token = SquadTargetCatalog.GetToken(target);
        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off),
            SquadOperatorChannelFixture.FileDigests(on));
        Assert.Equal(
            off.Degradations.ToArray(),
            on.Degradations.Where(d => d.Code != SquadOperatorChannelFixture.NotRenderedDegradation).ToArray());

        SquadDegradationRecord record = Assert.Single(
            on.Degradations,
            d => d.Code == SquadOperatorChannelFixture.NotRenderedDegradation);
        Assert.Equal(token, record.Target);
        Assert.Contains("Copilot CLI global scope only", record.Details ?? string.Empty, StringComparison.Ordinal);

        Assert.Empty(SquadOperatorChannelFixture.OperatorChannelMentions(
            on,
            SquadOperatorChannelFixture.NotRenderedDegradation));
    }

    /// <summary>
    /// Non-Copilot targets stay not-rendered whatever the server variable holds: unset or invalid must
    /// produce the same files and the same single not-rendered record as a valid name.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonCopilotTargetsWithBadServerName))]
    public async Task OperatorChannelGate_NonCopilotTargetStaysNotRenderedWhenServerNameMissingOrInvalid(
        SquadTarget target,
        string? serverName)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await NonCopilotGlobalOffRenders.GetOrAdd(
            target,
            t => RenderAsync(
                copilot, home, null, [t], SquadDeploymentScope.Global));
        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [target], SquadDeploymentScope.Global,
            serverName);

        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off),
            SquadOperatorChannelFixture.FileDigests(on));
        Assert.Single(on.Degradations, d => d.Code == SquadOperatorChannelFixture.NotRenderedDegradation);
    }

    public static IEnumerable<object?[]> NonCopilotTargetsWithBadServerName()
    {
        foreach (SquadTarget target in SquadTargetCatalog.All.Where(static t => t != SquadTarget.Copilot))
        {
            yield return [target, null];
            yield return [target, "bad name zz1"];
        }
    }

    public static IEnumerable<object[]> NonCopilotTargets()
    {
        return SquadTargetCatalog.All
            .Where(static target => target != SquadTarget.Copilot)
            .Select(static target => new object[] { target });
    }

    /// <summary>
    /// Guards the default-off path: with the gate unset or any value other than the exact string
    /// <c>true</c>, all 13 targets in both scopes render byte-identically to a render with the gate
    /// unset, carry the same degradations and warnings, and never mention <c>operator-channel</c>.
    /// The server is present and named, so the only thing that could differ is the gate itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefaultOffCases))]
    public async Task DefaultOffParity_AllThirteenTargetsMatchTheUnsetGateRender(
        SquadDeploymentScope scope,
        string gateValue)
    {
        string copilot = DefaultOffFixture.CopilotHome;
        string home = DefaultOffFixture.HomeDirectory;

        SquadRenderResult baseline = await DefaultOffBaselines.GetOrAdd(
            scope,
            s => RenderAsync(
                copilot, home, null, SquadTargetCatalog.All, s));
        SquadRenderResult candidate = await RenderAsync(
            copilot, home, gateValue, SquadTargetCatalog.All, scope);

        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(baseline),
            SquadOperatorChannelFixture.FileDigests(candidate));
        Assert.Equal(baseline.Degradations.ToArray(), candidate.Degradations.ToArray());
        Assert.Equal(baseline.Warnings.ToArray(), candidate.Warnings.ToArray());
        Assert.Equal(baseline.Errors.ToArray(), candidate.Errors.ToArray());
        Assert.Empty(SquadOperatorChannelFixture.OperatorChannelMentions(candidate));
    }

    public static IEnumerable<object[]> DefaultOffCases()
    {
        foreach (SquadDeploymentScope scope in new[] { SquadDeploymentScope.Project, SquadDeploymentScope.Global })
        {
            foreach (string value in SquadOperatorChannelFixture.NonTrueGateValues)
            {
                yield return [scope, value];
            }
        }
    }

    /// <summary>
    /// An overlay the process cannot read must fail the render as <see cref="SquadRenderValidationException"/> that names
    /// the overlay path and the cause. A raw <see cref="UnauthorizedAccessException"/> escaping the renderer reaches the
    /// operator as a stack trace instead of a render error with a path to fix.
    /// </summary>
    [Fact]
    public async Task OperatorChannelOverlay_UnreadableOverlay_FailsTheRenderWithAValidationErrorNamingThePathAndCause()
    {
        CanonicalSquadTree tree = CanonicalSquadTree.Create();
        string overlay = tree.PathOf("agents/conductor/overlays/operator-channel.md");
        string home = Path.Combine(tree.Files.Root, "home");
        string copilot = Path.Combine(tree.Files.Root, "copilot-home");
        tree.Files.CreateDirectory(home);
        tree.Files.CreateDirectory(copilot);
        tree.Files.WriteAllText(Path.Combine(copilot, "mcp-config.json"), SquadOperatorChannelFixture.ServerPresentConfig);
        const string Denied = "Access to the overlay is denied.";
        tree.Files.FailWhen(
            operation => operation.Name == "ReadAllText" && operation.Path == overlay,
            new UnauthorizedAccessException(Denied));

        SquadRenderValidationException failure = await Assert.ThrowsAsync<SquadRenderValidationException>(
            () => RenderAsync(copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global, tree: tree));

        Assert.Contains(overlay, failure.Message, StringComparison.Ordinal);
        Assert.Contains(Denied, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Copilot root is resolved from COPILOT_HOME, and the probe must read the server from that directory. A server
    /// declared only in the default home (<c>~/.copilot</c>) must not count. Passes today; it guards against the probe
    /// reading a fixed location.
    /// </summary>
    [Fact]
    public async Task CopilotRoot_ProbeReadsTheResolvedCopilotHomeNotTheDefaultHome()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerConfig("other-relay"));
        string defaultHome = Path.Combine(home, ".copilot");
        Tree.Files.CreateDirectory(defaultHome);
        Tree.Files.WriteAllText(
            Path.Combine(defaultHome, "mcp-config.json"),
            SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

        Assert.DoesNotContain(
            SquadOperatorChannelFixture.ToolEntry,
            SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(on)));
        Assert.Single(on.Warnings, w => w.Code == SquadOperatorChannelFixture.ServerAbsentWarning);
    }

    /// <summary>
    /// The Copilot root decides where the probe looks, so it must be resolved once per render and that one answer must
    /// serve the probe. This resolver answers the first Copilot query with COPILOT_HOME and every later one with a
    /// directory that lacks the server. A renderer that resolves the root again for the probe loses the overlay, so this
    /// fails until the root is resolved once.
    /// </summary>
    [Fact]
    public async Task CopilotRoot_IsResolvedOncePerRender_SoTheProbeUsesTheSameRootAsTheLinks()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);
        string laterRoot = Path.Combine(home, "later-copilot-home");
        Tree.Files.CreateDirectory(laterRoot);
        ISquadGlobalRootResolver roots = new SecondCopilotAnswerResolver(
            copilot,
            laterRoot,
            SquadOperatorChannelFixture.GlobalRoots(copilot, home));

        SquadRenderResult on = await RenderAsync(
            copilot, home, "true", [SquadTarget.Copilot], SquadDeploymentScope.Global, roots: roots);

        Assert.Contains(
            SquadOperatorChannelFixture.ToolEntry,
            SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(on)));
    }

    /// <summary>Answers the first Copilot root query with one directory and every later one with another.</summary>
    private sealed class SecondCopilotAnswerResolver(
        string firstCopilotRoot,
        string laterCopilotRoot,
        ISquadGlobalRootResolver others) : ISquadGlobalRootResolver
    {
        private int _copilotAnswers;

        public string ResolveGlobalRoot(SquadTarget target)
        {
            if (target != SquadTarget.Copilot)
            {
                return others.ResolveGlobalRoot(target);
            }

            _copilotAnswers++;
            return _copilotAnswers == 1 ? firstCopilotRoot : laterCopilotRoot;
        }
    }
}
