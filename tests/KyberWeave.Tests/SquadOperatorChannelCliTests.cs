using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins how <c>squad install</c>, <c>squad update</c>, and <c>squad doctor</c> resolve the operator
/// channel gate and its server name. All three read the same process environment, so the same environment
/// must yield the same conductor bytes and the same drift verdict. The commands run through their real
/// composition with the checked-in corpus and a temporary COPILOT_HOME.
/// </summary>
/// <remarks>
/// Gate facts fail on behaviour until the overlay, the server-name variable, and the warning filter
/// exist. The default-off facts guard the unchanged install, doctor, and status output. The gate, the
/// server variable, and COPILOT_HOME are process-wide, so this class shares the non-parallel collection
/// that <see cref="SquadOperatorChannelCollection"/> defines.
/// </remarks>
[Collection(SquadOperatorChannelCollection.Name)]
public sealed class SquadOperatorChannelCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<CliHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (CliHarness harness in _harnesses)
        {
            harness.Dispose();
        }

        _temp.Dispose();
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithServerFound_WritesTheServerToolEntryAndOverlay()
    {
        CliHarness h = NewHarness("install-on", SquadOperatorChannelFixture.ServerPresentConfig);

        CommandExecution execution = Run("true", h, () => Install(h).Execute(null!, InstallSettings(h)));

        AssertExit(execution, 0);
        string conductor = ReadConductor(h);
        Assert.Single(
            SquadOperatorChannelFixture.ToolEntries(conductor),
            entry => entry == SquadOperatorChannelFixture.ToolEntry);
        Assert.Contains("Hard-block question.", conductor, StringComparison.Ordinal);
        Assert.DoesNotContain(SquadOperatorChannelFixture.Placeholder, conductor, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithServerAbsent_WarnsOnTheCliAndWritesTheDefaultConductor()
    {
        CliHarness h = NewHarness("install-absent", mcpConfigJson: null);

        CommandExecution execution = Run("true", h, () => Install(h).Execute(null!, InstallSettings(h)));

        AssertExit(execution, 0);
        Assert.Contains(SquadOperatorChannelFixture.ServerAbsentWarning, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithServerNameUnset_WarnsNameMissingAndWritesTheDefaultConductor()
    {
        CliHarness h = NewHarness("install-name-missing", SquadOperatorChannelFixture.ServerPresentConfig);

        CommandExecution execution = Run("true", h, () => Install(h).Execute(null!, InstallSettings(h)), serverName: null);

        AssertExit(execution, 0);
        Assert.Contains(SquadOperatorChannelFixture.NameMissingWarning, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithInvalidServerName_WarnsNameInvalidAndNeverEchoesTheValue()
    {
        CliHarness h = NewHarness("install-name-invalid", SquadOperatorChannelFixture.ServerPresentConfig);
        const string invalid = "bad name zz1";

        CommandExecution execution = Run("true", h, () => Install(h).Execute(null!, InstallSettings(h)), serverName: invalid);

        AssertExit(execution, 0);
        Assert.Contains(SquadOperatorChannelFixture.NameInvalidWarning, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(invalid, ReadConductor(h), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonTrueGateValues))]
    public void Install_CopilotGlobal_GateNotExactlyTrue_WritesNoOperatorChannelOutput(string gateValue)
    {
        CliHarness h = NewHarness("install-off", SquadOperatorChannelFixture.ServerPresentConfig);

        CommandExecution execution = Run(gateValue, h, () => Install(h).Execute(null!, InstallSettings(h)));

        AssertExit(execution, 0);
        Assert.DoesNotContain("operator-channel", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-channel", ReadConductor(h), StringComparison.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(h.CopilotHome, "agents"), "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("operator-channel", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Install_GateTrue_ThenDoctorAndStatusReportNoDriftForTheSameEnvironment()
    {
        CliHarness h = NewHarness("install-then-doctor", SquadOperatorChannelFixture.ServerPresentConfig);

        CommandExecution install = Run("true", h, () => Install(h).Execute(null!, InstallSettings(h)));
        AssertExit(install, 0);
        Assert.Single(
            SquadOperatorChannelFixture.ToolEntries(ReadConductor(h)),
            entry => entry == SquadOperatorChannelFixture.ToolEntry);

        CommandExecution doctor = Run("true", h, () => Doctor(h).Execute(null!, new SquadDoctorSettings
        {
            Path = h.Anchor,
            Global = true
        }));
        AssertExit(doctor, 0);
        Assert.DoesNotContain("Unmanaged global file", doctor.Output, StringComparison.Ordinal);

        CommandExecution status = Run("true", h, () => Status(h).Execute(null!, new SquadStatusSettings
        {
            Path = h.Anchor,
            Global = true
        }));
        AssertExit(status, 0);
        Assert.Contains("All deployed files match the recorded receipt.", status.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_GateUnset_DoctorAndStatusReportNoDrift()
    {
        CliHarness h = NewHarness("default-off-doctor", SquadOperatorChannelFixture.ServerPresentConfig);

        CommandExecution install = Run(null, h, () => Install(h).Execute(null!, InstallSettings(h)));
        AssertExit(install, 0);

        CommandExecution doctor = Run(null, h, () => Doctor(h).Execute(null!, new SquadDoctorSettings
        {
            Path = h.Anchor,
            Global = true
        }));
        AssertExit(doctor, 0);
        Assert.DoesNotContain("Unmanaged global file", doctor.Output, StringComparison.Ordinal);

        CommandExecution status = Run(null, h, () => Status(h).Execute(null!, new SquadStatusSettings
        {
            Path = h.Anchor,
            Global = true
        }));
        AssertExit(status, 0);
        Assert.Contains("All deployed files match the recorded receipt.", status.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_CopilotGlobal_GateTrueWithServerFound_AddsTheServerToolEntryToTheInstalledConductor()
    {
        CliHarness h = NewHarness("update-on", SquadOperatorChannelFixture.ServerPresentConfig);
        CommandExecution install = Run(null, h, () => Install(h).Execute(null!, InstallSettings(h)));
        AssertExit(install, 0);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);

        CommandExecution update = Run("true", h, () => Update(h).Execute(null!, new SquadUpdateSettings
        {
            Path = h.Anchor,
            Global = true,
            Targets = ["copilot"],
            Version = "1.2.4",
            Yes = true
        }));

        AssertExit(update, 0);
        Assert.Single(
            SquadOperatorChannelFixture.ToolEntries(ReadConductor(h)),
            entry => entry == SquadOperatorChannelFixture.ToolEntry);
    }

    public static IEnumerable<object[]> NonTrueGateValues() =>
        SquadOperatorChannelFixture.NonTrueGateValues.Select(static value => new object[] { value });

    private CliHarness NewHarness(string name, string? mcpConfigJson)
    {
        string root = Path.Combine(_temp.Path, name);
        string copilotHome = Path.Combine(root, "copilot-home");
        string anchor = Path.Combine(root, "anchor");
        string userHome = Path.Combine(root, "user-home");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(userHome);
        if (mcpConfigJson is not null)
        {
            File.WriteAllText(Path.Combine(copilotHome, "mcp-config.json"), mcpConfigJson);
        }

        CliHarness harness = new(anchor, copilotHome, userHome);
        _harnesses.Add(harness);
        return harness;
    }

    private static CommandExecution Run(
        string? gateValue,
        CliHarness h,
        Func<int> execute,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName) =>
        Capture(() => SquadOperatorChannelFixture.RunWithGate(gateValue, h.CopilotHome, execute, serverName));

    private static SquadInstallCommand Install(CliHarness h) =>
        new(
            userPaths: h.UserPaths,
            stateStore: h.Store,
            releaseSource: h.ReleaseSource,
            renderer: SquadCommandComposition.ResolveRenderer(),
            isInteractive: false,
            readAnswer: _ => true,
            globalRoots: h.GlobalRoots);

    private static SquadUpdateCommand Update(CliHarness h) =>
        new(
            userPaths: h.UserPaths,
            stateStore: h.Store,
            releaseSource: h.ReleaseSource,
            renderer: SquadCommandComposition.ResolveRenderer(),
            isInteractive: false,
            readAnswer: _ => true,
            globalRoots: h.GlobalRoots);

    /// <remarks>
    /// The working directory is the checked-in corpus so the global collision scan can locate the
    /// canonical source; without it doctor skips the scan and the drift check proves nothing.
    /// </remarks>
    private static SquadDoctorCommand Doctor(CliHarness h) =>
        new(
            executor: new FakeProcessExecutor().WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n"),
            userPaths: h.UserPaths,
            workingDirectory: KyberWeaveTestPaths.ToolRoot,
            globalRoots: h.GlobalRoots,
            renderer: SquadCommandComposition.ResolveRenderer(),
            stateStore: h.Store);

    private static SquadStatusCommand Status(CliHarness h) =>
        new(
            userPaths: h.UserPaths,
            stateStore: h.Store,
            globalRoots: h.GlobalRoots);

    private static SquadInstallSettings InstallSettings(CliHarness h) =>
        new()
        {
            Path = h.Anchor,
            Global = true,
            Targets = ["copilot"],
            Version = "1.2.3",
            Yes = true
        };

    private static string ReadConductor(CliHarness h) =>
        File.ReadAllText(Path.Combine(h.CopilotHome, "agents", "conductor.agent.md"));

    private static void AssertExit(CommandExecution execution, int expected) =>
        Assert.True(
            execution.ExitCode == expected,
            $"Expected exit {expected} but got {execution.ExitCode}: {execution.Output}");

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    /// <summary>One isolated install context: anchor, COPILOT_HOME, user paths, state store, and a corpus source.</summary>
    private sealed class CliHarness : IDisposable
    {
        private readonly CorpusSquadReleaseSource _releaseSource = new();

        public CliHarness(string anchor, string copilotHome, string userHome)
        {
            Anchor = anchor;
            CopilotHome = copilotHome;
            UserPaths = new FakeSquadUserPaths(userHome);
            Store = new SquadStateStore(UserPaths);
            GlobalRoots = SquadOperatorChannelFixture.GlobalRoots(copilotHome, userHome);
        }

        public string Anchor { get; }

        public string CopilotHome { get; }

        public FakeSquadUserPaths UserPaths { get; }

        public SquadStateStore Store { get; }

        public ISquadGlobalRootResolver GlobalRoots { get; }

        public ISquadReleaseSource ReleaseSource => _releaseSource;

        public void Dispose() => _releaseSource.Dispose();
    }
}
