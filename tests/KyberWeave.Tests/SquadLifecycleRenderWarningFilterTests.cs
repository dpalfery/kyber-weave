using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins which renderer warnings the install and update lifecycle results carry to the CLI. Only the
/// operator channel's missing-server warning may reach the operator. The agent body length warning
/// must stay off the install and update output, so a default install prints exactly what it printed
/// before the operator channel existed.
/// </summary>
/// <remarks>
/// This is a post-hoc pin: the filter in <c>SquadLifecycleService.RenderWarningMessages</c> already
/// exists on this branch, so these facts pass on arrival. They fail if someone widens the filter to
/// pass every renderer warning through. Each fact first asserts that the renderer really produced the
/// warning it is about, so a fixture that stops triggering it cannot make a fact pass vacuously.
/// The gate and COPILOT_HOME are process-wide, so this class shares the non-parallel collection that
/// <see cref="SquadOperatorChannelCollection"/> defines.
/// </remarks>
[Collection(SquadOperatorChannelCollection.Name)]
public sealed class SquadLifecycleRenderWarningFilterTests : IDisposable
{
    private const string AgentBodyTooLongCode = "agent-body-too-long";
    private const string ServerAbsentCode = SquadOperatorChannelFixture.ServerAbsentWarning;
    private const string InstallVersion = "1.2.3";
    private const string UpdateVersion = "1.2.4";

    /// <summary>The newline before and after the padding text that <see cref="PaddedConductorReleaseSource"/> appends.</summary>
    private const int PaddingFramingCharacters = 2;

    private readonly TempDirectory _temp = new();
    private readonly List<Harness> _harnesses = [];

    public void Dispose()
    {
        foreach (Harness harness in _harnesses)
        {
            harness.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>
    /// An install whose conductor breaches the agent body cap must not put the
    /// <c>agent-body-too-long</c> warning on the lifecycle result. Without the filter a default install
    /// prints a warning it never printed before.
    /// </summary>
    [Fact]
    public async Task Install_AgentBodyOverCopilotCap_IsNotCarriedOntoTheLifecycleResult()
    {
        Harness h = NewHarness("install-body");

        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            null,
            h.CopilotHome,
            () => InstallAsync(h, true));

        Assert.True(
            h.Renderer.Codes.Contains(AgentBodyTooLongCode),
            $"Precondition: the fixture must make the renderer emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        IReadOnlyList<string> warnings = WarningsOf(result);
        Assert.True(
            warnings.Count == 0,
            $"A default install must show no renderer warning, but the result carries: {string.Join("; ", warnings)}");
    }

    /// <summary>
    /// The same guarantee on the update path, which runs its own warning projection. An install at the
    /// original version, then an update over the cap, must still show nothing for the body length.
    /// </summary>
    [Fact]
    public async Task Update_AgentBodyOverCopilotCap_IsNotCarriedOntoTheLifecycleResult()
    {
        Harness h = NewHarness("update-body");

        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            null,
            h.CopilotHome,
            async () =>
            {
                await InstallAsync(h, false);
                return await NewService(h, h.Oversized).UpdateAsync(
                    new SquadUpdateRequest(
                        h.Anchor,
                        SquadDeploymentScope.Global,
                        [SquadTarget.Copilot],
                        Version: UpdateVersion));
            });

        Assert.True(
            h.Renderer.Codes.Contains(AgentBodyTooLongCode),
            $"Precondition: the fixture must make the renderer emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        IReadOnlyList<string> warnings = WarningsOf(result);
        Assert.True(
            warnings.Count == 0,
            $"A default update must show no renderer warning, but the result carries: {string.Join("; ", warnings)}");
    }

    /// <summary>
    /// With the gate on and no server with the configured name declared in mcp-config.json, the operator channel warning must
    /// reach the install result, prefixed with its code. This is the one renderer warning the CLI shows.
    /// </summary>
    [Fact]
    public async Task Install_GateOnServerAbsent_ShowsTheOperatorChannelWarningOnTheLifecycleResult()
    {
        Harness h = NewHarness("install-absent");

        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            "true",
            h.CopilotHome,
            () => InstallAsync(h, false));

        Assert.True(
            h.Renderer.Codes.Contains(ServerAbsentCode),
            $"Precondition: the renderer must emit '{ServerAbsentCode}', but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        string warning = Assert.Single(WarningsOf(result));
        Assert.StartsWith(ServerAbsentCode + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// One render that produces both warnings must surface only the operator channel's. The body length
    /// warning is filtered out and the operator channel one is kept, so the filter cannot be satisfied by
    /// either dropping everything or passing everything.
    /// </summary>
    [Fact]
    public async Task Install_OversizedConductorAndServerAbsentInOneRender_ShowsOnlyTheOperatorChannelWarning()
    {
        Harness h = NewHarness("install-both");

        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            "true",
            h.CopilotHome,
            () => InstallAsync(h, true));

        Assert.True(
            h.Renderer.Codes.Contains(AgentBodyTooLongCode) && h.Renderer.Codes.Contains(ServerAbsentCode),
            $"Precondition: one render must emit both codes, but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        IReadOnlyList<string> warnings = WarningsOf(result);
        string warning = Assert.Single(warnings);
        Assert.StartsWith(ServerAbsentCode + ": ", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(
            warnings,
            line => line.StartsWith(AgentBodyTooLongCode, StringComparison.Ordinal));
    }

    /// <summary>
    /// With the gate on and <c>KYBER_SQUAD_OPERATOR_CHANNEL_SERVER</c> unset, the name-missing warning is an
    /// operator channel warning, so it must reach the install result for the operator to see.
    /// </summary>
    [Fact]
    public async Task Install_GateOnServerNameMissing_ShowsTheNameMissingWarningOnTheLifecycleResult()
    {
        Harness h = NewHarness("install-name-missing");

        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            "true",
            h.CopilotHome,
            () => InstallAsync(h, false),
            serverName: null);

        Assert.True(
            h.Renderer.Codes.Contains(SquadOperatorChannelFixture.NameMissingWarning),
            $"Precondition: the renderer must emit '{SquadOperatorChannelFixture.NameMissingWarning}', but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        string warning = Assert.Single(WarningsOf(result));
        Assert.StartsWith(SquadOperatorChannelFixture.NameMissingWarning + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the gate on and an invalid server name, the name-invalid warning is an operator channel warning
    /// and must reach the install result. The raw value stays out of the warning text.
    /// </summary>
    [Fact]
    public async Task Install_GateOnServerNameInvalid_ShowsTheNameInvalidWarningOnTheLifecycleResult()
    {
        Harness h = NewHarness("install-name-invalid");

        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            "true",
            h.CopilotHome,
            () => InstallAsync(h, false),
            serverName: "bad name zz1");

        Assert.True(
            h.Renderer.Codes.Contains(SquadOperatorChannelFixture.NameInvalidWarning),
            $"Precondition: the renderer must emit '{SquadOperatorChannelFixture.NameInvalidWarning}', but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        string warning = Assert.Single(WarningsOf(result));
        Assert.StartsWith(SquadOperatorChannelFixture.NameInvalidWarning + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlay is what pushes this conductor over the cap: the body alone fits, and body plus overlay does not.
    /// The operator channel caused the overflow, so <c>agent-body-too-long</c> must reach the install result and the
    /// operator sees a hint. Without the overlay the same body stays silent, which the body-alone precondition proves.
    /// </summary>
    [Fact]
    public async Task Install_OverlayPushesConductorOverCap_ShowsTheAgentBodyTooLongWarningOnTheLifecycleResult()
    {
        Harness h = NewHarness("install-overlay-overflow");
        WriteServerConfig(h);
        int padding = await OverflowPaddingForOverlayAsync(h);
        await AssertBodyAloneFitsCapAsync(padding);

        using PaddedConductorReleaseSource padded = new(padding);
        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            "true",
            h.CopilotHome,
            () => InstallFrom(h, padded, InstallVersion));

        AssertOverflowWarningShown(h, result);
    }

    /// <summary>
    /// The same guarantee on the update path. The install runs on the checked-in corpus, which fits the cap, and the
    /// update lands the padded conductor, so the overflow only the overlay causes is the one the update reports.
    /// </summary>
    [Fact]
    public async Task Update_OverlayPushesConductorOverCap_ShowsTheAgentBodyTooLongWarningOnTheLifecycleResult()
    {
        Harness h = NewHarness("update-overlay-overflow");
        WriteServerConfig(h);
        int padding = await OverflowPaddingForOverlayAsync(h);
        await AssertBodyAloneFitsCapAsync(padding);

        using PaddedConductorReleaseSource padded = new(padding);
        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            "true",
            h.CopilotHome,
            async () =>
            {
                await InstallFrom(h, h.Corpus, InstallVersion);
                return await NewService(h, padded).UpdateAsync(
                    new SquadUpdateRequest(
                        h.Anchor,
                        SquadDeploymentScope.Global,
                        [SquadTarget.Copilot],
                        Version: UpdateVersion));
            });

        AssertOverflowWarningShown(h, result);
    }

    /// <summary>
    /// When the overlay is not applied, the conductor is the default one, so the oversized body must not put
    /// <c>agent-body-too-long</c> on the install result. Each case keeps the overlay off through a different door: the
    /// gate, an absent server, an unset name, or an invalid name. The renderer still emits the warning in each case,
    /// which the precondition proves, so a pass cannot be vacuous.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotAppliedCases))]
    public async Task Install_OverlayNotApplied_KeepsAgentBodyTooLongOffTheLifecycleResult(
        string variant,
        string? gateValue,
        string? serverName,
        bool declareServer)
    {
        Harness h = NewHarness("install-not-applied-" + variant);
        if (declareServer)
        {
            WriteServerConfig(h);
        }

        SquadLifecycleResult result = await SquadOperatorChannelFixture.RunWithGateAsync(
            gateValue,
            h.CopilotHome,
            () => InstallFrom(h, h.Oversized, InstallVersion),
            serverName);

        Assert.True(
            h.Renderer.Codes.Contains(AgentBodyTooLongCode),
            $"Precondition ({variant}): the renderer must emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        Assert.DoesNotContain(
            WarningsOf(result),
            line => line.StartsWith(AgentBodyTooLongCode, StringComparison.Ordinal));
    }

    public static IEnumerable<object?[]> NotAppliedCases()
    {
        yield return ["gate-off", null, SquadOperatorChannelFixture.DefaultServerName, true];
        yield return ["server-absent", "true", SquadOperatorChannelFixture.DefaultServerName, false];
        yield return ["name-missing", "true", null, true];
        yield return ["name-invalid", "true", "bad name zz1", true];
    }

    private static void AssertOverflowWarningShown(Harness h, SquadLifecycleResult result)
    {
        Assert.True(
            h.Renderer.Codes.Contains(AgentBodyTooLongCode),
            $"Precondition: the overlay-applied render must emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", h.Renderer.Codes)}.");
        string warning = Assert.Single(WarningsOf(result));
        Assert.StartsWith(AgentBodyTooLongCode + ": ", warning, StringComparison.Ordinal);
        Assert.Contains("'conductor'", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The padding that places the body alone just under the cap and body plus overlay just over it. The overlay's growth
    /// is measured from two real renders, so the fixture follows the checked-in overlay rather than a copied length.
    /// </summary>
    private static async Task<int> OverflowPaddingForOverlayAsync(Harness h)
    {
        int offLength = await ConductorLengthAsync(h, null);
        int onLength = await ConductorLengthAsync(h, "true");
        int growth = onLength - offLength;
        Assert.True(
            growth > 0,
            $"Precondition: the overlay must add text to the conductor (default {offLength} chars, gated {onLength} chars).");

        // Centred on the cap: the body alone ends half the overlay's growth under it, and the overlay carries it over.
        return SquadOperatorChannelFixture.ConductorBodyLimit - offLength - growth / 2 - PaddingFramingCharacters;
    }

    /// <summary>
    /// Proves the precondition the overflow facts rest on: with the overlay off, the padded conductor does not exceed
    /// the cap, so any overflow with the overlay on is the overlay's doing.
    /// </summary>
    private async Task AssertBodyAloneFitsCapAsync(int padding)
    {
        Harness probe = NewHarness("body-alone-probe");
        using PaddedConductorReleaseSource padded = new(padding);
        await SquadOperatorChannelFixture.RunWithGateAsync(
            null,
            probe.CopilotHome,
            () => InstallFrom(probe, padded, InstallVersion));

        Assert.DoesNotContain(AgentBodyTooLongCode, probe.Renderer.Codes);
    }

    private static async Task<int> ConductorLengthAsync(Harness h, string? gateValue)
    {
        SquadRenderResult result = await SquadOperatorChannelFixture.RenderAsync(
            h.CopilotHome,
            h.UserHome,
            gateValue,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Global);
        return SquadOperatorChannelFixture.ConductorText(result).Length;
    }

    private static void WriteServerConfig(Harness h) =>
        File.WriteAllText(
            Path.Combine(h.CopilotHome, "mcp-config.json"),
            SquadOperatorChannelFixture.ServerPresentConfig,
            Encoding.UTF8);

    private static Task<SquadLifecycleResult> InstallFrom(Harness h, ISquadReleaseSource source, string version) =>
        NewService(h, source).InstallAsync(
            new SquadInstallRequest(
                h.Anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Copilot],
                Version: version));

    private static IReadOnlyList<string> WarningsOf(SquadLifecycleResult result)
    {
        return result.Warnings ?? throw new InvalidOperationException("The lifecycle result carries no warnings list.");
    }

    private static Task<SquadLifecycleResult> InstallAsync(Harness h, bool oversizedConductor)
    {
        ISquadReleaseSource releaseSource = oversizedConductor ? h.Oversized : h.Corpus;
        return NewService(h, releaseSource).InstallAsync(
            new SquadInstallRequest(
                h.Anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Copilot],
                Version: InstallVersion));
    }

    private static SquadLifecycleService NewService(Harness h, ISquadReleaseSource releaseSource)
    {
        return new SquadLifecycleService(releaseSource, h.Renderer, h.Store, globalRoots: h.GlobalRoots);
    }

    private Harness NewHarness(string name)
    {
        string root = Path.Combine(_temp.Path, name);
        string copilotHome = Path.Combine(root, "copilot-home");
        string anchor = Path.Combine(root, "anchor");
        string userHome = Path.Combine(root, "user-home");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(userHome);
        Harness harness = new(anchor, copilotHome, userHome);
        _harnesses.Add(harness);
        return harness;
    }

    /// <summary>
    /// The real checked-in corpus, with a long paragraph appended to the conductor so that the Copilot
    /// renderer emits <c>agent-body-too-long</c>. The corpus is copied into the extraction directory, so
    /// the repository tree is never written.
    /// </summary>
    private sealed class OversizedConductorReleaseSource : ISquadReleaseSource
    {
        /// <summary>Repeated until the conductor body is well past the 30,000 character Copilot agent cap.</summary>
        private const string PaddingSentence =
            "The conductor body is padded here so that the rendered agent file exceeds the Copilot custom agent cap. ";

        private readonly CorpusSquadReleaseSource _corpus = new();

        public async Task<SquadReleaseResult> DownloadAndExtractAsync(
            SquadReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            SquadReleaseResult result = await _corpus.DownloadAndExtractAsync(request, cancellationToken);
            string conductor = Path.Combine(result.ExtractionRoot, "agents", "conductor.md");
            string padding = string.Concat(Enumerable.Repeat(PaddingSentence, 600));
            await File.AppendAllTextAsync(conductor, "\n" + padding + "\n", cancellationToken);
            return result;
        }

        public void Dispose()
        {
            _corpus.Dispose();
        }
    }

    /// <summary>
    /// The real corpus with exactly <paramref name="paddingCharacters"/> characters of prose appended to the conductor.
    /// It puts the body plus overlay over the Copilot cap while the body alone stays under it. The corpus is copied into
    /// the extraction directory, so the repository tree is never written.
    /// </summary>
    private sealed class PaddedConductorReleaseSource(int paddingCharacters) : ISquadReleaseSource
    {
        private const string Sentence =
            "The conductor body is padded here so that the operator channel overlay pushes the agent file past the cap. ";

        private readonly CorpusSquadReleaseSource _corpus = new();

        public async Task<SquadReleaseResult> DownloadAndExtractAsync(
            SquadReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            SquadReleaseResult result = await _corpus.DownloadAndExtractAsync(request, cancellationToken);
            string repeated = string.Concat(Enumerable.Repeat(Sentence, paddingCharacters / Sentence.Length + 1));
            string padding = repeated[..paddingCharacters];
            string conductor = Path.Combine(result.ExtractionRoot, "agents", "conductor.md");
            await File.AppendAllTextAsync(conductor, "\n" + padding + "\n", cancellationToken);
            return result;
        }

        public void Dispose()
        {
            _corpus.Dispose();
        }
    }

    /// <summary>
    /// Records every warning code the real renderer emits for a render, so each fact can prove its
    /// precondition before it asserts what the lifecycle result carries. Delegates all behaviour unchanged.
    /// </summary>
    private sealed class RecordingRenderer(ISquadRenderer inner) : ISquadRenderer
    {
        public List<string> Codes { get; } = [];

        public IReadOnlyCollection<SquadTarget> SupportedTargets => inner.SupportedTargets;

        public async Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            SquadRenderResult result = await inner.RenderAsync(request, cancellationToken);
            Codes.AddRange(result.Warnings.Select(static warning => warning.Code));
            return result;
        }
    }

    /// <summary>One isolated install context: anchor, COPILOT_HOME, user paths, state store, renderer, and release sources.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(string anchor, string copilotHome, string userHome)
        {
            Anchor = anchor;
            CopilotHome = copilotHome;
            UserHome = userHome;
            Store = new SquadStateStore(new FakeSquadUserPaths(userHome));
            GlobalRoots = SquadOperatorChannelFixture.GlobalRoots(copilotHome, userHome);
            Renderer = new RecordingRenderer(SquadCommandComposition.ResolveRenderer());
            Corpus = new CorpusSquadReleaseSource();
            Oversized = new OversizedConductorReleaseSource();
        }

        public string Anchor { get; }

        public string CopilotHome { get; }

        public string UserHome { get; }

        public SquadStateStore Store { get; }

        public ISquadGlobalRootResolver GlobalRoots { get; }

        public RecordingRenderer Renderer { get; }

        public CorpusSquadReleaseSource Corpus { get; }

        public OversizedConductorReleaseSource Oversized { get; }

        public void Dispose()
        {
            Corpus.Dispose();
            Oversized.Dispose();
        }
    }
}
