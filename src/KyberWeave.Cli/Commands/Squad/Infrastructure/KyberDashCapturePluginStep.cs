using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;
using KyberWeave.Application.Update;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>How a capture-plugin step ended. Squad's own exit code never depends on it.</summary>
public enum CapturePluginStepOutcome
{
    /// <summary>The verb exited 0; its verdict line (installed, upgraded, kept-edited, …) was printed.</summary>
    Completed,

    /// <summary>The step did not run: KyberDash was not found, or it predates the verb.</summary>
    Skipped,

    /// <summary>The verb failed or could not start; a warning line names the failure.</summary>
    Warned
}

/// <summary>
///     Runs <c>kyberdash kyber capture plugin install --harness &lt;harness&gt; --preserve</c> after a Squad global
///     deployment, so the capture plugin of the deployed harness is installed or upgraded in the root that harness
///     loads it from.
/// </summary>
/// <remarks>
///     KyberDash owns the plugin file and its receipt (ADR 0031). Squad calls the verb and never writes the file.
///     <c>--preserve</c> keeps a user-edited or foreign plugin file, so this step never overwrites one. The step
///     never throws and never changes Squad's exit code: the Squad deployment has already succeeded, and rolling
///     it back for a capture helper would trade a working install for an optional one.
///     <para>
///         The OpenCode step is handed the root Squad resolved, as <c>--config-dir</c>. The Factory step is handed
///         no root at all: that flag selects the OpenCode config root, and the Factory verb refuses it — the
///         Factory harness resolves its own root (<c>$FACTORY_DIR</c>, else <c>~/.factory</c>), the same tree the
///         Factory file source reads, so the plugin and the file source always pair from one root.
///     </para>
/// </remarks>
internal sealed class KyberDashCapturePluginStep
{
    private const string UnknownCommandMarker = "unknown command";

    /// <summary>
    ///     The harnesses KyberDash installs a capture plugin for, in the order a deployment runs them. The OpenCode
    ///     step keeps the place it already had; the Factory step follows it.
    /// </summary>
    private static readonly IReadOnlyList<SquadTarget> PluginHarnesses = [SquadTarget.OpenCode, SquadTarget.Factory];

    /// <summary>
    ///     The bound for the plugin verb. It starts KyberDash and reads or writes the harness config, so it gets
    ///     more room than a probe. Two minutes is well past a healthy run and still ends a hang.
    /// </summary>
    private static readonly TimeSpan PluginVerbTimeout = TimeSpan.FromSeconds(120);

    private readonly IProcessRunner _runner;
    private readonly Func<string?> _locateKyberDash;
    private readonly SquadConsole _console;

    /// <summary>Creates a step over a process runner, a locator for the <c>kyberdash</c> executable and a console.</summary>
    /// <param name="runner">Runs the verb. Injected so tests never start a real process.</param>
    /// <param name="locateKyberDash">Returns the full path of <c>kyberdash</c>, or null when it is not installed.</param>
    /// <param name="console">Where the verdict, the guidance and the warnings are written.</param>
    public KyberDashCapturePluginStep(IProcessRunner runner, Func<string?> locateKyberDash, SquadConsole console)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locateKyberDash);
        ArgumentNullException.ThrowIfNull(console);
        _runner = runner;
        _locateKyberDash = locateKyberDash;
        _console = console;
    }

    /// <summary>The production step: the async process runner bounded for the verb, and the given locator.</summary>
    /// <param name="locator">Finds <c>kyberdash</c> beside the running binary or on <c>PATH</c>.</param>
    /// <param name="console">Where the step writes.</param>
    public static KyberDashCapturePluginStep Create(KyberDashLocator locator, SquadConsole console)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return new KyberDashCapturePluginStep(new CapturePluginProcessRunner(PluginVerbTimeout), locator.Locate, console);
    }

    /// <summary>Runs the verb against one OpenCode global root, prints its verdict, and relays its OpenCode guidance.</summary>
    /// <param name="openCodeRoot">The OpenCode global root Squad resolved, passed as <c>--config-dir</c>.</param>
    /// <param name="dryRun">Whether the Squad run is a dry run; passed through as <c>--dry-run</c>.</param>
    /// <param name="cancellationToken">Cancels the plugin verb run.</param>
    public Task<CapturePluginStepOutcome> RunAsync(
        string openCodeRoot,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(SquadTarget.OpenCode, openCodeRoot, dryRun, cancellationToken);
    }

    /// <summary>Runs the verb for one harness, against the root that harness resolves.</summary>
    /// <param name="harness">The harness whose capture plugin is installed; it names the verb's <c>--harness</c>.</param>
    /// <param name="globalRoot">
    ///     The root passed as <c>--config-dir</c>, or null when the verb is given no root at all. The Factory verb
    ///     refuses that flag and resolves its own root, so its call names none.
    /// </param>
    /// <param name="dryRun">Whether the Squad run is a dry run; passed through as <c>--dry-run</c>.</param>
    /// <param name="cancellationToken">Cancels the plugin verb run.</param>
    internal async Task<CapturePluginStepOutcome> RunAsync(
        SquadTarget harness,
        string? globalRoot,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        string name = DisplayName(harness);
        string? kyberdash = _locateKyberDash();
        if (kyberdash is null)
        {
            _console.Ansi.MarkupLine(
                $"[yellow]skipped[/] {name} capture plugin: KyberDash was not found beside kyber-weave or on PATH. " +
                "Install KyberDash, then run [bold]kyber-weave update[/].");
            return CapturePluginStepOutcome.Skipped;
        }

        ProcessStartInfo start = new(kyberdash)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in Arguments(harness, globalRoot, dryRun))
            start.ArgumentList.Add(argument);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(start, string.Empty, PluginVerbTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception
                                       or UnauthorizedAccessException or TimeoutException)
        {
            _console.Ansi.MarkupLine(
                $"[yellow]warning:[/] {name} capture plugin step could not run KyberDash: {Markup.Escape(ex.Message)}. " +
                "The Squad deployment is unchanged.");
            return CapturePluginStepOutcome.Warned;
        }

        string combined = result.StandardOutput + "\n" + result.StandardError;
        if (result.ExitCode != 0)
        {
            // An older KyberDash without the verb: commander refuses the unknown command. That is a version
            // gap to name, not a failure of the capture step.
            if (combined.Contains(UnknownCommandMarker, StringComparison.OrdinalIgnoreCase))
            {
                _console.Ansi.MarkupLine(
                    $"[yellow]skipped[/] {name} capture plugin: this KyberDash predates the capture plugin command. " +
                    "Run [bold]kyber-weave update[/] to upgrade it.");
                return CapturePluginStepOutcome.Skipped;
            }

            _console.Ansi.MarkupLine(
                $"[yellow]warning:[/] {name} capture plugin step failed (exit {result.ExitCode}): " +
                $"{Markup.Escape(FirstLine(combined))}. The Squad deployment is unchanged. " +
                $"Retry with [bold]kyberdash kyber capture plugin install --harness {SquadTargetCatalog.GetToken(harness)} --preserve[/].");
            return CapturePluginStepOutcome.Warned;
        }

        string verdict = FirstLine(result.StandardOutput);
        _console.Ansi.MarkupLine(verdict.Length == 0
            ? $"{name} capture plugin: ok"
            : Markup.Escape(verdict));
        foreach (string guidance in KyberDashGuidance.CapturePluginGuidanceLines(
                     SquadTargetCatalog.GetToken(harness),
                     result.StandardOutput,
                     verdict))
            _console.Ansi.MarkupLine(Markup.Escape(guidance));

        return CapturePluginStepOutcome.Completed;
    }

    /// <summary>
    ///     Runs the step for a global deployment that resolved a capture-plugin harness. Anything else (no such
    ///     target, <c>--no-capture-plugin</c>, or no step injected) does nothing.
    /// </summary>
    /// <remarks>
    ///     The plugin is global capture, so a project-scope deployment never reaches here. The OpenCode root comes
    ///     from the same resolver the lifecycle planned with, so the verb and Squad agree on the directory. The
    ///     Factory step is handed no root: that verb resolves the Factory harness's own root itself, so the two
    ///     steps can never share one root.
    /// </remarks>
    internal static async Task RunForGlobalAsync(
        KyberDashCapturePluginStep? step,
        IReadOnlyList<SquadTarget> deployedTargets,
        bool noCapturePlugin,
        bool dryRun,
        ISquadGlobalRootResolver globalRoots,
        CancellationToken cancellationToken = default)
    {
        if (step is null || noCapturePlugin)
            return;

        foreach (SquadTarget harness in PluginHarnesses)
        {
            if (!deployedTargets.Contains(harness))
                continue;

            if (harness == SquadTarget.Factory)
            {
                _ = await step.RunAsync(harness, null, dryRun, cancellationToken).ConfigureAwait(false);
                continue;
            }

            IReadOnlyList<(SquadTarget Target, string GlobalRoot)> roots =
                SquadCommandComposition.ResolveGlobalTargetRoots([harness], globalRoots);
            if (roots.Count == 0)
            {
                step._console.Ansi.MarkupLine(
                    $"[yellow]skipped[/] {DisplayName(harness)} capture plugin: " +
                    $"the {DisplayName(harness)} global root could not be resolved.");
                continue;
            }

            _ = await step.RunAsync(harness, roots[0].GlobalRoot, dryRun, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CapturePluginProcessRunner(TimeSpan defaultTimeout) : IProcessRunner
    {
        public bool IsAlive(int processId) => throw new NotSupportedException();

        public string? FindOnPath(string program) => throw new NotSupportedException();

        public async Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Program);

            ProcessStartInfo startInfo = new(request.Program)
            {
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);

            if (request.Environment is { } environment)
            {
                startInfo.Environment.Clear();
                foreach (KeyValuePair<string, string?> pair in environment)
                {
                    if (pair.Value is not null)
                        startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(
                    startInfo,
                    request.StandardInput ?? string.Empty,
                    request.Timeout ?? defaultTimeout,
                    request.MaxOutputBytes,
                    time: null,
                    cancellationToken).ConfigureAwait(false);

                return new ProcessRunResult(result.ExitCode, result.StandardOutput + result.StandardError, TimedOut: false)
                {
                    StandardOutput = result.StandardOutput,
                    StandardError = result.StandardError
                };
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                throw new FileNotFoundException($"command not found: {request.Program}", request.Program, ex);
            }
            catch (TimeoutException)
            {
                return new ProcessRunResult(-1, string.Empty, TimedOut: true)
                {
                    StandardOutput = string.Empty,
                    StandardError = string.Empty
                };
            }
        }
    }

    private static List<string> Arguments(SquadTarget harness, string? globalRoot, bool dryRun)
    {
        List<string> arguments =
        [
            "kyber", "capture", "plugin", "install", "--harness", SquadTargetCatalog.GetToken(harness), "--preserve"
        ];
        // No root means no --config-dir: the Factory verb resolves its own root and refuses the flag.
        if (globalRoot is not null)
        {
            arguments.Add("--config-dir");
            arguments.Add(globalRoot);
        }

        if (dryRun)
            arguments.Add("--dry-run");
        return arguments;
    }

    /// <summary>The name the harness carries in Squad's own lines; the verb's own lines are KyberDash's wording.</summary>
    private static string DisplayName(SquadTarget harness) => harness switch
    {
        SquadTarget.OpenCode => "OpenCode",
        SquadTarget.Factory => "Factory",
        _ => throw new ArgumentOutOfRangeException(
            nameof(harness),
            harness,
            "KyberDash installs a capture plugin for this harness.")
    };

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return string.Empty;
    }
}
