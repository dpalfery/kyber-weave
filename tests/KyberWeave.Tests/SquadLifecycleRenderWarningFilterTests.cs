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
            Store = new SquadStateStore(new FakeSquadUserPaths(userHome));
            GlobalRoots = SquadOperatorChannelFixture.GlobalRoots(copilotHome, userHome);
            Renderer = new RecordingRenderer(SquadCommandComposition.ResolveRenderer());
            Corpus = new CorpusSquadReleaseSource();
            Oversized = new OversizedConductorReleaseSource();
        }

        public string Anchor { get; }

        public string CopilotHome { get; }

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
