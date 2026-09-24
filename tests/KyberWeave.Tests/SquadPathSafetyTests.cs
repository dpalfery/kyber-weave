using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract suite for squad target-root argument safety (development mode: test-first,
/// docs/plans/2026-09-23-squad-path-argument-safety.md, §6 row T3). Pins the seams tasks
/// T4/T5 must implement: the long-only <c>--path</c> option binding onto the separate
/// <c>PathOption</c> property under a strict-configured CommandApp, the
/// positional/option coalescing and conflict rule, the injectable target-root
/// confirmation matrix, and the decline/non-interactive wiring in the mutating command.
/// Authored before the seams exist: every compile failure against
/// <c>PathOption</c>, <c>CoalesceTargetPath</c>, <c>SquadTargetRootConfirmation</c>, and
/// the optional <c>isInteractive</c>/<c>readAnswer</c> constructor parameters is the
/// recorded RED evidence, as is the failing <c>UseStrictParsing</c> assertion in
/// SquadCliCommandTests.
/// </summary>
/// <remarks>
/// The confirmation helper's <c>readAnswer</c> seam is contracted as
/// <c>Func&lt;string, bool&gt;</c> (question in, answer out) so tests inject the decision
/// without coupling to yes/no parsing conventions. Everything runs offline: release
/// fetches go through <see cref="FakeSquadReleaseSource"/>, and interactivity is injected
/// through delegates because xunit-redirected console streams make every real-console
/// check report non-interactive.
/// </remarks>
public sealed class SquadPathSafetyTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    #region Parser contract (§6 T3 row a): strict parsing and --path binding

    [Fact]
    public void StrictCommandApp_BindsPathOptionOntoSquadInstallSettings()
    {
        // --path must bind the separate PathOption property, leaving the positional
        // argument at its "." default; the coalescing rule (N6) then lets --path win.
        string deploymentRoot = Path.Combine(_temp.Path, "bind-root");

        PathBindingStubCommand.Captured = null;
        (int ExitCode, string Output) execution = RunThroughStrictApp(["install", "--path", deploymentRoot]);

        Assert.Equal(0, execution.ExitCode);
        SquadInstallSettings? captured = PathBindingStubCommand.Captured;
        Assert.NotNull(captured);
        Assert.Equal(deploymentRoot, captured.PathOption);
        Assert.Equal(".", captured.Path);
    }

    [Fact]
    public void StrictCommandApp_UnknownOption_ExitsNonZeroAndNamesTheOffender()
    {
        // The incident: relaxed parsing silently discarded an unrecognized option with
        // its value and proceeded against the current directory. Strict parsing must
        // hard-error with the offender named instead.
        PathBindingStubCommand.Captured = null;
        (int ExitCode, string Output) execution = RunThroughStrictApp(["install", "--definitely-not-an-option", "value"]);

        Assert.NotEqual(0, execution.ExitCode);
        Assert.Contains("definitely-not-an-option", Normalize(execution.Output), StringComparison.OrdinalIgnoreCase);
        Assert.Null(PathBindingStubCommand.Captured);
    }

    [Fact]
    public void StrictCommandApp_SurplusPositional_ExitsNonZero()
    {
        // Strict mode also rejects surplus positionals (plan §10); the tighter behavior
        // is intended and pinned so it cannot quietly regress to relaxed parsing.
        PathBindingStubCommand.Captured = null;
        (int ExitCode, string Output) execution = RunThroughStrictApp(["install", "first", "second"]);

        Assert.NotEqual(0, execution.ExitCode);
    }

    #endregion

    #region Coalescing contract (§6 T3 row b): positional vs --path

    [Theory]
    [InlineData(".", "/absolute/option-root")]
    [InlineData(".", "relative/option-root")]
    [InlineData("", "option-root")]
    [InlineData("   ", "option-root")]
    public void CoalesceTargetPath_WhenOptionSupplied_OptionWinsOverTheDefaultPositional(string positional, string option)
    {
        // N6: --path with the positional at its "." default is the normal case and
        // --path wins. The option value passes through verbatim; ResolveTargetRoot
        // absolutizes it afterwards.
        Assert.Equal(option, SquadCommandComposition.CoalesceTargetPath(positional, option));
    }

    [Theory]
    [InlineData("custom-positional-root")]
    [InlineData("./sub/dir")]
    public void CoalesceTargetPath_WhenNoOptionSupplied_PositionalPassesThrough(string positional)
    {
        // The positional argument remains the documented form; coalescing must not
        // disturb the option-free path every existing caller relies on.
        Assert.Equal(positional, SquadCommandComposition.CoalesceTargetPath(positional, null));
    }

    [Fact]
    public void CoalesceTargetPath_WhenPositionalAndOptionConflict_ThrowsNamingBothForms()
    {
        // N6: supplying both a non-default positional and --path is a conflict. The
        // failure follows the token-validation catch pattern (ArgumentException, which
        // the commands translate into exit 2) and the hint names both forms so the
        // operator can pick one.
        string positional = "conflicting-positional-root";

        ArgumentException conflict = Assert.Throws<ArgumentException>(
            () => SquadCommandComposition.CoalesceTargetPath(positional, "conflicting-option-root"));

        Assert.Contains(positional, conflict.Message, StringComparison.Ordinal);
        Assert.Contains("--path", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_WhenPositionalAndPathOptionConflict_ExitsTwoNamingBothForms()
    {
        // The command wiring surfaces the conflict as the convention's exit-2 abort
        // before any resolution or filesystem work, with the hint naming both forms.
        string targetDir = Path.Combine(_temp.Path, "conflict-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "conflict-user"));
        SquadInstallCommand command = new(userPaths);

        (int ExitCode, string Output) execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = Path.Combine(_temp.Path, "conflicting-positional-root"),
                PathOption = Path.Combine(_temp.Path, "conflicting-option-root"),
                Targets = ["codex"]
            }));

        Assert.Equal(2, execution.ExitCode);
        string normalized = Normalize(execution.Output);
        Assert.Contains("conflicting-positional-root", normalized, StringComparison.Ordinal);
        Assert.Contains("--path", normalized, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(targetDir, ".kyber-weave")));
    }

    #endregion

    #region Confirmation helper contract (§6 T3 row c): SquadTargetRootConfirmation matrix

    [Fact]
    public void Confirm_WhenInteractiveAccepted_ProceedsAfterPromptingOnce()
    {
        string echoRoot = Path.Combine(_temp.Path, "confirm-accept-root");
        int promptCount = 0;

        (bool Proceed, string Output) execution = Capture(() => SquadTargetRootConfirmation.Confirm(
            echoRoot,
            SquadDeploymentScope.Project,
            "install",
            isInteractive: true,
            yes: false,
            readAnswer: _ =>
            {
                promptCount++;
                return true;
            }));

        Assert.True(execution.Proceed);
        Assert.Equal(1, promptCount);
        Assert.Contains(echoRoot, Normalize(execution.Output), StringComparison.Ordinal);
        Assert.Contains("project", Normalize(execution.Output), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confirm_WhenInteractiveDeclined_AbortsAfterEchoingTheRoot()
    {
        // The resolved root must be visible in the output even on the abort path: the
        // echo is the audit trail, and the operator declined something they were shown.
        string echoRoot = Path.Combine(_temp.Path, "confirm-decline-root");
        int promptCount = 0;

        (bool Proceed, string Output) execution = Capture(() => SquadTargetRootConfirmation.Confirm(
            echoRoot,
            SquadDeploymentScope.Project,
            "install",
            isInteractive: true,
            yes: false,
            readAnswer: _ =>
            {
                promptCount++;
                return false;
            }));

        Assert.False(execution.Proceed);
        Assert.Equal(1, promptCount);
        Assert.Contains(echoRoot, Normalize(execution.Output), StringComparison.Ordinal);
    }

    [Fact]
    public void Confirm_WhenYesFlagSet_ProceedsWithoutPrompting()
    {
        // N2: --yes exists for automation running under a pty, which would otherwise
        // hang on the prompt; the readAnswer seam must never be invoked.
        string echoRoot = Path.Combine(_temp.Path, "confirm-yes-root");

        (bool Proceed, string Output) execution = Capture(() => SquadTargetRootConfirmation.Confirm(
            echoRoot,
            SquadDeploymentScope.Global,
            "update",
            isInteractive: true,
            yes: true,
            readAnswer: _ => throw new InvalidOperationException("The confirmation prompt must be skipped when --yes is supplied.")));

        Assert.True(execution.Proceed);
        Assert.Contains(echoRoot, Normalize(execution.Output), StringComparison.Ordinal);
        Assert.Contains("global", Normalize(execution.Output), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confirm_WhenNonInteractive_ProceedsWithoutPrompting()
    {
        // N1: a scripted caller is deterministic by construction — the root is echoed
        // and the run proceeds, never prompted, never blocked on a flag.
        string echoRoot = Path.Combine(_temp.Path, "confirm-noninteractive-root");

        (bool Proceed, string Output) execution = Capture(() => SquadTargetRootConfirmation.Confirm(
            echoRoot,
            SquadDeploymentScope.Project,
            "uninstall",
            isInteractive: false,
            yes: false,
            readAnswer: _ => throw new InvalidOperationException("A non-interactive console must never be prompted.")));

        Assert.True(execution.Proceed);
        Assert.Contains(echoRoot, Normalize(execution.Output), StringComparison.Ordinal);
    }

    #endregion

    #region Command wiring contract (§6 T3 row d): confirmation before mutation

    [Fact]
    public void Install_WhenInteractiveConfirmationDeclined_ExitsTwoWithoutWritingAnything()
    {
        // N3: a decline is aborted-before-side-effects client input — exit 2 with the
        // decline message, and the lifecycle call (the only side-effecting step) must
        // never have been reached: zero release requests, zero filesystem writes.
        string targetDir = Path.Combine(_temp.Path, "decline-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "decline-user"));
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadStateStore stateStore = new(userPaths);

        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: true,
            readAnswer: _ => false);

        (int ExitCode, string Output) execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("Declined. No changes were made.", Normalize(execution.Output), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(targetDir, ".kyber-weave")));
        Assert.Empty(releaseSource.Requests);
    }

    [Fact]
    public void Install_WhenNonInteractive_ProceedsWithoutPromptingAndEchoesTheAbsoluteRoot()
    {
        // N1 wiring: a non-interactive mutating run echoes the resolved absolute root
        // and scope, then proceeds to a successful install without any prompt. The
        // scope word is asserted because the pre-existing success line does not name
        // it — its presence is what proves the N4 echo happened.
        string targetDir = Path.Combine(_temp.Path, "echo-root-target");
        Directory.CreateDirectory(targetDir);

        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "echo-root-user"));
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadStateStore stateStore = new(userPaths);
        int promptCount = 0;

        SquadInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false,
            readAnswer: _ =>
            {
                promptCount++;
                return true;
            });

        (int ExitCode, string Output) execution = Capture(() => command.Execute(
            null!,
            new SquadInstallSettings
            {
                Path = targetDir,
                Targets = ["codex"],
                Global = false,
                DryRun = false,
                Adopt = false
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(0, promptCount);
        string normalized = Normalize(execution.Output);
        Assert.Contains(Path.GetFullPath(targetDir), normalized, StringComparison.Ordinal);
        Assert.Contains("project", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(targetDir, ".kyber-weave")));
    }

    #endregion

    #region Strict CommandApp harness

    /// <summary>
    /// Runs the stub command through a fresh strict-configured CommandApp and returns
    /// its exit code together with whatever Spectre rendered.
    /// </summary>
    /// <remarks>
    /// Both swaps below touch process-global console state, so the body is routed
    /// through <see cref="ProcessConsoleCapture.Run{T}(Func{T})"/> — the same
    /// serialization gate every other capture in the suite uses. Ungated, a concurrent
    /// capture rebinding <see cref="AnsiConsole.Console"/> mid-run would either pull
    /// this run's renders into its own writer (empty capture) or have its own capture
    /// contaminated by the forwarding writer. The shared-console binding stays intact:
    /// the callback re-pins the static to <see cref="SharedRenderConsole.Console"/> so
    /// every render keeps flowing through the forwarding-writer channel, and
    /// <see cref="BindSharedRenderConsole"/> still performs the one-time binding at
    /// assembly load before any test runs.
    /// </remarks>
    private static (int ExitCode, string Output) RunThroughStrictApp(string[] arguments)
    {
        return Capture(() =>
        {
            using StringWriter writer = new();
            ForwardingWriter.Current = writer;
            AnsiConsole.Console = SharedRenderConsole.Console;
            try
            {
                SquadAppRegistrar registrar = new();
                CommandApp app = new(registrar);
                app.Configure(config =>
                {
                    config.SetApplicationName("kyber-weave");
                    config.UseStrictParsing();
                    config.AddCommand<PathBindingStubCommand>("install");
                });

                return (app.Run(arguments), writer.ToString());
            }
            finally
            {
                ForwardingWriter.Current = null;
            }
        }).Result;
    }

    private static (T Result, string Output) Capture<T>(Func<T> execute)
    {
        CapturedConsoleExecution<T> execution = ProcessConsoleCapture.Run(execute);
        return (execution.Result, execution.Output);
    }

    private static string Normalize(string output) => string.Join(
        ' ',
        output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Stub over the real settings type that records the instance the parser bound, so
    /// the assertion inspects what an operator's argv actually produced.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1812: An internal class that is apparently never instantiated",
        Justification = "Spectre.Console.Cli's type resolver instantiates the stub via reflection.")]
    private sealed class PathBindingStubCommand : Command<SquadInstallSettings>
    {
        internal static SquadInstallSettings? Captured { get; set; }
        protected override int Execute(CommandContext context, SquadInstallSettings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }

    /// <summary>
    /// Process-wide writer that forwards to whichever StringWriter the running capture
    /// has installed. Spectre.Console.Cli binds the console used for command-app error
    /// renders once per process — the first render wins and later renders keep writing
    /// to that first console's writer, which earlier captures have by then disposed.
    /// Rendering through one long-lived console whose target switches per capture is
    /// what keeps every strict-parse assertion reading its own output.
    /// </summary>
    private sealed class ForwardingWriter : TextWriter
    {
        public static TextWriter? Current { get; set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Current?.Write(value);

        public override void Write(string? value) => Current?.Write(value);
    }

    /// <summary>The single live console every strict-parse render is routed through.</summary>
    private static class SharedRenderConsole
    {
        public static readonly ForwardingWriter Writer = new();

        public static readonly IAnsiConsole Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(Writer)
        });
    }

    /// <summary>
    /// Registrar for the strict-parse app: pins the shared render console so no render
    /// can land in a disposed capture, and resolves everything else the way the default
    /// registrar does — instances directly, service/implementation pairs through
    /// Activator, and IEnumerable&lt;T&gt; services as arrays of their registered
    /// implementations.
    /// </summary>
    private sealed class SquadAppRegistrar : ITypeRegistrar
    {
        private readonly Dictionary<Type, object> _instances = new() { [typeof(IAnsiConsole)] = SharedRenderConsole.Console };
        private readonly Dictionary<Type, List<Type>> _implementations = [];

        public void Register(Type service, Type implementation)
        {
            if (!_implementations.TryGetValue(service, out List<Type>? known))
            {
                known = [];
                _implementations[service] = known;
            }

            known.Add(implementation);
        }

        public void RegisterInstance(Type service, object instance) => _instances[service] = instance;

        public void RegisterLazy(Type service, Func<object> factory)
        {
            // The pinned render console must not be replaced by the lazily registered
            // default console: later renders would land in a disposed capture's writer.
            if (!_instances.ContainsKey(service))
            {
                _instances[service] = factory();
            }
        }

        public ITypeResolver Build() => new SquadAppResolver(_instances, _implementations);
    }

    private sealed class SquadAppResolver(
        Dictionary<Type, object> instances,
        Dictionary<Type, List<Type>> implementations) : ITypeResolver
    {
        public object? Resolve(Type? type)
        {
            if (type is null)
            {
                return null;
            }

            if (instances.TryGetValue(type, out object? instance))
            {
                return instance;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                Type elementType = type.GetGenericArguments()[0];
                List<object> built = implementations
                    .SelectMany(pair => pair.Value)
                    .Where(elementType.IsAssignableFrom)
                    .Select(implementation => Activator.CreateInstance(implementation))
                    .Where(resolved => resolved is not null)
                    .Select(resolved => resolved!)
                    .ToList();
                Array typed = Array.CreateInstance(elementType, built.Count);
                for (int index = 0; index < built.Count; index++)
                {
                    typed.SetValue(built[index], index);
                }

                return typed;
            }

            if (implementations.TryGetValue(type, out List<Type>? candidates) && candidates.Count > 0)
            {
                return Activator.CreateInstance(candidates[^1]);
            }

            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception ex) when (ex is MissingMemberException or ArgumentException or MemberAccessException or NotSupportedException or TargetInvocationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Binds the process-global console to the shared render console at assembly load,
    /// before any test (here or elsewhere) runs the first CommandApp, so the render
    /// channel is fixed while a live forwarding writer exists. The binder run itself
    /// renders a parse error that is discarded with its writer.
    /// </summary>
    /// <remarks>
    /// Deliberately ungated. The runtime executes a module initializer exactly once,
    /// before any other code in the module runs or any of its static state is accessed,
    /// and blocks every other thread's first access to the module until it completes —
    /// a strictly stronger serialization than <see cref="ProcessConsoleCapture"/>'s
    /// gate, which lives in this same module and so could not be reached before this
    /// method finishes anyway. The gate is also the wrong tool here:
    /// <see cref="ProcessConsoleCapture.Run{T}(Func{T})"/> restores
    /// <see cref="AnsiConsole.Console"/> to its entry value on exit, which would write
    /// the pre-binding default console back over the process-lifetime binding these
    /// swaps exist to establish.
    /// </remarks>
    [ModuleInitializer]
    internal static void BindSharedRenderConsole()
    {
        AnsiConsole.Console = SharedRenderConsole.Console;
        ForwardingWriter.Current = new StringWriter();
        try
        {
            RunThroughStrictApp(["install", "--__render-channel-binder__"]);
        }
        catch (Exception)
        {
            // Binding must never break test assembly load.
        }
        finally
        {
            ForwardingWriter.Current = null;
        }
    }

    #endregion
}
