using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;

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

    private readonly IProcessExecutor _executor;
    private readonly Func<string?> _locateKyberDash;

    /// <summary>Creates a step over an executor and a locator for the <c>kyberdash</c> executable.</summary>
    /// <param name="executor">Runs the verb. Injected so tests never start a real process.</param>
    /// <param name="locateKyberDash">Returns the full path of <c>kyberdash</c>, or null when it is not installed.</param>
    public KyberDashCapturePluginStep(IProcessExecutor executor, Func<string?> locateKyberDash)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(locateKyberDash);
        _executor = executor;
        _locateKyberDash = locateKyberDash;
    }

    /// <summary>The production step: the shared process executor and the real KyberDash locator.</summary>
    public static KyberDashCapturePluginStep CreateDefault()
    {
        return new KyberDashCapturePluginStep(ProcessExecutor.WithTimeout(PluginVerbTimeout), LocateKyberDash);
    }

    /// <summary>Runs the verb against one OpenCode global root, prints its verdict, and relays its OpenCode guidance.</summary>
    /// <param name="openCodeRoot">The OpenCode global root Squad resolved, passed as <c>--config-dir</c>.</param>
    /// <param name="dryRun">Whether the Squad run is a dry run; passed through as <c>--dry-run</c>.</param>
    public CapturePluginStepOutcome Run(string openCodeRoot, bool dryRun)
    {
        return Run(SquadTarget.OpenCode, openCodeRoot, dryRun);
    }

    /// <summary>Runs the verb for one harness, against the root that harness resolves.</summary>
    /// <param name="harness">The harness whose capture plugin is installed; it names the verb's <c>--harness</c>.</param>
    /// <param name="globalRoot">
    ///     The root passed as <c>--config-dir</c>, or null when the verb is given no root at all. The Factory verb
    ///     refuses that flag and resolves its own root, so its call names none.
    /// </param>
    /// <param name="dryRun">Whether the Squad run is a dry run; passed through as <c>--dry-run</c>.</param>
    internal CapturePluginStepOutcome Run(SquadTarget harness, string? globalRoot, bool dryRun)
    {
        string name = DisplayName(harness);
        string? kyberdash = _locateKyberDash();
        if (kyberdash is null)
        {
            AnsiConsole.MarkupLine(
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
            result = _executor.Run(start, string.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception
                                       or UnauthorizedAccessException or TimeoutException)
        {
            AnsiConsole.MarkupLine(
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
                AnsiConsole.MarkupLine(
                    $"[yellow]skipped[/] {name} capture plugin: this KyberDash predates the capture plugin command. " +
                    "Run [bold]kyber-weave update[/] to upgrade it.");
                return CapturePluginStepOutcome.Skipped;
            }

            AnsiConsole.MarkupLine(
                $"[yellow]warning:[/] {name} capture plugin step failed (exit {result.ExitCode}): " +
                $"{Markup.Escape(FirstLine(combined))}. The Squad deployment is unchanged. " +
                $"Retry with [bold]kyberdash kyber capture plugin install --harness {SquadTargetCatalog.GetToken(harness)} --preserve[/].");
            return CapturePluginStepOutcome.Warned;
        }

        string verdict = FirstLine(result.StandardOutput);
        AnsiConsole.MarkupLine(verdict.Length == 0
            ? $"{name} capture plugin: ok"
            : Markup.Escape(verdict));
        foreach (string guidance in Guidance(harness, result.StandardOutput, verdict))
            AnsiConsole.MarkupLine(Markup.Escape(guidance));

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
    internal static void RunForGlobal(
        KyberDashCapturePluginStep? step,
        IReadOnlyList<SquadTarget> deployedTargets,
        bool noCapturePlugin,
        bool dryRun,
        ISquadGlobalRootResolver? globalRoots)
    {
        if (step is null || noCapturePlugin)
            return;

        foreach (SquadTarget harness in PluginHarnesses)
        {
            if (!deployedTargets.Contains(harness))
                continue;

            if (harness == SquadTarget.Factory)
            {
                step.Run(harness, null, dryRun);
                continue;
            }

            IReadOnlyList<(SquadTarget Target, string GlobalRoot)> roots =
                SquadCommandComposition.ResolveGlobalTargetRoots([harness], globalRoots);
            if (roots.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]skipped[/] {DisplayName(harness)} capture plugin: " +
                    $"the {DisplayName(harness)} global root could not be resolved.");
                continue;
            }

            step.Run(harness, roots[0].GlobalRoot, dryRun);
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

    /// <summary>
    ///     The guidance the verb printed after its verdict. The OpenCode verb marks every guidance line with its
    ///     harness prefix and prints other chatter beside them, so only the marked lines are relayed. The Factory
    ///     verb's stdout is a contract — the plugin line, the consent file and the consent reminder — and every
    ///     line in it names the plugin, so every line after the verdict is relayed.
    /// </summary>
    private static IReadOnlyList<string> Guidance(SquadTarget harness, string standardOutput, string verdict)
    {
        if (harness == SquadTarget.OpenCode)
            return KyberDashGuidance.HarnessOpenCodeLines(standardOutput);

        List<string> lines = [];
        foreach (string raw in standardOutput.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line == verdict || !line.StartsWith("factory", StringComparison.Ordinal))
                continue;

            lines.Add(line);
        }

        return lines;
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

    /// <summary>
    ///     Finds <c>kyberdash</c> beside the running <c>kyber-weave</c>, then on <c>PATH</c>. Returns null when it
    ///     is installed in neither place.
    /// </summary>
    private static string? LocateKyberDash()
    {
        string fileName = OperatingSystem.IsWindows() ? "kyberdash.exe" : "kyberdash";

        string? installDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(installDirectory))
        {
            string beside = Path.Combine(installDirectory, fileName);
            if (File.Exists(beside))
                return beside;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters Path.Combine rejects cannot hold kyberdash; keep searching.
            }

        return null;
    }
}
