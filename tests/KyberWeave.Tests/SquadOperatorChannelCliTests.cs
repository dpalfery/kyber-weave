using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins how <c>squad install</c>, <c>squad update</c>, and <c>squad doctor</c> resolve the operator
/// channel gate and its server name. All three read the same environment, so the same environment
/// must yield the same conductor bytes and the same drift verdict. The commands run through their real
/// composition with the checked-in corpus and a temporary COPILOT_HOME.
/// </summary>
/// <remarks>
/// Gate facts fail on behaviour until the overlay, the server-name variable, and the warning filter
/// exist. The default-off facts guard the unchanged install, doctor, and status output. Environment
/// variables are read through injectable delegates rather than process mutation, allowing safe
/// parallel test execution. The install for each gate and server combination runs once for the whole
/// run through <see cref="PreparedSquadInstalls"/> and is captured here, so a test asserts the recorded
/// command output and works on its own copy of the deployed home.
/// </remarks>
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
        CliHarness h = NewHarness("install-on");
        InstalledChannel channel = Channel("gate-on-server-present", "true", SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertExit(channel.Execution, 0);
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
        CliHarness h = NewHarness("install-absent");
        InstalledChannel channel = Channel("gate-on-server-absent", "true", null);
        channel.CopyInto(h);

        AssertExit(channel.Execution, 0);
        Assert.Contains(
            SquadOperatorChannelFixture.ServerAbsentWarning,
            channel.Execution.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithServerNameUnset_WarnsNameMissingAndWritesTheDefaultConductor()
    {
        CliHarness h = NewHarness("install-name-missing");
        InstalledChannel channel = Channel("gate-on-name-missing", "true", SquadOperatorChannelFixture.ServerPresentConfig, null);
        channel.CopyInto(h);

        AssertExit(channel.Execution, 0);
        Assert.Contains(
            SquadOperatorChannelFixture.NameMissingWarning,
            channel.Execution.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithInvalidServerName_WarnsNameInvalidAndNeverEchoesTheValue()
    {
        CliHarness h = NewHarness("install-name-invalid");
        const string invalid = "bad name zz1";
        InstalledChannel channel = Channel("gate-on-name-invalid", "true", SquadOperatorChannelFixture.ServerPresentConfig, invalid);
        channel.CopyInto(h);

        AssertExit(channel.Execution, 0);
        Assert.Contains(
            SquadOperatorChannelFixture.NameInvalidWarning,
            channel.Execution.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(invalid, ReadConductor(h), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonTrueGateValues))]
    public void Install_CopilotGlobal_GateNotExactlyTrue_WritesNoOperatorChannelOutput(string gateValue)
    {
        CliHarness h = NewHarness("install-off");
        InstalledChannel channel = Channel(
            $"gate-not-true/{gateValue}",
            gateValue,
            SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertExit(channel.Execution, 0);
        Assert.DoesNotContain("operator-channel", channel.Execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-channel", ReadConductor(h), StringComparison.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(h.CopilotHome, "agents"), "*",
                     SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("operator-channel", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Install_GateTrue_ThenDoctorAndStatusReportNoDriftForTheSameEnvironment()
    {
        CliHarness h = NewHarness("install-then-doctor");
        InstalledChannel channel = Channel("gate-on-server-present", "true", SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertExit(channel.Execution, 0);
        Assert.Single(
            SquadOperatorChannelFixture.ToolEntries(ReadConductor(h)),
            entry => entry == SquadOperatorChannelFixture.ToolEntry);

        CommandExecution doctor = Run(() => Doctor(h, "true").Execute(null!, new SquadDoctorSettings
        {
            Path = h.Anchor,
            Global = true
        }));
        AssertExit(doctor, 0);
        Assert.DoesNotContain("Unmanaged global file", doctor.Output, StringComparison.Ordinal);

        CommandExecution status = Run(() => Status(h, "true").Execute(null!, new SquadStatusSettings
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
        CliHarness h = NewHarness("default-off-doctor");
        InstalledChannel channel = Channel("gate-off", null, SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertExit(channel.Execution, 0);

        CommandExecution doctor = Run(() => Doctor(h, null).Execute(null!, new SquadDoctorSettings
        {
            Path = h.Anchor,
            Global = true
        }));
        AssertExit(doctor, 0);
        Assert.DoesNotContain("Unmanaged global file", doctor.Output, StringComparison.Ordinal);

        CommandExecution status = Run(() => Status(h, null).Execute(null!, new SquadStatusSettings
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
        CliHarness h = NewHarness("update-on");
        InstalledChannel channel = Channel("gate-off", null, SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);
        AssertExit(channel.Execution, 0);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);

        CommandExecution update = Run(() => Update(h, "true").Execute(null!, new SquadUpdateSettings
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

    public static IEnumerable<object[]> NonTrueGateValues()
    {
        return SquadOperatorChannelFixture.NonTrueGateValues.Select(static value => new object[] { value });
    }

    /// <summary>Creates an empty home for one test; the prepared install's copy fills it.</summary>
    private CliHarness NewHarness(string name)
    {
        string root = Path.Combine(_temp.Path, name);
        string copilotHome = Path.Combine(root, "copilot-home");
        string anchor = Path.Combine(root, "anchor");
        string userHome = Path.Combine(root, "user-home");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(userHome);

        CliHarness harness = new(root, anchor, copilotHome, userHome);
        _harnesses.Add(harness);
        return harness;
    }

    /// <summary>
    /// Gets the run-wide install for one gate and server combination: the real install command ran once,
    /// and its exit code and console output are recorded here for every test that asserts them.
    /// </summary>
    private static InstalledChannel Channel(
        string name,
        string? gateValue,
        string? mcpConfigJson,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName)
    {
        string key = $"operator-channel-cli/{name}";
        return PreparedSquadInstalls.Get(key, () => InstalledChannel.Build(key, gateValue, mcpConfigJson, serverName));
    }

    private static SquadUpdateCommand Update(
        CliHarness h,
        string? gateValue = null,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName)
    {
        return SquadTestCommands.Update(
            h.UserPaths,
            h.Store,
            h.ReleaseSource,
            SquadHostDefaults.ResolveRenderer(),
            false,
            _ => true,
            h.GlobalRoots,
            environmentReader: h.CreateEnvironmentReader(gateValue, serverName));
    }

    /// <remarks>
    /// The working directory is the checked-in corpus so the global collision scan can locate the
    /// canonical source; without it doctor skips the scan and the drift check proves nothing.
    /// </remarks>
    private static SquadDoctorCommand Doctor(
        CliHarness h,
        string? gateValue = null,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName)
    {
        return SquadTestCommands.Doctor(
            new FakeProcessExecutor().WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n"),
            h.UserPaths,
            KyberWeaveTestPaths.ToolRoot,
            h.GlobalRoots,
            SquadHostDefaults.ResolveRenderer(),
            h.Store,
            h.CreateEnvironmentReader(gateValue, serverName));
    }

    private static SquadStatusCommand Status(
        CliHarness h,
        string? gateValue = null,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName)
    {
        return SquadTestCommands.Status(
            h.UserPaths,
            h.Store,
            h.GlobalRoots,
            h.CreateEnvironmentReader(gateValue, serverName));
    }

    private static string ReadConductor(CliHarness h)
    {
        return File.ReadAllText(Path.Combine(h.CopilotHome, "agents", "conductor.agent.md"));
    }

    private static void AssertExit(CommandExecution execution, int expected)
    {
        Assert.True(
            execution.ExitCode == expected,
            $"Expected exit {expected} but got {execution.ExitCode}: {execution.Output}");
    }

    private static CommandExecution Run(Func<int> execute) => Capture(execute);

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    /// <summary>
    /// One real <c>squad install</c> of the Copilot global shape, with the command's exit code and output,
    /// built once for the run and copied into each test's own home.
    /// </summary>
    private sealed class InstalledChannel : PreparedSquadInstall
    {
        private InstalledChannel(string key, TempDirectory directory, CommandExecution execution)
            : base(key, directory)
        {
            Execution = execution;
        }

        /// <summary>What the install command printed, and its exit code.</summary>
        public CommandExecution Execution { get; }

        /// <summary>Gives <paramref name="harness"/> a copy of the deployed home, keyed to the copy's own root.</summary>
        /// <param name="harness">The test's home. It receives the copilot home, user home and anchor contents.</param>
        public void CopyInto(CliHarness harness)
        {
            ArgumentNullException.ThrowIfNull(harness);

            SquadInstallCopy.Tree(TemplatePath, harness.Root);
            SquadInstallCopy.ReRoot(
                Path.Combine(harness.Root, "user-home", ".kyber-weave", "squad"),
                SquadInstallCopy.Canonical(TemplatePath),
                SquadInstallCopy.Canonical(harness.Root));
        }

        internal static InstalledChannel Build(
            string key,
            string? gateValue,
            string? mcpConfigJson,
            string? serverName)
        {
            TempDirectory? directory = new();
            try
            {
                string root = directory.Path;
                string copilotHome = Path.Combine(root, "copilot-home");
                string anchor = Path.Combine(root, "anchor");
                string userHome = Path.Combine(root, "user-home");
                Directory.CreateDirectory(copilotHome);
                Directory.CreateDirectory(anchor);
                Directory.CreateDirectory(userHome);
                if (mcpConfigJson is not null)
                    File.WriteAllText(Path.Combine(copilotHome, "mcp-config.json"), mcpConfigJson);

                using CliHarness harness = new(root, anchor, copilotHome, userHome);
                CommandExecution execution = Capture(() => SquadTestCommands.Install(
                        harness.UserPaths,
                        harness.Store,
                        harness.ReleaseSource,
                        SquadHostDefaults.ResolveRenderer(),
                        false,
                        _ => true,
                        harness.GlobalRoots,
                        environmentReader: harness.CreateEnvironmentReader(gateValue, serverName))
                    .Execute(null!, new SquadInstallSettings
                    {
                        Path = anchor,
                        Global = true,
                        Targets = ["copilot"],
                        Version = "1.2.3",
                        Yes = true
                    }));

                InstalledChannel install = new(key, directory, execution);
                directory = null;
                return install;
            }
            finally
            {
                directory?.Dispose();
            }
        }
    }

    /// <summary>One isolated install context: anchor, COPILOT_HOME, user paths, state store, and a corpus source.</summary>
    private sealed class CliHarness : IDisposable
    {
        private readonly SyntheticValidSquadReleaseSource _releaseSource = new();

        public CliHarness(string root, string anchor, string copilotHome, string userHome)
        {
            Root = root;
            Anchor = anchor;
            CopilotHome = copilotHome;
            UserPaths = new FakeSquadUserPaths(userHome);
            Store = new SquadStateStore(UserPaths);
            GlobalRoots = SquadOperatorChannelFixture.GlobalRoots(copilotHome, userHome);
        }

        public string Root { get; }

        public string Anchor { get; }

        public string CopilotHome { get; }

        public FakeSquadUserPaths UserPaths { get; }

        public SquadStateStore Store { get; }

        public ISquadGlobalRootResolver GlobalRoots { get; }

        public ISquadReleaseSource ReleaseSource => _releaseSource;

        public Func<string, string?> CreateEnvironmentReader(
            string? gateValue,
            string? serverName = SquadOperatorChannelFixture.DefaultServerName)
        {
            return SquadOperatorChannelFixture.CreateEnvironmentReader(gateValue, CopilotHome, serverName);
        }

        public void Dispose()
        {
            _releaseSource.Dispose();
        }
    }
}
