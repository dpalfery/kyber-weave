using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Arbiter;
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
    /// Every Arbiter command the composition registers resolves, and the console it writes to is the one the root
    /// registered, so a test substituting one sees the output.
    /// </summary>
    [Fact]
    public void Composition_HandsEveryArbiterCommandTheRegisteredConsole()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        using ArbiterTestHost host = ArbiterTestHost.Create(services => services.AddSingleton<IAnsiConsole>(console));

        // Resolution proves the wiring: a command with an unregistered constructor parameter throws here.
        _ = host.Command<ArbiterAuditCommand>();
        _ = host.Command<ArbiterCommentCommand>();
        _ = host.Command<ArbiterDoctorCommand>();
        _ = host.Command<ArbiterEvalCommand>();
        _ = host.Command<ArbiterInstallCommand>();
        _ = host.Command<ArbiterPlanCommand>();
        _ = host.Command<ArbiterSectionsCommand>();
        _ = host.Command<ArbiterSetupCommand>();
        _ = host.Command<ArbiterStatusCommand>();
        _ = host.Command<ArbiterTuneCommand>();
        _ = host.Command<ArbiterUninstallCommand>();
        _ = host.Command<ArbiterValidateCommand>();

        int exitCode = host.Command<ArbiterRulesCommand>().Execute(null!, new ArbiterSettings { Path = "/nowhere" });

        Assert.Equal(0, exitCode);
        Assert.Contains("rule(s) listed", console.Output, StringComparison.Ordinal);
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
