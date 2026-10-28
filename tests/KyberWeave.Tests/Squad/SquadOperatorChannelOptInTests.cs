using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
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
/// Facts are black-box through <see cref="SquadCommandComposition.ResolveRenderer"/> and
/// <see cref="SquadRenderRequest"/>. The default-off parity facts guard against regressions: with the gate
/// unset or set to any value other than the exact string <c>true</c>, every output must match a render with
/// the gate unset, and no <c>operator-channel</c> text may appear anywhere. The environment is mutated, so
/// this class joins a non-parallel collection; no other class in the assembly writes the gate or the server
/// variable.
/// </remarks>
[Collection(SquadOperatorChannelCollection.Name)]
public sealed class SquadOperatorChannelOptInTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Creates an isolated COPILOT_HOME with an optional <c>mcp-config.json</c> and a fake user home.
    /// Each call gets its own directory, so theory cases never share state.
    /// </summary>
    private (string CopilotHome, string HomeDirectory) NewFixture(string? mcpConfigJson)
    {
        string root = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        string copilotHome = Path.Combine(root, "copilot-home");
        string homeDirectory = Path.Combine(root, "home");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(homeDirectory);
        if (mcpConfigJson is not null)
        {
            File.WriteAllText(Path.Combine(copilotHome, "mcp-config.json"), mcpConfigJson, Encoding.UTF8);
        }

        return (copilotHome, homeDirectory);
    }

    [Fact]
    public async Task OperatorChannelGate_ExactTrueWithServerFound_AddsExactlyOneToolEntryToCopilotGlobalConductor()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

        IReadOnlyList<string> offTools = SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(off));
        IReadOnlyList<string> onTools = SquadOperatorChannelFixture.ToolEntries(SquadOperatorChannelFixture.ConductorText(on));

        Assert.Equal(
            offTools.Append(SquadOperatorChannelFixture.ToolEntry).ToArray(),
            onTools.ToArray());
    }

    [Fact]
    public async Task OperatorChannelGate_ExactTrueWithServerFound_InsertsOverlayTextIntoCopilotGlobalConductorBody()
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);
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

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

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

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

        Assert.Equal(
            SquadOperatorChannelFixture.FileDigests(off).Where(IsNotConductor).ToArray(),
            SquadOperatorChannelFixture.FileDigests(on).Where(IsNotConductor).ToArray());
        Assert.Equal(
            off.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray(),
            on.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray());

        static bool IsNotConductor(string digest) =>
            !digest.Contains("conductor.agent.md", StringComparison.Ordinal);
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

        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global,
            serverName: serverName);

        string conductor = SquadOperatorChannelFixture.ConductorText(on);
        Assert.Contains($"'{serverName}/*'", SquadOperatorChannelFixture.ToolEntries(conductor), StringComparer.Ordinal);
        Assert.Contains(serverName, conductor, StringComparison.Ordinal);

        string[] texts = SquadOperatorChannelFixture.FileTexts(on);
        Assert.Empty(texts.Where(t => t.Contains(SquadOperatorChannelFixture.Placeholder, StringComparison.Ordinal)).ToArray());
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

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global);

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
        yield return ["other-server-names-only", "{\"mcpServers\":{\"codegraph\":{\"command\":\"x\"},\"other-relay\":{\"command\":\"y\"}}}"];
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
    public async Task OperatorChannelGate_ServerNameMissing_RendersDefaultOutputPlusNameMissingWarning(string? serverName)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global,
            serverName: serverName);

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
    public async Task OperatorChannelGate_ServerNameInvalid_RendersDefaultOutputAndNeverEchoesTheRawValue(string serverName)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [SquadTarget.Copilot], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Global,
            serverName: serverName);

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

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [SquadTarget.Copilot], SquadDeploymentScope.Project);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [SquadTarget.Copilot], SquadDeploymentScope.Project);

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
            requiredDegradationCode: SquadOperatorChannelFixture.NotRenderedDegradation));
    }

    [Theory]
    [MemberData(nameof(NonCopilotTargets))]
    public async Task OperatorChannelGate_EveryOtherTargetRecordsNotRenderedDegradationAndKeepsDefaultOutput(
        SquadTarget target)
    {
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [target], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [target], SquadDeploymentScope.Global);

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
            requiredDegradationCode: SquadOperatorChannelFixture.NotRenderedDegradation));
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

        SquadRenderResult off = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, [target], SquadDeploymentScope.Global);
        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: "true", [target], SquadDeploymentScope.Global,
            serverName: serverName);

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

    public static IEnumerable<object[]> NonCopilotTargets() =>
        SquadTargetCatalog.All
            .Where(static target => target != SquadTarget.Copilot)
            .Select(static target => new object[] { target });

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
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        SquadRenderResult baseline = await SquadOperatorChannelFixture.RenderAsync(
            copilot, home, gateValue: null, SquadTargetCatalog.All, scope);
        SquadRenderResult candidate = await SquadOperatorChannelFixture.RenderAsync(
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

    [Fact]
    public void OperatorChannelOverlay_IsPresentAsTheContractFile()
    {
        Assert.True(
            File.Exists(SquadOperatorChannelFixture.OverlayPath),
            $"The operator-channel overlay must exist at {SquadOperatorChannelFixture.OverlayPath}.");
    }

    [Theory]
    [InlineData("Teams")]
    [InlineData("APIM")]
    [InlineData("Azure")]
    public void OperatorChannelOverlay_NamesNeitherTheProductNorItsHosting(string forbiddenTerm)
    {
        string overlay = SquadOperatorChannelFixture.OverlayText();

        Assert.DoesNotContain(forbiddenTerm, overlay, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The overlay is generic: it carries the placeholder that the renderer replaces with the configured
    /// server name, and it hard-codes no server alias.
    /// </summary>
    [Fact]
    public void OperatorChannelOverlay_UsesThePlaceholderAndHardCodesNoServerAlias()
    {
        string overlay = SquadOperatorChannelFixture.OverlayText();

        Assert.Contains(SquadOperatorChannelFixture.Placeholder, overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-channel/*", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("`operator-channel`", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorChannelOverlay_NamesTheThreeEventsAndNothingElse()
    {
        string overlay = SquadOperatorChannelFixture.OverlayText();

        foreach (string eventLabel in SquadOperatorChannelFixture.EventLabels)
        {
            Assert.Contains(eventLabel, overlay, StringComparison.Ordinal);
        }

        Assert.Contains("Send nothing else.", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorChannelOverlay_BoundsEachReceiveAtFiveMinutesAndAtMostSixCalls()
    {
        string overlay = SquadOperatorChannelFixture.OverlayText();

        Assert.Contains("up to 5 minutes", overlay, StringComparison.Ordinal);
        Assert.Contains("at most 6 receive calls", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorChannelOverlay_StatesThatSupervisedModeNeverUsesIt()
    {
        string overlay = SquadOperatorChannelFixture.OverlayText();

        Assert.Contains("Supervised mode never uses it", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// An overlay the process cannot read must fail the render as <see cref="SquadRenderValidationException"/> that names
    /// the overlay path and the cause. A raw <see cref="UnauthorizedAccessException"/> escaping the renderer reaches the
    /// operator as a stack trace instead of a render error with a path to fix. Unix-only: file modes are not enforced
    /// on Windows, and a root process reads a mode-000 file, so both skip with a reason.
    /// </summary>
    [Fact]
    public async Task OperatorChannelOverlay_UnreadableOverlay_FailsTheRenderWithAValidationErrorNamingThePathAndCause()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are not enforced on Windows, so an unreadable overlay cannot be simulated.");
            return;
        }

        string sourceCopy = Path.Combine(_temp.Path, "source-copy");
        CopyTree(SquadOperatorChannelFixture.ProductRoot, sourceCopy);
        string overlay = Path.Combine(sourceCopy, "agents", "conductor", "overlays", "operator-channel.md");
        (string copilot, string home) = NewFixture(SquadOperatorChannelFixture.ServerPresentConfig);

        File.SetUnixFileMode(overlay, UnixFileMode.None);
        try
        {
            string deniedMessage = DeniedReadMessage(overlay);

            SquadRenderValidationException failure = await Assert.ThrowsAsync<SquadRenderValidationException>(
                () => SquadOperatorChannelFixture.RunWithGateAsync("true", copilot, async () =>
                {
                    ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
                    return await renderer.RenderAsync(new SquadRenderRequest(
                        SourceDirectory: sourceCopy,
                        Targets: [SquadTarget.Copilot],
                        Scope: SquadDeploymentScope.Global,
                        GlobalRoots: SquadOperatorChannelFixture.GlobalRoots(copilot, home)));
                }));

            Assert.Contains(overlay, failure.Message, StringComparison.Ordinal);
            Assert.Contains(deniedMessage, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(overlay, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
        Directory.CreateDirectory(defaultHome);
        await File.WriteAllTextAsync(
            Path.Combine(defaultHome, "mcp-config.json"),
            SquadOperatorChannelFixture.ServerPresentConfig,
            Encoding.UTF8);

        SquadRenderResult on = await SquadOperatorChannelFixture.RenderAsync(
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
        Directory.CreateDirectory(laterRoot);
        ISquadGlobalRootResolver roots = new SecondCopilotAnswerResolver(
            copilot,
            laterRoot,
            SquadOperatorChannelFixture.GlobalRoots(copilot, home));

        SquadRenderResult on = await SquadOperatorChannelFixture.RunWithGateAsync("true", copilot, async () =>
        {
            ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
            return await renderer.RenderAsync(new SquadRenderRequest(
                SourceDirectory: SquadOperatorChannelFixture.ProductRoot,
                Targets: [SquadTarget.Copilot],
                Scope: SquadDeploymentScope.Global,
                GlobalRoots: roots));
        });

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

    /// <summary>Returns the denial message the process gets reading <paramref name="path"/>, or skips when it can read it.</summary>
    private static string DeniedReadMessage(string path)
    {
        try
        {
            _ = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (UnauthorizedAccessException denied)
        {
            return denied.Message;
        }

        Assert.Skip("The process can read a file with mode 000 (running as root?), so an unreadable overlay cannot be simulated.");
        throw new InvalidOperationException("Unreachable: Assert.Skip always throws.");
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0 };
        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", options))
        {
            string target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopyTree(directory.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }
}

/// <summary>Non-parallel collection for every test that mutates the operator-channel gate, its server name, or COPILOT_HOME.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection definitions are named <Name>Collection by convention; "
        + "the suffix is the framework idiom, not a claim to be a collection type.")]
public sealed class SquadOperatorChannelCollection
{
    public const string Name = "Squad operator channel gate";
}

/// <summary>Shared fixture for the operator channel facts: paths, the gate and server variables, and render and parsing helpers.</summary>
internal static class SquadOperatorChannelFixture
{
    internal const string GateVariable = "KYBER_SQUAD_OPERATOR_CHANNEL";
    internal const string ServerVariable = "KYBER_SQUAD_OPERATOR_CHANNEL_SERVER";
    internal const string CopilotHomeVariable = "COPILOT_HOME";
    internal const string DefaultServerName = "ops-relay";
    internal const string ToolEntry = "'ops-relay/*'";
    internal const string Placeholder = "{{operator-channel-server}}";
    internal const string ServerAbsentWarning = "operator-channel-server-absent";
    internal const string NameMissingWarning = "operator-channel-server-name-missing";
    internal const string NameInvalidWarning = "operator-channel-server-name-invalid";
    internal const string NotRenderedDegradation = "operator-channel-not-rendered";
    internal const int ConductorBodyLimit = 30000;

    internal const string ServerPresentConfig =
        "{\"mcpServers\":{\"ops-relay\":{\"command\":\"ops-relay-server\",\"args\":[]}}}";

    internal static readonly string[] EventLabels =
    [
        "Hard-block question.",
        "Final report.",
        "Blocked or failed run status."
    ];

    internal static readonly string[] NonTrueGateValues =
    [
        "",
        " ",
        "True",
        "TRUE",
        "1",
        "yes",
        "false",
        " true",
        "true "
    ];

    internal static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    internal static readonly string OverlayPath =
        Path.Combine(ProductRoot, "agents", "conductor", "overlays", "operator-channel.md");

    private static readonly Regex ToolsFlowSequence =
        new(@"^tools:\s*\[(?<items>.*)\]\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A mcp-config.json that declares exactly one server, <paramref name="serverName"/>.</summary>
    internal static string ServerConfig(string serverName) =>
        "{\"mcpServers\":{\"" + serverName + "\":{\"command\":\"x\",\"args\":[]}}}";

    /// <summary>
    /// Runs an async body with the gate, the server variable, and COPILOT_HOME set, restoring all three
    /// afterwards. A null <paramref name="serverName"/> leaves the server variable unset.
    /// </summary>
    internal static async Task<T> RunWithGateAsync<T>(
        string? gateValue,
        string copilotHome,
        Func<Task<T>> body,
        string? serverName = DefaultServerName)
    {
        string? previousGate = Environment.GetEnvironmentVariable(GateVariable);
        string? previousServer = Environment.GetEnvironmentVariable(ServerVariable);
        string? previousCopilotHome = Environment.GetEnvironmentVariable(CopilotHomeVariable);
        Environment.SetEnvironmentVariable(GateVariable, gateValue);
        Environment.SetEnvironmentVariable(ServerVariable, serverName);
        Environment.SetEnvironmentVariable(CopilotHomeVariable, copilotHome);
        try
        {
            return await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(GateVariable, previousGate);
            Environment.SetEnvironmentVariable(ServerVariable, previousServer);
            Environment.SetEnvironmentVariable(CopilotHomeVariable, previousCopilotHome);
        }
    }

    /// <summary>Runs a synchronous body with the gate, the server variable, and COPILOT_HOME set, restoring all three afterwards.</summary>
    internal static T RunWithGate<T>(
        string? gateValue,
        string copilotHome,
        Func<T> body,
        string? serverName = DefaultServerName)
    {
        string? previousGate = Environment.GetEnvironmentVariable(GateVariable);
        string? previousServer = Environment.GetEnvironmentVariable(ServerVariable);
        string? previousCopilotHome = Environment.GetEnvironmentVariable(CopilotHomeVariable);
        Environment.SetEnvironmentVariable(GateVariable, gateValue);
        Environment.SetEnvironmentVariable(ServerVariable, serverName);
        Environment.SetEnvironmentVariable(CopilotHomeVariable, copilotHome);
        try
        {
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(GateVariable, previousGate);
            Environment.SetEnvironmentVariable(ServerVariable, previousServer);
            Environment.SetEnvironmentVariable(CopilotHomeVariable, previousCopilotHome);
        }
    }

    /// <summary>
    /// Resolves global roots so that the Copilot global root is the given COPILOT_HOME directory, the
    /// same location the probe reads <c>mcp-config.json</c> from.
    /// </summary>
    internal static ISquadGlobalRootResolver GlobalRoots(string copilotHome, string homeDirectory) =>
        new SquadGlobalRoots(
            name => string.Equals(name, CopilotHomeVariable, StringComparison.Ordinal) ? copilotHome : null,
            homeDirectory);

    internal static async Task<SquadRenderResult> RenderAsync(
        string copilotHome,
        string homeDirectory,
        string? gateValue,
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope,
        string? serverName = DefaultServerName)
    {
        return await RunWithGateAsync(gateValue, copilotHome, async () =>
        {
            ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
            SquadRenderResult result = await renderer.RenderAsync(new SquadRenderRequest(
                SourceDirectory: ProductRoot,
                Targets: targets,
                Scope: scope,
                GlobalRoots: GlobalRoots(copilotHome, homeDirectory)));
            Assert.True(result.Success, string.Join("; ", result.Errors));
            return result;
        }, serverName);
    }

    internal static IReadOnlyList<string> FileDigests(SquadRenderResult result) =>
        result.Files
            .Select(static file => $"{file.RelativePath} {Convert.ToHexString(SHA256.HashData(file.Content.Span))}")
            .ToArray();

    internal static string[] FileTexts(SquadRenderResult result) =>
        result.Files
            .Select(static file => Encoding.UTF8.GetString(file.Content.Span))
            .ToArray();

    internal static string ConductorText(SquadRenderResult result)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            static f => f.RelativePath.EndsWith("conductor.agent.md", StringComparison.Ordinal));
        return Encoding.UTF8.GetString(file.Content.Span);
    }

    /// <summary>The entries of the conductor frontmatter <c>tools:</c> flow sequence, in order.</summary>
    internal static IReadOnlyList<string> ToolEntries(string markdown)
    {
        Match match = ToolsFlowSequence.Match(markdown);
        Assert.True(match.Success, "The conductor frontmatter has no `tools:` flow sequence.");
        return match.Groups["items"].Value
            .Split(',')
            .Select(static item => item.Trim())
            .ToArray();
    }

    /// <remarks>
    /// <paramref name="requiredDegradationCode"/> names the one degradation a fact expects, such as
    /// <c>operator-channel-not-rendered</c>. Only degradations carrying that exact code are skipped,
    /// because the code itself contains <c>operator-channel</c>. Every other degradation, every
    /// generated file, and every warning is still scanned, so leakage elsewhere still fails.
    /// </remarks>
    internal static string[] OperatorChannelMentions(
        SquadRenderResult result,
        string? requiredDegradationCode = null)
    {
        IEnumerable<string> texts = result.Files
            .Select(static file => Encoding.UTF8.GetString(file.Content.Span))
            .Concat(result.Degradations
                .Where(d => d.Code != requiredDegradationCode)
                .Select(static d => $"{d.Target} {d.Code} {d.Details}"))
            .Concat(result.Warnings.Select(static w => $"{w.Code} {w.Message} {w.Target}"));

        return texts
            .Where(static text => text.Contains("operator-channel", StringComparison.Ordinal))
            .ToArray();
    }

    internal static string OverlayText()
    {
        Assert.True(File.Exists(OverlayPath), $"The operator-channel overlay is missing at {OverlayPath}.");
        return File.ReadAllText(OverlayPath, Encoding.UTF8);
    }

    /// <summary>
    /// The overlay's first prose paragraph, read from the file with the placeholder replaced by the default
    /// server name, so the body check follows the file as the renderer will emit it.
    /// </summary>
    internal static string OverlayFirstParagraph() =>
        OverlayText()
            .Split('\n')
            .Select(static line => line.Trim())
            .First(static line => line.Length > 0 && !line.StartsWith('#'))
            .Replace(Placeholder, DefaultServerName, StringComparison.Ordinal);
}
