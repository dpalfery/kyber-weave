using KyberWeave.Testing.Squad;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;

namespace KyberWeave.Integration.Tests;

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
public sealed class SquadLifecycleRenderWarningFilterTests : IDisposable
{
    private const string AgentBodyTooLongCode = "agent-body-too-long";
    private const string ServerAbsentCode = SquadOperatorChannelFixture.ServerAbsentWarning;
    private const string InstallVersion = "1.2.3";
    private const string UpdateVersion = "1.2.4";

    /// <summary>The newline before and after the padding text the padded release appends.</summary>
    private const int PaddingFramingCharacters = 2;

    /// <summary>
    ///     The padding that places the body alone just under the cap and body plus overlay just over it, measured once
    ///     for the whole run. The overlay's growth comes from two real renders at a fixed home, so the fixture follows
    ///     the checked-in overlay rather than a copied length and both overflow facts install the same conductor.
    /// </summary>
    private static readonly Lazy<int> OverflowPadding =
        new(() => OverflowPaddingAsync().GetAwaiter().GetResult());

    /// <summary>The fixed home the padding is measured at. Its absolute links make the conductor's length depend on the path.</summary>
    private static readonly Lazy<(string CopilotHome, string UserHome)> ConductorLengthFixture =
        new(CreateConductorLengthFixture);

    private readonly List<Harness> _harnesses = [];

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        foreach (Harness harness in _harnesses) harness.Dispose();

        _temp.Dispose();
    }

    /// <summary>
    ///     An install whose conductor breaches the agent body cap must not put the
    ///     <c>agent-body-too-long</c> warning on the lifecycle result. Without the filter a default install
    ///     prints a warning it never printed before.
    /// </summary>
    [Fact]
    public void Install_AgentBodyOverCopilotCap_IsNotCarriedOntoTheLifecycleResult()
    {
        PreparedInstall prepared = InstallCase(ReleaseKind.Oversized, gateValue: null, declareServer: false);

        AssertRendererEmitted(prepared, AgentBodyTooLongCode);
        IReadOnlyList<string> warnings = prepared.Record.Warnings;
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
        Harness h = NewHarness("update-body");
        InstallCase(ReleaseKind.Corpus, gateValue: null, declareServer: false).CopyInto(h);
        Func<string, string?> env = h.CreateEnvironmentReader(null);

        SquadLifecycleResult result = await NewService(h, h.Oversized, env).UpdateAsync(
            new SquadUpdateRequest(
                h.Anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Copilot],
                Version: UpdateVersion));

        Assert.True(
            h.Renderer.WarningCodes.Contains(AgentBodyTooLongCode),
            $"Precondition: the fixture must make the renderer emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", h.Renderer.WarningCodes)}.");
        IReadOnlyList<string> warnings = WarningsOf(result);
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
    public void Install_GateOnServerAbsent_ShowsTheOperatorChannelWarningOnTheLifecycleResult()
    {
        PreparedInstall prepared = InstallCase(ReleaseKind.Corpus, gateValue: "true", declareServer: false);

        AssertRendererEmitted(prepared, ServerAbsentCode);
        string warning = Assert.Single(prepared.Record.Warnings);
        Assert.StartsWith(ServerAbsentCode + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One render that produces both warnings must surface only the operator channel's. The body length
    ///     warning is filtered out and the operator channel one is kept, so the filter cannot be satisfied by
    ///     either dropping everything or passing everything.
    /// </summary>
    [Fact]
    public void Install_OversizedConductorAndServerAbsentInOneRender_ShowsOnlyTheOperatorChannelWarning()
    {
        PreparedInstall prepared = InstallCase(ReleaseKind.Oversized, gateValue: "true", declareServer: false);

        Assert.True(
            prepared.RenderCodes.Contains(AgentBodyTooLongCode) && prepared.RenderCodes.Contains(ServerAbsentCode),
            $"Precondition: one render must emit both codes, but it emitted: {string.Join(", ", prepared.RenderCodes)}.");
        IReadOnlyList<string> warnings = prepared.Record.Warnings;
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
    public void Install_GateOnServerNameMissing_ShowsTheNameMissingWarningOnTheLifecycleResult()
    {
        PreparedInstall prepared = InstallCase(
            ReleaseKind.Corpus,
            gateValue: "true",
            declareServer: false,
            serverName: null);

        AssertRendererEmitted(prepared, SquadOperatorChannelFixture.NameMissingWarning);
        string warning = Assert.Single(prepared.Record.Warnings);
        Assert.StartsWith(SquadOperatorChannelFixture.NameMissingWarning + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     With the gate on and an invalid server name, the name-invalid warning is an operator channel warning
    ///     and must reach the install result. The raw value stays out of the warning text.
    /// </summary>
    [Fact]
    public void Install_GateOnServerNameInvalid_ShowsTheNameInvalidWarningOnTheLifecycleResult()
    {
        PreparedInstall prepared = InstallCase(
            ReleaseKind.Corpus,
            gateValue: "true",
            declareServer: false,
            serverName: "bad name zz1");

        AssertRendererEmitted(prepared, SquadOperatorChannelFixture.NameInvalidWarning);
        string warning = Assert.Single(prepared.Record.Warnings);
        Assert.StartsWith(SquadOperatorChannelFixture.NameInvalidWarning + ": ", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The overlay is what pushes this conductor over the cap: the body alone fits, and body plus overlay does not.
    ///     The operator channel caused the overflow, so <c>agent-body-too-long</c> must reach the install result and the
    ///     operator sees a hint. Without the overlay the same body stays silent, which the body-alone precondition proves.
    /// </summary>
    [Fact]
    public void Install_OverlayPushesConductorOverCap_ShowsTheAgentBodyTooLongWarningOnTheLifecycleResult()
    {
        int padding = OverflowPadding.Value;
        AssertBodyAloneFitsCap(padding);

        PreparedInstall prepared = InstallCase(
            ReleaseKind.Padded,
            gateValue: "true",
            declareServer: true,
            paddingCharacters: padding);

        AssertOverflowWarningShown(prepared.RenderCodes, prepared.Record.Warnings);
    }

    /// <summary>
    ///     The same guarantee on the update path. The install runs on the checked-in corpus, which fits the cap, and the
    ///     update lands the padded conductor, so the overflow only the overlay causes is the one the update reports.
    /// </summary>
    [Fact]
    public async Task Update_OverlayPushesConductorOverCap_ShowsTheAgentBodyTooLongWarningOnTheLifecycleResult()
    {
        Harness h = NewHarness("update-overlay-overflow");
        WriteServerConfig(h);
        int padding = OverflowPadding.Value;
        AssertBodyAloneFitsCap(padding);

        InstallCase(ReleaseKind.Corpus, gateValue: "true", declareServer: true).CopyInto(h);
        using FakeSquadReleaseSource padded = PaddedCorpus(PaddingOf(padding));
        Func<string, string?> env = h.CreateEnvironmentReader("true");
        SquadLifecycleResult result = await NewService(h, padded, env).UpdateAsync(
            new SquadUpdateRequest(
                h.Anchor,
                SquadDeploymentScope.Global,
                [SquadTarget.Copilot],
                Version: UpdateVersion));

        AssertOverflowWarningShown(h.Renderer.WarningCodes, WarningsOf(result));
    }

    /// <summary>
    ///     When the overlay is not applied, the conductor is the default one, so the oversized body must not put
    ///     <c>agent-body-too-long</c> on the install result. Each case keeps the overlay off through a different door: the
    ///     gate, an absent server, an unset name, or an invalid name. The renderer still emits the warning in each case,
    ///     which the precondition proves, so a pass cannot be vacuous.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotAppliedCases))]
    public void Install_OverlayNotApplied_KeepsAgentBodyTooLongOffTheLifecycleResult(
        string variant,
        string? gateValue,
        string? serverName,
        bool declareServer)
    {
        PreparedInstall prepared = InstallCase(
            ReleaseKind.Oversized,
            gateValue: gateValue,
            declareServer: declareServer,
            serverName: serverName);

        Assert.True(
            prepared.RenderCodes.Contains(AgentBodyTooLongCode),
            $"Precondition ({variant}): the renderer must emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", prepared.RenderCodes)}.");
        Assert.DoesNotContain(
            prepared.Record.Warnings,
            line => line.StartsWith(AgentBodyTooLongCode, StringComparison.Ordinal));
    }

    public static IEnumerable<object?[]> NotAppliedCases()
    {
        yield return ["gate-off", null, SquadOperatorChannelFixture.DefaultServerName, true];
        yield return ["server-absent", "true", SquadOperatorChannelFixture.DefaultServerName, false];
        yield return ["name-missing", "true", null, true];
        yield return ["name-invalid", "true", "bad name zz1", true];
    }

    private static void AssertOverflowWarningShown(
        IReadOnlyList<string> renderCodes,
        IReadOnlyList<string> warnings)
    {
        Assert.True(
            renderCodes.Contains(AgentBodyTooLongCode),
            $"Precondition: the overlay-applied render must emit '{AgentBodyTooLongCode}', but it emitted: {string.Join(", ", renderCodes)}.");
        string warning = Assert.Single(warnings);
        Assert.StartsWith(AgentBodyTooLongCode + ": ", warning, StringComparison.Ordinal);
        Assert.Contains("'conductor'", warning, StringComparison.Ordinal);
    }

    private static void AssertRendererEmitted(PreparedInstall prepared, string code)
    {
        Assert.True(
            prepared.RenderCodes.Contains(code),
            $"Precondition: the fixture must make the renderer emit '{code}', but it emitted: {string.Join(", ", prepared.RenderCodes)}.");
    }

    /// <summary>
    ///     The padding that places the body alone just under the cap and body plus overlay just over it. The overlay's growth
    ///     is measured from two real renders, so the fixture follows the checked-in overlay rather than a copied length.
    /// </summary>
    private static async Task<int> OverflowPaddingAsync()
    {
        (string copilotHome, string userHome) = ConductorLengthFixture.Value;
        int offLength = await ConductorLengthAsync(copilotHome, userHome, null);
        int onLength = await ConductorLengthAsync(copilotHome, userHome, "true");
        int growth = onLength - offLength;
        Assert.True(
            growth > 0,
            $"Precondition: the overlay must add text to the conductor (default {offLength} chars, gated {onLength} chars).");

        // Centred on the cap: the body alone ends half the overlay's growth under it, and the overlay carries it over.
        return SquadOperatorChannelFixture.ConductorBodyLimit - offLength - growth / 2 - PaddingFramingCharacters;
    }

    /// <summary>
    ///     An empty Copilot home declaring the operator channel's server, so the gated render applies the overlay, and a
    ///     user home beside it. Created once for the run, because the conductor's absolute links make its length depend on
    ///     the path.
    /// </summary>
    private static (string CopilotHome, string UserHome) CreateConductorLengthFixture()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "kw-render-warning-length-" + Guid.NewGuid().ToString("N"));
        string copilotHome = Path.Combine(root, "copilot-home");
        string userHome = Path.Combine(root, "user-home");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(userHome);
        File.WriteAllText(
            Path.Combine(copilotHome, "mcp-config.json"),
            SquadOperatorChannelFixture.ServerPresentConfig,
            Encoding.UTF8);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup on process termination.
            }
        };

        return (copilotHome, userHome);
    }

    /// <summary>
    ///     Proves the precondition the overflow facts rest on: with the overlay off, the padded conductor does not exceed
    ///     the cap, so any overflow with the overlay on is the overlay's doing.
    /// </summary>
    private static void AssertBodyAloneFitsCap(int padding)
    {
        PreparedInstall probe = InstallCase(
            ReleaseKind.Padded,
            gateValue: null,
            declareServer: false,
            paddingCharacters: padding);

        Assert.DoesNotContain(AgentBodyTooLongCode, probe.RenderCodes);
    }

    private static async Task<int> ConductorLengthAsync(string copilotHome, string userHome, string? gateValue)
    {
        ISquadRenderer renderer = SquadHostDefaults.ResolveRenderer();
        SquadRenderResult result = await renderer.RenderAsync(new SquadRenderRequest(
            SyntheticValidSquadReleaseSource.SyntheticCorpusRoot,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Global,
            GlobalRoots: SquadOperatorChannelFixture.GlobalRoots(copilotHome, userHome),
            EnvironmentReader: SquadOperatorChannelFixture.CreateEnvironmentReader(gateValue, copilotHome)).Prepared());
        return SquadOperatorChannelFixture.ConductorText(result).Length;
    }

    private static void WriteServerConfig(Harness h)
    {
        File.WriteAllText(
            Path.Combine(h.CopilotHome, "mcp-config.json"),
            SquadOperatorChannelFixture.ServerPresentConfig,
            Encoding.UTF8);
    }

    /// <summary>
    ///     The one real install for <paramref name="kind" />'s input: built on the first request, then handed to
    ///     every fact that asks for the same input.
    /// </summary>
    private static PreparedInstall InstallCase(
        ReleaseKind kind,
        string? gateValue,
        bool declareServer,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName,
        int paddingCharacters = 0)
    {
        string key = SquadInstallRecipe.Key(
            "install",
            SquadDeploymentScope.Global,
            [SquadTarget.Copilot],
            ("case", "render-warning-filter"),
            ("release", Recipe(kind, paddingCharacters)),
            ("version", InstallVersion),
            ("gate", gateValue),
            ("server", serverName),
            ("server-declared", declareServer ? "yes" : "no"));
        return PreparedSquadInstalls.Get(key, () => Build(
            key,
            kind,
            gateValue,
            declareServer,
            serverName,
            paddingCharacters));
    }

    /// <summary>The recipe token for a release input: its corpus and, for the padded one, its measured padding.</summary>
    private static string Recipe(ReleaseKind kind, int paddingCharacters) => kind switch
    {
        ReleaseKind.Corpus => "corpus",
        ReleaseKind.Oversized => "oversized",
        ReleaseKind.Padded => $"padded-{paddingCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown release kind."),
    };

    /// <summary>
    ///     The real install behind a prepared case, into its own home, with an <c>mcp-config.json</c> written first
    ///     when the case needs one. The renderer's warning codes are recorded beside the lifecycle result's warnings.
    /// </summary>
    private static PreparedInstall Build(
        string key,
        ReleaseKind kind,
        string? gateValue,
        bool declareServer,
        string? serverName,
        int paddingCharacters)
    {
        TempDirectory? directory = new();
        try
        {
            string anchor = Path.Combine(directory.Path, "anchor");
            string copilotHome = Path.Combine(directory.Path, "copilot-home");
            string userHome = Path.Combine(directory.Path, "user-home");
            Directory.CreateDirectory(anchor);
            Directory.CreateDirectory(copilotHome);
            Directory.CreateDirectory(userHome);
            if (declareServer)
            {
                File.WriteAllText(
                    Path.Combine(copilotHome, "mcp-config.json"),
                    SquadOperatorChannelFixture.ServerPresentConfig,
                    Encoding.UTF8);
            }

            FakeSquadRenderer renderer = FakeSquadRenderer.Wrapping(SquadTestRegistry.Create([new CopilotRenderer()]));
            FakeSquadUserPaths userPaths = new(userHome);
            SquadStateStore store = RealSquadState.Store(userPaths);
            using ISquadReleaseSource releaseSource = ReleaseSource(kind, paddingCharacters);
            SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
                releaseSource,
                renderer,
                store,
                globalRoots: SquadOperatorChannelFixture.GlobalRoots(copilotHome, userHome),
                environmentReader: SquadOperatorChannelFixture.CreateEnvironmentReader(gateValue, copilotHome, serverName));

            SquadLifecycleResult result = service.InstallAsync(new SquadInstallRequest(
                    anchor,
                    SquadDeploymentScope.Global,
                    [SquadTarget.Copilot],
                    Version: InstallVersion))
                .GetAwaiter().GetResult();
            if (!result.Success)
                throw new InvalidOperationException(
                    $"The prepared install for '{key}' failed: {string.Join("; ", result.Errors ?? [])}.");

            _ = result.Warnings ?? throw new InvalidOperationException("The lifecycle result carries no warnings list.");

            PreparedInstall prepared = new(
                key,
                directory,
                anchor,
                copilotHome,
                userHome,
                renderer.WarningCodes,
                SquadInstallRecord.FromLifecycle(result));
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    private static ISquadReleaseSource ReleaseSource(ReleaseKind kind, int paddingCharacters) => kind switch
    {
        ReleaseKind.Corpus => new SyntheticValidSquadReleaseSource(),
        ReleaseKind.Oversized => PaddedCorpus(OversizedPadding()),
        ReleaseKind.Padded => PaddedCorpus(PaddingOf(paddingCharacters)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown release kind."),
    };

    /// <summary>
    ///     The synthetic corpus with <paramref name="padding" /> characters of prose appended to the conductor, so the
    ///     renderer measures a longer body. The corpus is copied into the extraction directory, so the repository tree is
    ///     never written.
    /// </summary>
    private static FakeSquadReleaseSource PaddedCorpus(string padding) => new()
    {
        SourceDirectory = SyntheticValidSquadReleaseSource.SyntheticCorpusRoot,
        AfterExtract = (root, token) => File.AppendAllTextAsync(
            Path.Combine(root, "agents", "conductor.md"),
            "\n" + padding + "\n",
            token),
    };

    /// <summary>Repeated until the conductor body is well past the 30,000 character Copilot agent cap.</summary>
    private static string OversizedPadding()
    {
        const string PaddingSentence =
            "The conductor body is padded here so that the rendered agent file exceeds the Copilot custom agent cap. ";
        return string.Concat(Enumerable.Repeat(PaddingSentence, 600));
    }

    /// <summary>Exactly <paramref name="paddingCharacters" /> characters, enough to put the body plus overlay over the cap.</summary>
    private static string PaddingOf(int paddingCharacters)
    {
        const string Sentence =
            "The conductor body is padded here so that the operator channel overlay pushes the agent file past the cap. ";
        string repeated = string.Concat(Enumerable.Repeat(Sentence, paddingCharacters / Sentence.Length + 1));
        return repeated[..paddingCharacters];
    }

    private static IReadOnlyList<string> WarningsOf(SquadLifecycleResult result)
    {
        return result.Warnings ?? throw new InvalidOperationException("The lifecycle result carries no warnings list.");
    }

    private static SquadLifecycleService NewService(
        Harness h,
        ISquadReleaseSource releaseSource,
        Func<string, string?>? environmentReader = null)
    {
        return SquadHostDefaults.CreateLifecycleService(
            releaseSource,
            h.Renderer,
            h.Store,
            globalRoots: h.GlobalRoots,
            environmentReader: environmentReader);
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

    /// <summary>The corpus a prepared case installs: the synthetic one, oversized, or padded by a measured number of characters.</summary>
    private enum ReleaseKind
    {
        Corpus,
        Oversized,
        Padded,
    }

    /// <summary>
    ///     One prepared install, with the renderer warnings its own render emitted and a copy of the installed
    ///     home and user data for the facts that must update over it.
    /// </summary>
    private sealed class PreparedInstall(
        string key,
        TempDirectory directory,
        string anchor,
        string copilotHome,
        string userHome,
        IReadOnlyList<string> renderCodes,
        SquadInstallRecord record) : PreparedSquadInstall(key, directory, record)
    {
        public string Anchor { get; } = anchor;

        public string CopilotHome { get; } = copilotHome;

        public string UserHome { get; } = userHome;

        public IReadOnlyList<string> RenderCodes { get; } = renderCodes;

        /// <summary>
        ///     Copies the prepared home and user data into <paramref name="harness" />'s own directories and re-roots
        ///     the recorded root in state and in the deployed files the Copilot harness links to, so the copy is a
        ///     faithful install at its new root for the update that follows.
        /// </summary>
        public void CopyInto(Harness harness)
        {
            SquadInstallCopy.Tree(CopilotHome, harness.CopilotHome);
            SquadInstallCopy.Tree(UserHome, harness.UserHome);

            string templateCopilotHome = SquadInstallCopy.Canonical(CopilotHome);
            string copyCopilotHome = SquadInstallCopy.Canonical(harness.CopilotHome);
            string stateDirectory = Path.Combine(harness.UserHome, ".kyber-weave", "squad");
            SquadInstallCopy.ReRoot(
                stateDirectory,
                SquadInstallCopy.Canonical(UserHome),
                SquadInstallCopy.Canonical(harness.UserHome));
            SquadInstallCopy.ReRoot(stateDirectory, templateCopilotHome, copyCopilotHome);

            SquadStateStore store = RealSquadState.Store(new FakeSquadUserPaths(harness.UserHome));
            _ = SquadInstallCopy.ReRootDeployed(
                harness.CopilotHome,
                store.ResolveReceiptPath(harness.Anchor, SquadDeploymentScope.Global),
                store,
                templateCopilotHome,
                copyCopilotHome);
        }
    }

    /// <summary>One isolated install context: anchor, COPILOT_HOME, user paths, state store, renderer, and release source.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(string anchor, string copilotHome, string userHome)
        {
            Anchor = anchor;
            CopilotHome = copilotHome;
            UserHome = userHome;
            Store = RealSquadState.Store(new FakeSquadUserPaths(userHome));
            GlobalRoots = SquadOperatorChannelFixture.GlobalRoots(copilotHome, userHome);
            Renderer = FakeSquadRenderer.Wrapping(SquadTestRegistry.Create([new CopilotRenderer()]));
            Oversized = PaddedCorpus(OversizedPadding());
        }

        public string Anchor { get; }

        public string CopilotHome { get; }

        public string UserHome { get; }

        public SquadStateStore Store { get; }

        public ISquadGlobalRootResolver GlobalRoots { get; }

        public FakeSquadRenderer Renderer { get; }

        public FakeSquadReleaseSource Oversized { get; }

        public void Dispose()
        {
            Oversized.Dispose();
        }

        public Func<string, string?> CreateEnvironmentReader(
            string? gateValue,
            string? serverName = SquadOperatorChannelFixture.DefaultServerName)
        {
            return SquadOperatorChannelFixture.CreateEnvironmentReader(gateValue, CopilotHome, serverName);
        }
    }
}
