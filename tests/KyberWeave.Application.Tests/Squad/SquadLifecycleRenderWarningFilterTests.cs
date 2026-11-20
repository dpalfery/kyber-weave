using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
///     Pins which renderer warnings the install and update lifecycle results carry to the CLI. Only the
///     operator channel's missing-server warning may reach the operator. The agent body length warning
///     must stay off the install and update output, so a default install prints exactly what it printed
///     before the operator channel existed.
/// </summary>
/// <remarks>
///     This is a post-hoc pin: the filter in <c>SquadLifecycleService.RenderWarningMessages</c> already
///     exists on this branch, so these facts pass on arrival. They fail if someone widens the filter to
///     pass every renderer warning through. Each fact first asserts that the renderer really produced the
///     warning it is about, so a fixture that stops triggering it cannot make a fact pass vacuously.
///     Environment variables are read through injectable delegates rather than process mutation,
///     allowing safe parallel test execution.
///     A fact whose subject is one install reads the record of one run-wide prepared install
///     (<see cref="PreparedSquadInstalls" />), keyed by the release, the operator channel gate, the server
///     name and whether an <c>mcp-config.json</c> was already in the home. The two update facts still run
///     their update: their subject is the sequence of an install followed by an update over it, so they
///     start from a copy of a prepared install instead of installing it themselves.
/// </remarks>
public sealed class SquadLifecycleRenderWarningFilterTests
{
    private const string AgentBodyTooLongCode = "agent-body-too-long";
    private const string ServerAbsentCode = SquadOperatorChannelFixture.ServerAbsentWarning;
    private const string InstallVersion = "1.2.3";
    private const string UpdateVersion = "1.2.4";

    /// <summary>The newline before and after the padding text the padded release appends.</summary>
    private const int PaddingFramingCharacters = 2;

    /// <summary>
    ///     An install whose conductor breaches the agent body cap must not put the
    ///     <c>agent-body-too-long</c> warning on the lifecycle result. Without the filter a default install
    ///     prints a warning it never printed before.
    /// </summary>
    [Fact]
    public async Task Install_AgentBodyOverCopilotCap_IsNotCarriedOntoTheLifecycleResult()
    {
        InstallResult prepared = await InstallCaseAsync(ReleaseKind.Oversized, gateValue: null, declareServer: false);

        AssertRendererEmitted(prepared, AgentBodyTooLongCode);
        IReadOnlyList<string> warnings = prepared.Warnings;
        Assert.True(
            warnings.Count == 0,
            $"A default install must show no renderer warning, but the result carries: {string.Join("; ", warnings)}");
    }

    /// <summary>
    ///     The same guarantee on the update path, which runs its own warning projection. An install at the
    ///     original version, then an update over the cap, must still show nothing for the body length.
    /// </summary>
    [Fact]
    public async Task Update_AgentBodyOverCopilotCap_IsNotCarriedOntoTheLifecycleResult()
    {
        Harness h = new();
        _ = await h.InstallAsync(ReleaseKind.Corpus, gateValue: null, declareServer: false);

        InstallResult result = await h.UpdateAsync(ReleaseKind.Oversized, gateValue: null);

        AssertRendererEmitted(result, AgentBodyTooLongCode);
        IReadOnlyList<string> warnings = result.Warnings;
        Assert.True(
            warnings.Count == 0,
            $"A default update must show no renderer warning, but the result carries: {string.Join("; ", warnings)}");
    }

    /// <summary>
    ///     With the gate on and no server with the configured name declared in mcp-config.json, the operator channel warning
    ///     must
    ///     reach the install result, prefixed with its code. This is the one renderer warning the CLI shows.
    /// </summary>
    [Fact]
    public async Task Install_GateOnServerAbsent_ShowsTheOperatorChannelWarningOnTheLifecycleResult()
    {
        InstallResult prepared = await InstallCaseAsync(ReleaseKind.Corpus, gateValue: "true", declareServer: false);

        AssertRendererEmitted(prepared, ServerAbsentCode);
        string warning = Assert.Single(prepared.Warnings);
        Assert.StartsWith(ServerAbsentCode + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One render that produces both warnings must surface only the operator channel's. The body length
    ///     warning is filtered out and the operator channel one is kept, so the filter cannot be satisfied by
    ///     either dropping everything or passing everything.
    /// </summary>
    [Fact]
    public async Task Install_OversizedConductorAndServerAbsentInOneRender_ShowsOnlyTheOperatorChannelWarning()
    {
        InstallResult prepared = await InstallCaseAsync(ReleaseKind.Oversized, gateValue: "true", declareServer: false);

        Assert.True(
            prepared.RenderCodes.Contains(AgentBodyTooLongCode) && prepared.RenderCodes.Contains(ServerAbsentCode),
            $"Precondition: one render must emit both codes, but it emitted: {string.Join(", ", prepared.RenderCodes)}.");
        IReadOnlyList<string> warnings = prepared.Warnings;
        string warning = Assert.Single(warnings);
        Assert.StartsWith(ServerAbsentCode + ": ", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(
            warnings,
            line => line.StartsWith(AgentBodyTooLongCode, StringComparison.Ordinal));
    }

    /// <summary>
    ///     With the gate on and <c>KYBER_SQUAD_OPERATOR_CHANNEL_SERVER</c> unset, the name-missing warning is an
    ///     operator channel warning, so it must reach the install result for the operator to see.
    /// </summary>
    [Fact]
    public async Task Install_GateOnServerNameMissing_ShowsTheNameMissingWarningOnTheLifecycleResult()
    {
        InstallResult prepared = await InstallCaseAsync(
            ReleaseKind.Corpus,
            gateValue: "true",
            declareServer: false,
            serverName: null);

        AssertRendererEmitted(prepared, SquadOperatorChannelFixture.NameMissingWarning);
        string warning = Assert.Single(prepared.Warnings);
        Assert.StartsWith(SquadOperatorChannelFixture.NameMissingWarning + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     With the gate on and an invalid server name, the name-invalid warning is an operator channel warning
    ///     and must reach the install result. The raw value stays out of the warning text.
    /// </summary>
    [Fact]
    public async Task Install_GateOnServerNameInvalid_ShowsTheNameInvalidWarningOnTheLifecycleResult()
    {
        InstallResult prepared = await InstallCaseAsync(
            ReleaseKind.Corpus,
            gateValue: "true",
            declareServer: false,
            serverName: "bad name zz1");

        AssertRendererEmitted(prepared, SquadOperatorChannelFixture.NameInvalidWarning);
        string warning = Assert.Single(prepared.Warnings);
        Assert.StartsWith(SquadOperatorChannelFixture.NameInvalidWarning + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The overlay is what pushes this conductor over the cap: the body alone fits, and body plus overlay does not.
    ///     The operator channel caused the overflow, so <c>agent-body-too-long</c> must reach the install result and the
    ///     operator sees a hint. Without the overlay the same body stays silent, which the body-alone precondition proves.
    /// </summary>
    [Fact]
    public async Task Install_OverlayPushesConductorOverCap_ShowsTheAgentBodyTooLongWarningOnTheLifecycleResult()
    {
        int padding = await OverflowPaddingAsync();
        await AssertBodyAloneFitsCapAsync(padding);

        InstallResult prepared = await InstallCaseAsync(
            ReleaseKind.Padded,
            gateValue: "true",
            declareServer: true,
            paddingCharacters: padding);

        AssertOverflowWarningShown(prepared);
    }

    /// <summary>
    ///     The same guarantee on the update path. The install runs on the checked-in corpus, which fits the cap, and the
    ///     update lands the padded conductor, so the overflow only the overlay causes is the one the update reports.
    /// </summary>
    [Fact]
    public async Task Update_OverlayPushesConductorOverCap_ShowsTheAgentBodyTooLongWarningOnTheLifecycleResult()
    {
        int padding = await OverflowPaddingAsync();
        await AssertBodyAloneFitsCapAsync(padding);

        Harness h = new();
        _ = await h.InstallAsync(ReleaseKind.Corpus, gateValue: "true", declareServer: true);
        InstallResult result = await h.UpdateAsync(ReleaseKind.Padded, gateValue: "true", paddingCharacters: padding);

        AssertOverflowWarningShown(result);
    }

    /// <summary>
    ///     When the overlay is not applied, the conductor is the default one, so the oversized body must not put
    ///     <c>agent-body-too-long</c> on the install result. Each case keeps the overlay off through a different door: the
    ///     gate, an absent server, an unset name, or an invalid name. The renderer still emits the warning in each case,
    ///     which the precondition proves, so a pass cannot be vacuous.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotAppliedCases))]
    public async Task Install_OverlayNotApplied_KeepsAgentBodyTooLongOffTheLifecycleResult(
        string variant,
        string? gateValue,
        string? serverName,
        bool declareServer)
    {
        InstallResult prepared = await InstallCaseAsync(
            ReleaseKind.Oversized,
            gateValue: gateValue,
            declareServer: declareServer,
            serverName: serverName);

        Assert.True(
            prepared.RenderCodes.Contains(AgentBodyTooLongCode),
            $"Precondition ({variant}): the renderer must emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", prepared.RenderCodes)}.");
        Assert.DoesNotContain(
            prepared.Warnings,
            line => line.StartsWith(AgentBodyTooLongCode, StringComparison.Ordinal));
    }

    public static IEnumerable<object?[]> NotAppliedCases()
    {
        yield return ["gate-off", null, SquadOperatorChannelFixture.DefaultServerName, true];
        yield return ["server-absent", "true", SquadOperatorChannelFixture.DefaultServerName, false];
        yield return ["name-missing", "true", null, true];
        yield return ["name-invalid", "true", "bad name zz1", true];
    }

    /// <summary>
    ///     Proves the guarantee the overflow facts are about: the conductor's own <c>agent-body-too-long</c> warning
    ///     reaches the lifecycle result, and it is the only kind of warning the result carries. The result may name
    ///     another shipped agent too, because the filter keeps every over-cap agent's warning once the overlay is
    ///     applied, so the fact selects the conductor's warning instead of requiring it to be the only one.
    /// </summary>
    private static void AssertOverflowWarningShown(InstallResult result)
    {
        Assert.True(
            result.ConductorLength > SquadOperatorChannelFixture.ConductorBodyLimit,
            $"Precondition: the overlay must carry the conductor past the " +
            $"{SquadOperatorChannelFixture.ConductorBodyLimit}-character cap, but it renders to {result.ConductorLength} characters.");
        string warning = Assert.Single(
            result.Warnings,
            line => line.Contains("'conductor'", StringComparison.Ordinal));
        Assert.StartsWith(AgentBodyTooLongCode + ": ", warning, StringComparison.Ordinal);
        Assert.All(
            result.Warnings,
            line => Assert.StartsWith(AgentBodyTooLongCode + ": ", line, StringComparison.Ordinal));
    }

    private static void AssertRendererEmitted(InstallResult prepared, string code)
    {
        Assert.True(
            prepared.RenderCodes.Contains(code),
            $"Precondition: the fixture must make the renderer emit '{code}', but it emitted: {string.Join(", ", prepared.RenderCodes)}.");
    }


    /// <summary>
    ///     The padding that places the body alone just under the cap and body plus overlay just over it. The overlay's
    ///     growth is measured from two real renders, so the fixture follows the checked-in overlay rather than a copied length.
    /// </summary>
    private static async Task<int> OverflowPaddingAsync()
    {
        Harness measure = new();
        int offLength = await measure.ConductorLengthAsync(null);
        int onLength = await measure.ConductorLengthAsync("true");
        int growth = onLength - offLength;
        Assert.True(
            growth > 0,
            $"Precondition: the overlay must add text to the conductor (default {offLength} chars, gated {onLength} chars).");

        // Centred on the cap: the body alone ends half the overlay's growth under it, and the overlay carries it over.
        return SquadOperatorChannelFixture.ConductorBodyLimit - offLength - growth / 2 - PaddingFramingCharacters;
    }

    /// <summary>
    ///     Proves the precondition the overflow facts rest on: with the overlay off the padded conductor renders under
    ///     the cap, so any overflow with the overlay on is the overlay's doing. It is asserted on the conductor's own
    ///     rendered length, because the render also reports any other agent that is over the cap.
    /// </summary>
    private static async Task AssertBodyAloneFitsCapAsync(int padding)
    {
        Harness probe = new();
        InstallResult result = await probe.InstallAsync(
            ReleaseKind.Padded,
            gateValue: null,
            declareServer: false,
            paddingCharacters: padding);

        Assert.True(
            result.ConductorLength <= SquadOperatorChannelFixture.ConductorBodyLimit,
            $"Precondition: with the overlay off the padded conductor must fit the " +
            $"{SquadOperatorChannelFixture.ConductorBodyLimit}-character cap, but it renders to {result.ConductorLength} characters.");
    }

    /// <summary>Exactly <paramref name="paddingCharacters" /> characters, enough to put the body plus overlay over the cap.</summary>
    private static string PaddingOf(int paddingCharacters)
    {
        const string Sentence =
            "The conductor body is padded here so that the operator channel overlay pushes the agent file past the cap. ";
        string repeated = string.Concat(Enumerable.Repeat(Sentence, paddingCharacters / Sentence.Length + 1));
        return repeated[..paddingCharacters];
    }

    /// <summary>Repeated until the conductor body is well past the 30,000 character Copilot agent cap.</summary>
    private static string OversizedPadding()
    {
        const string PaddingSentence =
            "The conductor body is padded here so that the rendered agent file exceeds the Copilot custom agent cap. ";
        return string.Concat(Enumerable.Repeat(PaddingSentence, 600));
    }

    private Task<InstallResult> InstallCaseAsync(
        ReleaseKind kind,
        string? gateValue,
        bool declareServer,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName,
        int paddingCharacters = 0) =>
        new Harness().InstallAsync(kind, gateValue, declareServer, serverName, paddingCharacters);

    /// <summary>The corpus an install uses: the shipped one, oversized, or padded by a measured number of characters.</summary>
    private enum ReleaseKind
    {
        Corpus,
        Oversized,
        Padded,
    }

    /// <summary>
    ///     The renderer warnings one render emitted beside the warnings the lifecycle result carries, and the length of
    ///     the conductor file that render produced.
    /// </summary>
    private sealed record InstallResult(
        IReadOnlyList<string> RenderCodes,
        IReadOnlyList<string> Warnings,
        int ConductorLength);

    /// <summary>
    ///     One isolated in-memory install context: anchor, COPILOT_HOME, user home, state, and the shipped corpus
    ///     the release source extracts from.
    /// </summary>
    private sealed class Harness
    {
        private readonly InMemorySquadState _state = new();
        private readonly string _corpus;
        private readonly string _anchor;
        private readonly string _copilotHome;
        private readonly string _userHome;

        public Harness()
        {
            _corpus = CanonicalSquadTree.CopyInto(_state.Files, Path.Combine(_state.Files.Root, "release", "kyber-squad"));
            _anchor = Path.Combine(_state.Files.Root, "anchor");
            _copilotHome = Path.Combine(_state.Files.Root, "copilot-home");
            _userHome = _state.UserPaths.HomeDirectory;
            _state.Files.CreateDirectory(_anchor);
            _state.Files.CreateDirectory(_copilotHome);
        }

        /// <summary>Installs globally into this context, writing an <c>mcp-config.json</c> first when the case declares the server.</summary>
        public async Task<InstallResult> InstallAsync(
            ReleaseKind kind,
            string? gateValue,
            bool declareServer,
            string? serverName = SquadOperatorChannelFixture.DefaultServerName,
            int paddingCharacters = 0)
        {
            if (declareServer)
            {
                _state.Files.WriteAllText(
                    Path.Combine(_copilotHome, "mcp-config.json"),
                    SquadOperatorChannelFixture.ServerPresentConfig);
            }

            FakeSquadRenderer renderer = NewRenderer();
            using FakeSquadReleaseSource releaseSource = ReleaseSource(kind, paddingCharacters);
            SquadLifecycleService service = Service(releaseSource, renderer, gateValue, serverName);

            SquadLifecycleResult result = await service.InstallAsync(new SquadInstallRequest(
                _anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Copilot],
                Version: InstallVersion));
            Assert.True(result.Success, $"The install failed: {string.Join("; ", result.Errors ?? [])}.");
            return new InstallResult(renderer.WarningCodes, WarningsOf(result), ConductorLengthOf(renderer));
        }

        /// <summary>Updates the install already in this context to <paramref name="kind"/>'s corpus.</summary>
        public async Task<InstallResult> UpdateAsync(ReleaseKind kind, string? gateValue, int paddingCharacters = 0)
        {
            FakeSquadRenderer renderer = NewRenderer();
            using FakeSquadReleaseSource releaseSource = ReleaseSource(kind, paddingCharacters);
            SquadLifecycleService service = Service(
                releaseSource,
                renderer,
                gateValue,
                SquadOperatorChannelFixture.DefaultServerName);

            SquadLifecycleResult result = await service.UpdateAsync(new SquadUpdateRequest(
                _anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Copilot],
                Version: UpdateVersion));
            return new InstallResult(renderer.WarningCodes, WarningsOf(result), ConductorLengthOf(renderer));
        }

        /// <summary>The rendered conductor's length with the overlay gate at <paramref name="gateValue"/>, declaring the server first.</summary>
        public async Task<int> ConductorLengthAsync(string? gateValue)
        {
            _state.Files.WriteAllText(
                Path.Combine(_copilotHome, "mcp-config.json"),
                SquadOperatorChannelFixture.ServerPresentConfig);
            SquadRenderResult result = await NewRegistry().RenderAsync(new SquadRenderRequest(
                _corpus,
                [SquadTarget.Copilot],
                SquadDeploymentScope.Global,
                GlobalRoots: SquadOperatorChannelFixture.GlobalRoots(_copilotHome, _userHome),
                EnvironmentReader: SquadOperatorChannelFixture.CreateEnvironmentReader(gateValue, _copilotHome)));
            return SquadOperatorChannelFixture.ConductorText(result).Length;
        }

        /// <summary>The conductor file length in the first render the fake answered, which is the one the result reports on.</summary>
        private static int ConductorLengthOf(FakeSquadRenderer renderer) =>
            renderer.Results.Count == 0
                ? 0
                : SquadOperatorChannelFixture.ConductorText(renderer.Results[0]).Length;

        private static IReadOnlyList<string> WarningsOf(SquadLifecycleResult result) =>
            result.Warnings ?? throw new InvalidOperationException("The lifecycle result carries no warnings list.");

        private SquadRendererRegistry NewRegistry() =>
            new([new CopilotRenderer()], new InMemorySquadServices(_state.Files).RenderInputReader());

        private FakeSquadRenderer NewRenderer() => FakeSquadRenderer.Wrapping(NewRegistry());

        private SquadLifecycleService Service(
            ISquadReleaseSource releaseSource,
            ISquadRenderer renderer,
            string? gateValue,
            string? serverName) =>
            SquadTestCommands.InMemoryLifecycle(
                _state,
                releaseSource,
                renderer,
                SquadOperatorChannelFixture.GlobalRoots(_copilotHome, _userHome),
                SquadOperatorChannelFixture.CreateEnvironmentReader(gateValue, _copilotHome, serverName));

        private FakeSquadReleaseSource ReleaseSource(ReleaseKind kind, int paddingCharacters) => kind switch
        {
            ReleaseKind.Corpus => FakeSquadReleaseSource.FromDirectory(_corpus, _state.Files),
            ReleaseKind.Oversized => PaddedCorpus(OversizedPadding()),
            ReleaseKind.Padded => PaddedCorpus(PaddingOf(paddingCharacters)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown release kind."),
        };

        /// <summary>The shipped corpus with <paramref name="padding"/> characters of prose appended to the conductor.</summary>
        private FakeSquadReleaseSource PaddedCorpus(string padding) => new()
        {
            SourceDirectory = _corpus,
            Files = _state.Files,
            AfterExtract = (root, _) =>
            {
                _state.Files.AppendAllText(Path.Combine(root, "agents", "conductor.md"), "\n" + padding + "\n");
                return Task.CompletedTask;
            },
        };
    }
}
