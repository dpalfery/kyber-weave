using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

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

        int exitCode = command.Execute(null!, new ArbiterSettings { Path = "/nowhere" });

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

        int exitCode = command.Execute(null!, new ArbiterSettings { Path = "/nowhere", Format = "json" });

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

        int exitCode = command.Execute(null!, new ArbiterSettings { Path = "/nowhere", Format = "json" });

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
        ArbiterGatingConsole gating = new(console);

        gating.WriteRows(new HarnessGatingReport([], []), token => token);

        Assert.Equal($"  no harness installed for this project.{System.Environment.NewLine}", console.Output);
    }

    /// <summary>A row keeps the label, its state and the fix command on one line, the fix after a semicolon.</summary>
    [Fact]
    public void GatingConsole_WritesARowWithItsFixCommand()
    {
        TestConsole console = new();
        ArbiterGatingConsole gating = new(console);
        HarnessGatingReport report = new(
            [new HarnessGatingRow("claude", "gated", "kyber-weave arbiter install --target claude", Ungated: false)],
            []);

        gating.WriteRows(report, token => token.ToUpperInvariant());

        Assert.Equal(
            $"  CLAUDE: gated; run kyber-weave arbiter install --target claude{System.Environment.NewLine}",
            console.Output);
    }

    /// <summary>
    /// Every Arbiter command the composition registers writes its output to the console the root registered. Resolution
    /// alone would not prove that: a command that still called the static console resolves just as well, so each one is
    /// run and its own output is read back.
    /// </summary>
    [Fact]
    public void Composition_HandsEveryArbiterCommandTheConsoleItRegistered()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        using ArbiterTestHost host = ArbiterTestHost.Create(services =>
        {
            services.AddSingleton<IAnsiConsole>(console);
            services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(homeDirectory: "/kw/home"));
            services.AddSingleton<IProcessExecutor>(new FakeProcessExecutor());
            services.AddSingleton<IGitCommandRunner>(
                new FakeGitCommandRunner((_, _) => new GitOutcome(1, string.Empty, string.Empty)));
        });

        // Each command runs in a state that prints and changes nothing: a usage error, an unreadable path, or the
        // defaults an empty configuration yields.
        List<(string Command, Func<int> Run)> commands =
        [
            ("arbiter validate", () => host.Command<ArbiterValidateCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository, Format = "json" })),
            ("arbiter rules", () => host.Command<ArbiterRulesCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository })),
            ("arbiter plan", () => host.Command<ArbiterPlanCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository + "/plan.md", Format = "json" })),
            ("arbiter eval", () => host.Command<ArbiterEvalCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository, Format = "json" })),
            ("arbiter audit", () => host.Command<ArbiterAuditCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository })),
            ("arbiter tune", () => host.Command<ArbiterTuneCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository })),
            ("arbiter setup", () => host.Command<ArbiterSetupCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository, Provider = "no-such-provider" })),
            ("arbiter status", () => host.Command<ArbiterStatusCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository })),
            ("arbiter doctor", () => host.Command<ArbiterDoctorCommand>()
                .Execute(null!, new ArbiterSettings { Path = NoRepository })),
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
    private const string NoRepository = "/kw/none";

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
