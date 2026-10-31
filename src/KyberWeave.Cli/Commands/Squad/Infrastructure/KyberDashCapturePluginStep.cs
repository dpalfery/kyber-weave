using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>How the OpenCode capture-plugin step ended. Squad's own exit code never depends on it.</summary>
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
///     Runs <c>kyberdash kyber capture plugin install --harness opencode --preserve</c> after a Squad global
///     deployment, so the OpenCode capture plugin is installed or upgraded in the same root Squad just wrote to.
/// </summary>
/// <remarks>
///     KyberDash owns the plugin file and its receipt (ADR 0031). Squad calls the verb and never writes the file.
///     <c>--preserve</c> keeps a user-edited or foreign plugin file, so this step never overwrites one. The step
///     never throws and never changes Squad's exit code: the Squad deployment has already succeeded, and rolling
///     it back for a capture helper would trade a working install for an optional one.
/// </remarks>
internal sealed class KyberDashCapturePluginStep
{
    private const string UnknownCommandMarker = "unknown command";
    private const string VerdictPrefix = "opencode plugin: ";

    /// <summary>
    ///     The bound for the plugin verb. It starts KyberDash and reads or writes the OpenCode config, so it gets
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

    /// <summary>Runs the verb against one OpenCode global root and prints one or two lines about it.</summary>
    /// <param name="openCodeRoot">The OpenCode global root Squad resolved, passed as <c>--config-dir</c>.</param>
    /// <param name="dryRun">Whether the Squad run is a dry run; passed through as <c>--dry-run</c>.</param>
    public CapturePluginStepOutcome Run(string openCodeRoot, bool dryRun)
    {
        string? kyberdash = _locateKyberDash();
        if (kyberdash is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]skipped[/] OpenCode capture plugin: KyberDash was not found beside kyber-weave or on PATH. " +
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
        foreach (string argument in Arguments(openCodeRoot, dryRun))
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
                $"[yellow]warning:[/] OpenCode capture plugin step could not run KyberDash: {Markup.Escape(ex.Message)}. " +
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
                    "[yellow]skipped[/] OpenCode capture plugin: this KyberDash predates the capture plugin command. " +
                    "Run [bold]kyber-weave update[/] to upgrade it.");
                return CapturePluginStepOutcome.Skipped;
            }

            AnsiConsole.MarkupLine(
                $"[yellow]warning:[/] OpenCode capture plugin step failed (exit {result.ExitCode}): " +
                $"{Markup.Escape(FirstLine(combined))}. The Squad deployment is unchanged. " +
                "Retry with [bold]kyberdash kyber capture plugin install --harness opencode --preserve[/].");
            return CapturePluginStepOutcome.Warned;
        }

        string verdict = FirstLine(result.StandardOutput);
        AnsiConsole.MarkupLine(verdict.Length == 0
            ? "OpenCode capture plugin: ok"
            : Markup.Escape(verdict));
        if (PluginChanged(verdict))
            AnsiConsole.MarkupLine("[bold]Restart OpenCode to load the new capture plugin.[/]");

        return CapturePluginStepOutcome.Completed;
    }

    /// <summary>
    ///     Runs the step for a global deployment that resolved OpenCode. Anything else (no OpenCode target,
    ///     <c>--no-capture-plugin</c>, or no step injected) does nothing.
    /// </summary>
    /// <remarks>
    ///     The plugin is global capture, so a project-scope deployment never reaches here. The root comes from the
    ///     same resolver the lifecycle planned with, so the verb and Squad agree on the directory.
    /// </remarks>
    internal static void RunForGlobal(
        KyberDashCapturePluginStep? step,
        IReadOnlyList<SquadTarget> deployedTargets,
        bool noCapturePlugin,
        bool dryRun,
        ISquadGlobalRootResolver? globalRoots)
    {
        if (step is null || noCapturePlugin || !deployedTargets.Contains(SquadTarget.OpenCode))
            return;

        IReadOnlyList<(SquadTarget Target, string GlobalRoot)> roots =
            SquadCommandComposition.ResolveGlobalTargetRoots([SquadTarget.OpenCode], globalRoots);
        if (roots.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]skipped[/] OpenCode capture plugin: the OpenCode global root could not be resolved.");
            return;
        }

        step.Run(roots[0].GlobalRoot, dryRun);
    }

    /// <summary>Whether the verb's verdict means the plugin file on disk changed, so OpenCode must restart.</summary>
    private static bool PluginChanged(string verdict)
    {
        if (!verdict.StartsWith(VerdictPrefix, StringComparison.Ordinal))
            return false;

        string[] fields = verdict.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3 && fields[2] is "installed" or "upgraded";
    }

    private static List<string> Arguments(string openCodeRoot, bool dryRun)
    {
        List<string> arguments =
        [
            "kyber", "capture", "plugin", "install", "--harness", "opencode", "--preserve", "--config-dir", openCodeRoot
        ];
        if (dryRun)
            arguments.Add("--dry-run");
        return arguments;
    }

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
