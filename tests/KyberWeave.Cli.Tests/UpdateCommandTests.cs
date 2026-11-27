using System.ComponentModel;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Commands.Update;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Update;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Domain.Update;

namespace KyberWeave.Cli.Tests;

/// <summary>
/// What <c>kyber-weave update</c> prints: the one outcome line or the one error line, never a raw exception, and the
/// same capture-plugin guidance the Squad deploy step relays. The command runs over the container with an in-memory
/// install, a fake release transport, a loopback hand-off and a captured console.
/// </summary>
public sealed class UpdateCommandTests : IDisposable
{
    private readonly UpdateWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void ExecuteWritesHumanOutput()
    {
        using ReleaseMapHandler handler = UpdateRelease.Map("0.2.0", "osx-arm64", windows: false);
        UpdateRelease.MapLatest(handler, "0.2.0");
        SelfUpdateHost host = _world.CreateHost("0.1.0", "osx-arm64");
        LoopbackFinishingProcess loopback = _world.Loopback(handler, host, _ => { }, _ => null);
        UpdateCommandRun run = UpdateCommandTestServices.Create(_world, host, handler, loopback.Invoke);

        int exit = run.Command.Execute(null!, new UpdateSettings());

        Assert.Equal(0, exit);
        Assert.Contains("updated kyber-weave 0.2.0", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A successful hand-off ends the run with exactly one bold outcome line. The child prints nothing on success, so
    /// the outcome is never printed twice, once by each process.
    /// </summary>
    [Fact]
    public void ExecuteAfterASuccessfulHandOffPrintsExactlyOneOutcomeLine()
    {
        using ReleaseMapHandler handler = UpdateRelease.Map("0.2.0", "osx-arm64", windows: false);
        SelfUpdateHost host = _world.CreateHost("0.1.0", "osx-arm64");
        List<IReadOnlyList<string>> handOffs = [];
        UpdateCommandRun run = UpdateCommandTestServices.Create(
            _world,
            host,
            handler,
            (_, arguments, _) =>
            {
                handOffs.Add(arguments);
                return 0;
            });

        int exit = run.Command.Execute(null!, new UpdateSettings { Version = "0.2.0" });

        Assert.True(exit == 0, $"exit {exit}; output: {run.Output}");
        Assert.Single(handOffs, arguments => arguments.Count > 0 && arguments[0] == "update");
        string[] lines = run.Output.Split('\n');
        Assert.Single(lines, line => line.StartsWith("updated kyber-weave 0.2.0 → ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed hand-off prints exactly one error line, and the line names the retry rather than echoing the raw
    /// exception text. A user reading the error needs the cause and the command, not a stack-shaped string.
    /// </summary>
    [Fact]
    public void ExecuteAfterAFailedHandOffPrintsExactlyOneErrorLineWithoutTheRawExceptionText()
    {
        Win32Exception failure = new(5, "Access is denied.");
        using ReleaseMapHandler handler = UpdateRelease.Map("0.2.0", "osx-arm64", windows: false);
        SelfUpdateHost host = _world.CreateHost("0.1.0", "osx-arm64");
        List<IReadOnlyList<string>> handOffs = [];
        UpdateCommandRun run = UpdateCommandTestServices.Create(
            _world,
            host,
            handler,
            (_, arguments, _) =>
            {
                handOffs.Add(arguments);
                if (arguments[0] == "update")
                    throw failure;

                return 0;
            });

        int exit = run.Command.Execute(null!, new UpdateSettings { Version = "0.2.0" });

        Assert.Equal(1, exit);
        Assert.Single(handOffs, arguments => arguments.Count > 0 && arguments[0] == "update");
        string[] lines = run.Output.Split('\n');
        string errorLine = Assert.Single(lines, line => line.StartsWith("kyber-weave: error:", StringComparison.Ordinal));
        Assert.NotEqual($"kyber-weave: error: {failure.Message}", errorLine);
        Assert.Contains("kyber-weave update 0.2.0", errorLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// A finish-mode run on the installed version prints no outcome line of its own. Its work is reported by the
    /// process that started it, so an empty outcome is the correct success.
    /// </summary>
    [Fact]
    public void ExecuteFinishingTheInstalledVersionPrintsNoOutcomeLine()
    {
        using ReleaseMapHandler handler = new();
        SelfUpdateHost host = _world.CreateHost("0.2.0", "osx-arm64");
        UpdateCommandRun run = UpdateCommandTestServices.Create(_world, host, handler);

        int exit = run.Command.Execute(null!, new UpdateSettings { Version = "0.2.0", FinishUpdate = true });

        Assert.Equal(0, exit);
        Assert.Empty(handler.Uris);
        string[] lines = run.Output.Split('\n');
        Assert.DoesNotContain(lines, line => line.Contains("already on", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("updated kyber-weave", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("kyber-weave: error:", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The update and the Squad deploy step relay the capture plugin's guidance, and they must relay the same
    ///     lines from the same verb output. The rule lived in a private copy in each of them, and the two copies
    ///     differed by one token, so nothing clustered them: a change to one copy left the other relaying something
    ///     else, and only a reader would notice. This test runs both paths over one verb output and holds them
    ///     together.
    /// </summary>
    [Theory]
    [InlineData("opencode")]
    [InlineData("factory")]
    public void TheUpdateAndTheSquadStepRelayTheSameCapturePluginGuidance(string harness)
    {
        string stdout = CapturePluginVerbStdout(harness);

        _world.InstallKyberDash();
        using ReleaseMapHandler handler = UpdateRelease.Map("0.10.0", "osx-arm64", windows: false, withKyberDash: true);
        SelfUpdateHost host = _world.CreateHost("0.1.0", "osx-arm64");
        List<string> log = [];
        Func<string, string?> environment = HomeWithTray();
        Func<string, IReadOnlyList<string>, ProcessResult> capture = (_, args) =>
            HarnessOf(args) == harness ? new ProcessResult(0, stdout, string.Empty) : UpdateWorld.QuietSuccess;
        SelfUpdateOutcome outcome = _world.Run(
            handler,
            host,
            new SelfUpdateOptions("0.10.0", false, false),
            environment,
            log: log.Add,
            runProcess: (_, _) => 0,
            captureProcess: capture);

        Assert.Equal(0, outcome.ExitCode);

        FakeProcessExecutor executor = new FakeProcessExecutor().WithProbeOutput("kyberdash", stdout);
        using Spectre.Console.Testing.TestConsole squadConsole = new() { EmitAnsiSequences = false };
        squadConsole.Profile.Width = 400;
        KyberDashCapturePluginStep step = new(
            executor,
            () => Path.Combine(_world.Install, "kyberdash"),
            new SquadConsole(squadConsole, new FakeConsole(isInputRedirected: true)));
        CapturePluginStepOutcome stepOutcome = step.Run(
            TargetOf(harness),
            harness == "opencode" ? Path.Combine(_world.Files.Root, "opencode") : null,
            false);

        Assert.Equal(CapturePluginStepOutcome.Completed, stepOutcome);

        string[] expected = ExpectedGuidance(harness);
        Assert.Equal(expected, RelayedGuidance(log));
        Assert.Equal(expected, RelayedGuidance(squadConsole.Output.Split('\n')));
        // Neither path relays the verb's chatter, and neither relays the verdict as guidance.
        Assert.DoesNotContain(log, line => line.Contains("verb chatter", StringComparison.Ordinal));
        Assert.DoesNotContain(squadConsole.Output, "verb chatter", StringComparison.Ordinal);
    }

    /// <summary>A home with a recorded tray, so the update's reconciliation runs every step.</summary>
    private Func<string, string?> HomeWithTray()
    {
        string home = _world.InInstall("home");
        _world.Files.CreateDirectory(Path.Combine(home, ".kyberdash"));
        _world.Files.WriteAllText(
            Path.Combine(home, ".kyberdash", "tray.json"),
            """{"path":"/Applications/KyberDash.app","version":"0.9.23"}""");
        return _world.HomeEnvironment(home);
    }

    /// <summary>The harness a verb names, or an empty string when it names none.</summary>
    private static string HarnessOf(IReadOnlyList<string> args)
    {
        for (int index = 0; index + 1 < args.Count; index++)
            if (args[index] == "--harness")
                return args[index + 1];

        return string.Empty;
    }

    /// <summary>
    ///     The capture-plugin verb's stdout for one harness: a verdict line, two guidance lines, and chatter that is
    ///     not guidance. The wording after the harness prefix is KyberDash's, so these tests use neutral text.
    /// </summary>
    private static string CapturePluginVerbStdout(string harness)
    {
        return harness == "opencode"
            ? "opencode plugin: upgraded /fake/plugins/kyberdash-opencode.js\n" +
              $"harness opencode: relayed guidance alpha\n" +
              $"harness opencode: relayed guidance beta\n" +
              "verb chatter that is not guidance\n"
            : "factory plugin: upgraded /fake/plugins/kyberdash-factory.js\n" +
              "factory plugin: relayed guidance alpha\n" +
              "factory plugin: relayed guidance beta\n" +
              "verb chatter that is not guidance\n";
    }

    /// <summary>The guidance lines both paths must relay for one harness, in printed order.</summary>
    private static string[] ExpectedGuidance(string harness)
    {
        return harness == "opencode"
            ? ["relayed guidance alpha", "relayed guidance beta"]
            : ["factory plugin: relayed guidance alpha", "factory plugin: relayed guidance beta"];
    }

    /// <summary>The relayed guidance lines in a printed output, in printed order.</summary>
    private static string[] RelayedGuidance(IEnumerable<string> lines)
    {
        return lines
            .Select(line => line.Trim())
            .Where(line => line.Contains("relayed guidance", StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>The Squad harness a verb token names.</summary>
    private static SquadTarget TargetOf(string harness)
    {
        return harness == "opencode" ? SquadTarget.OpenCode : SquadTarget.Factory;
    }
}
