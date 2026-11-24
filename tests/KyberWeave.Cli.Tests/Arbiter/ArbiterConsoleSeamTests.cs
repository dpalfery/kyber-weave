using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Fakes;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The Arbiter commands write to the console they were constructed with rather than the process-wide static one, so a
/// unit test reads their output without a terminal and the composition root decides which console they get.
/// </summary>
public sealed class ArbiterConsoleSeamTests
{
    [Fact]
    public void Rules_WritesItsTableAndCountToTheConsoleItWasGiven()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        ArbiterRulesCommand command = new(new FixedArbiterConfigs(), console);

        int exitCode = command.Execute(null!, new ArbiterSettings { Path = NoRepository });

        Assert.Equal(0, exitCode);
        Assert.Contains("KW-ARB-", console.Output, StringComparison.Ordinal);
        Assert.Contains("rule(s) listed", console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A report rendered through <see cref="KyberWeave.Cli.Commands.CommandHelpers.Finish"/> also goes to the injected
    /// console: a command's findings are its main output, and a test that cannot read them can only prove the happy path.
    /// </summary>
    [Fact]
    public void Rules_AConfigurationThatFailsToLoad_WritesItsFindingToTheConsoleItWasGiven()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        ArbiterRulesCommand command = new(new FailingArbiterConfigs(), console);

        int exitCode = command.Execute(null!, new ArbiterSettings { Path = NoRepository, Format = "json" });

        Assert.Equal(1, exitCode);
        Assert.Contains(FailingArbiterConfigs.Code, console.Output, StringComparison.Ordinal);
    }

    /// <summary><c>arbiter validate</c> is one of the commands, so its findings reach the injected console too.</summary>
    [Fact]
    public void Validate_AConfigurationThatFailsToLoad_WritesItsFindingToTheConsoleItWasGiven()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        ArbiterValidateCommand command = new(new FailingArbiterConfigs(), console);

        int exitCode = command.Execute(null!, new ArbiterSettings { Path = NoRepository, Format = "json" });

        Assert.Equal(1, exitCode);
        Assert.Contains(FailingArbiterConfigs.Code, console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The key input reads both of its answers through the seams it was given: the standard-input line from
    /// <see cref="IConsole"/> and the masked prompt from the injected <see cref="IAnsiConsole"/>, so a test scripts a key
    /// instead of typing one, and neither answer comes from the process's own streams.
    /// </summary>
    [Fact]
    public void KeyInput_ReadsBothAnswersThroughTheSeamsItWasGiven()
    {
        TestConsole console = new();
        console.Input.PushTextWithEnter("TS-PROMPTED-KEY");
        FakeConsole standard = new(isInputRedirected: true);
        standard.ScriptInput("TS-STDIN-KEY\n");
        ConsoleArbiterKeyInput input = new(console, standard);

        string? fromStdin = input.ReadFromStandardInput();
        string? fromPrompt = input.PromptHidden();

        Assert.Equal("TS-STDIN-KEY", fromStdin);
        Assert.Equal("TS-PROMPTED-KEY", fromPrompt);
    }

    /// <summary>The empty notice is the exact text <c>arbiter status</c> and <c>arbiter doctor</c> printed before.</summary>
    [Fact]
    public void GatingConsole_WritesTheEmptyNoticeUnchanged()
    {
        TestConsole console = new();

        ArbiterGatingConsole.WriteRows(console, new HarnessGatingReport([], []), token => token);

        Assert.Equal($"  no harness installed for this project.{System.Environment.NewLine}", console.Output);
    }

    /// <summary>A row keeps the label, its state and the fix command on one line, the fix after a semicolon.</summary>
    [Fact]
    public void GatingConsole_WritesARowWithItsFixCommand()
    {
        TestConsole console = new();
        HarnessGatingReport report = new(
            [new HarnessGatingRow("claude", "gated", "kyber-weave arbiter install --target claude", Ungated: false)],
            []);

        ArbiterGatingConsole.WriteRows(console, report, token => token.ToUpperInvariant());

        Assert.Equal(
            $"  CLAUDE: gated; run kyber-weave arbiter install --target claude{System.Environment.NewLine}",
            console.Output);
    }

    /// <summary>
    /// <c>arbiter status</c> writes its gating rows to the same console as the rest of its report. The command takes one
    /// console, so there is no second one for a row to go to; before that it took a renderer wrapping its own console,
    /// and the lifting applied to one console while rows were written to the other.
    /// </summary>
    [Fact]
    public void Status_WritesItsGatingRowsToItsOwnConsole()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        ArbiterStatusCommand command = new(
            new FixedArbiterConfigs(),
            new NoKeyService(),
            new FakeProcessEnvironment(homeDirectory: ArbiterTestRoots.Under("kw", "home")),
            new NoUserConfigStore(),
            new NoRepositoryLocator(),
            new OneRowGating(),
            console);

        _ = command.Execute(null!, new ArbiterSettings { Path = NoRepository });

        Assert.Contains("GLOBAL scope", console.Output, StringComparison.Ordinal);
        Assert.Contains("PROJECT scope", console.Output, StringComparison.Ordinal);
        Assert.Contains(
            "  Claude: gated; run kyber-weave arbiter install --project --target claude",
            console.Output,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A width lifted for one write is restored on the console it was lifted from, even when the console a caller holds
    /// starts naming a different console during the write. Restoring through the caller instead would leave the first
    /// console unfolded at <see cref="int.MaxValue"/> and overwrite the second one's width.
    /// </summary>
    [Fact]
    public void WriteUnfolded_RestoresTheWidthOfTheConsoleItLifted_WhenTheConsoleSwitchesMidWrite()
    {
        TestConsole first = new();
        first.Profile.Width = 80;
        TestConsole second = new();
        second.Profile.Width = 100;
        SwitchingConsole console = new(first);

        CommandHelpers.WriteUnfolded(console, () => console.Use(second));

        Assert.Equal(80, first.Profile.Width);
        Assert.Equal(100, second.Profile.Width);
    }

    /// <summary>
    /// Every Arbiter command the composition registers writes its output to the console the root registered. Resolution
    /// alone would not prove that: a command that still called the static console resolves just as well, so each one is
    /// run and its own output is read back.
    /// </summary>
    [Fact]
    public void Composition_HandsEveryArbiterCommandTheConsoleItRegistered()
    {
        using InMemoryArbiterHost host = InMemoryArbiterHost.Create();
        TestConsole console = host.Console;
        string noRepository = host.InWork("none");

        // Each command runs in a state that prints and changes nothing: a usage error, an unreadable path, or the
        // defaults an empty configuration yields.
        List<(string Command, Func<int> Run)> commands =
        [
            ("arbiter validate", () => host.Command<ArbiterValidateCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository, Format = "json" })),
            ("arbiter rules", () => host.Command<ArbiterRulesCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository })),
            ("arbiter plan", () => host.Command<ArbiterPlanCommand>()
                .Execute(null!, new ArbiterSettings { Path = Path.Combine(noRepository, "plan.md"), Format = "json" })),
            ("arbiter eval", () => host.Command<ArbiterEvalCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository, Format = "json" })),
            ("arbiter audit", () => host.Command<ArbiterAuditCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository })),
            ("arbiter tune", () => host.Command<ArbiterTuneCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository })),
            ("arbiter setup", () => host.Command<ArbiterSetupCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository, Provider = "no-such-provider" })),
            ("arbiter status", () => host.Command<ArbiterStatusCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository })),
            ("arbiter doctor", () => host.Command<ArbiterDoctorCommand>()
                .Execute(null!, new ArbiterSettings { Path = noRepository })),
            ("arbiter sections", () => host.Command<ArbiterSectionsCommand>()
                .Execute(null!, new ArbiterSectionsSettings())),
            ("arbiter comment", () => host.Command<ArbiterCommentCommand>()
                .Execute(null!, new ArbiterCommentSettings())),
            ("arbiter install", () => host.Command<ArbiterInstallCommand>()
                .Execute(null!, new ArbiterInstallSettings { Target = "no-such-target" })),
            ("arbiter uninstall", () => host.Command<ArbiterUninstallCommand>()
                .Execute(null!, new ArbiterUninstallSettings { Target = "no-such-target" })),
        ];

        foreach ((string command, Func<int> run) in commands)
        {
            int before = console.Output.Length;
            int exitCode = 0;

            // The process-wide console is captured for the run, so a command that still reaches it is caught even when
            // it also writes something to the injected one.
            CapturedConsoleExecution<int> processConsole =
                ProcessConsoleCapture.Run(() => exitCode = run(), width: 200);

            Assert.True(
                console.Output.Length > before,
                $"{command} wrote nothing to the console it was given (exit code {exitCode}).");
            Assert.True(
                processConsole.Output.Length == 0,
                $"{command} also wrote to the process-wide console: {processConsole.Output}");
        }
    }

    /// <summary>A repository path that exists nowhere, so every command takes its not-found path.</summary>
    private static readonly string NoRepository = ArbiterTestRoots.Under("kw", "none");

    /// <summary>A console a test points at one <see cref="TestConsole"/> and then another, mid-write.</summary>
    private sealed class SwitchingConsole(TestConsole first) : IAnsiConsole
    {
        private IAnsiConsole _current = first;

        internal void Use(IAnsiConsole next) => _current = next;

        public Profile Profile => _current.Profile;

        public IAnsiConsoleCursor Cursor => _current.Cursor;

        public IAnsiConsoleInput Input => _current.Input;

        public IExclusivityMode ExclusivityMode => _current.ExclusivityMode;

        public RenderPipeline Pipeline => _current.Pipeline;

        public void Clear(bool home) => _current.Clear(home);

        public void Write(IRenderable renderable) => _current.Write(renderable);

        public void WriteAnsi(Action<AnsiWriter> action) => _current.WriteAnsi(action);
    }

    /// <summary>Resolves no key: the provider in these tests is <c>none</c>, which needs none.</summary>
    private sealed class NoKeyService : IArbiterKeyService
    {
        public string? ResolveKey(KyberWeaveConfig config, string endpoint) => null;

        public string? ResolveKey(KyberWeaveConfig config, ArbiterProviderConfig effectiveProvider, string endpoint) => null;

        public string? TryResolveKey(KyberWeaveConfig config, string endpoint) => null;

        public string? TryResolveKey(KyberWeaveConfig config, ArbiterProviderConfig effectiveProvider, string endpoint) => null;

        public Func<Uri, string?> CreateKeyResolver(KyberWeaveConfig config) => _ => null;
    }

    /// <summary>Holds no global targets, so the status report's GLOBAL section is the empty notice.</summary>
    private sealed class NoUserConfigStore : IUserHostConfigStore
    {
        public string EnsureUserConfig(string home) => string.Empty;

        public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
        {
        }

        public void RemoveGlobalTarget(string home, string target)
        {
        }

        public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
    }

    /// <summary>Names the path it is given as the repository root, without running git.</summary>
    private sealed class NoRepositoryLocator : IArbiterRepositoryLocator
    {
        public string ResolveRepositoryRoot(string path) => path;

        public string ResolveArbiterDirectory(string path) => path + "/artifacts/arbiter";

        public string FindGitRoot(string start) => start;
    }

    /// <summary>Reports one gated harness, so a gating row is written.</summary>
    private sealed class OneRowGating : IArbiterHarnessGating
    {
        public HarnessGatingReport Build(
            string root,
            string home,
            KyberWeaveConfig config,
            IReadOnlyList<string> globalTargets) =>
            new(
                [new HarnessGatingRow(
                    "claude",
                    "gated",
                    "kyber-weave arbiter install --project --target claude",
                    Ungated: false)],
                []);
    }

    /// <summary>A configuration resolver that answers with the product defaults and reads nothing.</summary>
    private sealed class FixedArbiterConfigs : IArbiterConfigResolver
    {
        public bool TryResolve(
            string path,
            string? configFile,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? configPath)
        {
            config = KyberWeaveConfigDefaults.Product;
            configPath = null;
            return true;
        }

        public KyberWeaveConfig? TryLoadRepository(string root) => KyberWeaveConfigDefaults.Product;

        public KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope) =>
            new(true, KyberWeaveConfigDefaults.Product);

        public KyberWeaveConfig LoadFromYaml(string yaml) => KyberWeaveConfigDefaults.Product;
    }

    /// <summary>A configuration resolver that reports the load failure the way the real one does.</summary>
    private sealed class FailingArbiterConfigs : IArbiterConfigResolver
    {
        internal const string Code = "KW-CONFIG-001";

        public bool TryResolve(
            string path,
            string? configFile,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? configPath)
        {
            report.Add(new Diagnostic(Code, Severity.Error, "kyber-weave.yml could not be read.", "kyber-weave.yml"));
            config = KyberWeaveConfigDefaults.Product;
            configPath = null;
            return false;
        }

        public KyberWeaveConfig? TryLoadRepository(string root) => null;

        public KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope) =>
            new(false, error: "kyber-weave.yml could not be read.");

        public KyberWeaveConfig LoadFromYaml(string yaml) => KyberWeaveConfigDefaults.Product;
    }
}
