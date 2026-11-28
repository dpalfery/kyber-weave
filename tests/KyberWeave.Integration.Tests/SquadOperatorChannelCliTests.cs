using KyberWeave.Testing.Squad;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Squad;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Integration.Tests;

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
/// parallel test execution. One real install runs for each distinct gate and server input, keyed by that
/// input in <see cref="PreparedSquadInstalls"/> and built once for the whole run before the first test
/// body: a test reads the recorded exit code and console output and works on its own copy of the
/// deployed home. The update fact still runs its update, because its subject is the sequence of an
/// install followed by an update over it.
/// </remarks>
public sealed class SquadOperatorChannelCliTests : IDisposable
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string InstallVersion = "1.2.3";
    private const string InvalidServerName = "bad name zz1";

    /// <summary>
    /// Every prepared install for the class, built on the first constructor call so that no real install
    /// runs inside a test body.
    /// </summary>
    private static readonly Lazy<PreparedInstalls> Prepared = new(BuildPreparedInstalls);

    private readonly TempDirectory _temp = new();
    private readonly List<CliHarness> _harnesses = [];

    /// <summary>Builds every prepared install before the test body runs, so a test only reads a record or a copy.</summary>
    public SquadOperatorChannelCliTests()
    {
        _ = Prepared.Value;
    }

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
        InstalledChannel channel = Channel("true", SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertInstalled(channel);
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
        InstalledChannel channel = Channel("true", null);
        channel.CopyInto(h);

        AssertInstalled(channel);
        Assert.Contains(
            SquadOperatorChannelFixture.ServerAbsentWarning,
            channel.Record.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithServerNameUnset_WarnsNameMissingAndWritesTheDefaultConductor()
    {
        CliHarness h = NewHarness("install-name-missing");
        InstalledChannel channel = Channel("true", SquadOperatorChannelFixture.ServerPresentConfig, null);
        channel.CopyInto(h);

        AssertInstalled(channel);
        Assert.Contains(
            SquadOperatorChannelFixture.NameMissingWarning,
            channel.Record.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotGlobal_GateTrueWithInvalidServerName_WarnsNameInvalidAndNeverEchoesTheValue()
    {
        CliHarness h = NewHarness("install-name-invalid");
        const string invalid = InvalidServerName;
        InstalledChannel channel = Channel("true", SquadOperatorChannelFixture.ServerPresentConfig, invalid);
        channel.CopyInto(h);

        AssertInstalled(channel);
        Assert.Contains(
            SquadOperatorChannelFixture.NameInvalidWarning,
            channel.Record.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(invalid, ReadConductor(h), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonTrueGateValues))]
    public void Install_CopilotGlobal_GateNotExactlyTrue_WritesNoOperatorChannelOutput(string gateValue)
    {
        CliHarness h = NewHarness("install-off");
        InstalledChannel channel = Channel(gateValue, SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertInstalled(channel);
        Assert.DoesNotContain("operator-channel", channel.Record.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-channel", ReadConductor(h), StringComparison.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(h.CopilotHome, "agents"), "*",
                     SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("operator-channel", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Install_GateTrue_ThenDoctorAndStatusReportNoDriftForTheSameEnvironment()
    {
        CliHarness h = NewHarness("install-then-doctor");
        InstalledChannel channel = Channel("true", SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertInstalled(channel);
        Assert.Single(
            SquadOperatorChannelFixture.ToolEntries(ReadConductor(h)),
            entry => entry == SquadOperatorChannelFixture.ToolEntry);

        CommandExecution doctor = await RunAsync(() => Doctor(h, "true").ExecuteAsync(null!, new SquadDoctorSettings
        {
            Path = h.Anchor,
            Global = true
        }, CancellationToken.None));
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
    public async Task Install_GateUnset_DoctorAndStatusReportNoDrift()
    {
        CliHarness h = NewHarness("default-off-doctor");
        InstalledChannel channel = Channel(null, SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);

        AssertInstalled(channel);

        CommandExecution doctor = await RunAsync(() => Doctor(h, null).ExecuteAsync(null!, new SquadDoctorSettings
        {
            Path = h.Anchor,
            Global = true
        }, CancellationToken.None));
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
    public async Task Update_CopilotGlobal_GateTrueWithServerFound_AddsTheServerToolEntryToTheInstalledConductor()
    {
        CliHarness h = NewHarness("update-on");
        InstalledChannel channel = Channel(null, SquadOperatorChannelFixture.ServerPresentConfig);
        channel.CopyInto(h);
        AssertInstalled(channel);
        Assert.DoesNotContain(SquadOperatorChannelFixture.ToolEntry, ReadConductor(h), StringComparison.Ordinal);

        CommandExecution update = await RunAsync(() => Update(h, "true").ExecuteAsync(null!, new SquadUpdateSettings
        {
            Path = h.Anchor,
            Global = true,
            Targets = ["copilot"],
            Version = "1.2.4",
            Yes = true
        }, CancellationToken.None));

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
    /// Gets the run-wide install for one gate and server input. Every input the class uses is built
    /// before the first test body; a test only reads this record and copies the deployed home.
    /// </summary>
    private static InstalledChannel Channel(
        string? gateValue,
        string? mcpConfigJson,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName)
    {
        return Prepared.Value.For(gateValue, mcpConfigJson, serverName);
    }

    /// <summary>
    /// Builds one real <c>squad install</c> for every gate and server input the class's tests assert on,
    /// so each distinct input becomes one prepared install for the run and none runs inside a test body.
    /// </summary>
    private static PreparedInstalls BuildPreparedInstalls()
    {
        List<InstalledChannel> installs =
        [
            BuildInstalledChannel("true", SquadOperatorChannelFixture.ServerPresentConfig),
            BuildInstalledChannel("true", null),
            BuildInstalledChannel("true", SquadOperatorChannelFixture.ServerPresentConfig, null),
            BuildInstalledChannel("true", SquadOperatorChannelFixture.ServerPresentConfig, InvalidServerName),
            BuildInstalledChannel(null, SquadOperatorChannelFixture.ServerPresentConfig),
        ];
        installs.AddRange(SquadOperatorChannelFixture.NonTrueGateValues
            .Select(value => BuildInstalledChannel(value, SquadOperatorChannelFixture.ServerPresentConfig)));

        return new PreparedInstalls(installs);
    }

    private static InstalledChannel BuildInstalledChannel(
        string? gateValue,
        string? mcpConfigJson,
        string? serverName = SquadOperatorChannelFixture.DefaultServerName)
    {
        string key = RecipeKey(gateValue, mcpConfigJson, serverName);
        return PreparedSquadInstalls.Get(key, () => InstalledChannel.Build(key, gateValue, mcpConfigJson, serverName));
    }

    /// <summary>The key naming one install by its full input: the gate, the server name and the config already in the home.</summary>
    private static string RecipeKey(string? gateValue, string? mcpConfigJson, string? serverName) =>
        SquadInstallRecipe.Key(
            "install",
            SquadDeploymentScope.Global,
            [SquadTarget.Copilot],
            ("case", "operator-channel-cli"),
            ("version", InstallVersion),
            ("gate", gateValue),
            ("server", serverName),
            ("mcp-config",
                mcpConfigJson is null
                    ? null
                    : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(mcpConfigJson)))));

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
            Corpus.Root,
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

    private static void AssertInstalled(InstalledChannel channel)
    {
        Assert.True(
            channel.Record.ExitCode == 0,
            $"Expected exit 0 but got {channel.Record.ExitCode}: {channel.Record.StandardOutput}");
    }

    private static CommandExecution Run(Func<int> execute) => Capture(execute);

    private static async Task<CommandExecution> RunAsync(Func<Task<int>> execute)
    {
        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(execute);
        return new CommandExecution(execution.Result, execution.Output, execution.ErrorOutput);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output, execution.ErrorOutput);
    }

    private sealed record CommandExecution(int ExitCode, string Output, string ErrorOutput);

    /// <summary>
    /// One real <c>squad install</c> of the Copilot global shape, with the record of what the command
    /// returned, built once for the run and copied into each test's own home.
    /// </summary>
    private sealed class InstalledChannel(string key, TempDirectory directory, SquadInstallRecord record)
        : PreparedSquadInstall(key, directory, record)
    {
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
                    .ExecuteAsync(null!, new SquadInstallSettings
                    {
                        Path = anchor,
                        Global = true,
                        Targets = ["copilot"],
                        Version = InstallVersion,
                        Yes = true
                    }, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());

                InstalledChannel install = new(
                    key,
                    directory,
                    new SquadInstallRecord
                    {
                        ExitCode = execution.ExitCode,
                        StandardOutput = execution.Output,
                        StandardError = execution.ErrorOutput
                    });
                directory = null;
                return install;
            }
            finally
            {
                directory?.Dispose();
            }
        }
    }

    /// <summary>
    /// The class's prepared installs, looked up by the full input they were built from. A test that asks
    /// for an input nobody prepared fails here rather than installing inside its own body.
    /// </summary>
    private sealed class PreparedInstalls
    {
        private readonly IReadOnlyDictionary<string, InstalledChannel> _installs;

        public PreparedInstalls(IEnumerable<InstalledChannel> installs)
        {
            ArgumentNullException.ThrowIfNull(installs);
            _installs = installs.ToDictionary(static install => install.Key, StringComparer.Ordinal);
        }

        public InstalledChannel For(string? gateValue, string? mcpConfigJson, string? serverName)
        {
            string key = RecipeKey(gateValue, mcpConfigJson, serverName);
            return _installs.TryGetValue(key, out InstalledChannel? install)
                ? install
                : throw new InvalidOperationException(
                    $"No prepared install was built for '{key}'. Add that input to BuildPreparedInstalls: a test must not install inside its body.");
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
            Store = RealSquadState.Store(UserPaths);
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
