using System.Diagnostics;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
///     Executes a harness instance configured with the active project preset.
/// </summary>
/// <remarks>
///     Enforces preset selection before execution, ensures instance assets are up to date
///     and linked, redirects configuration to the isolated instance root, and passes through
///     execution control and exit codes to the underlying harness process.
/// </remarks>
internal sealed class SquadRunCommand : Command<SquadRunSettings>
{
    private readonly ISquadInstanceService _instances;
    private readonly ISquadModelPresetSelectionStore _selectionStore;
    private readonly ISquadModelPresetStore _presetStore;
    private readonly ISquadHarnessLauncher _launcher;
    private readonly IProcessEnvironment _environment;
    private readonly SquadRunOptions _options;
    private readonly SquadConsole _output;
    private readonly IPathResolver _paths;
    private readonly Func<ProcessStartInfo, int>? _runner;

    /// <summary>Creates the run command over the Squad services the host registers.</summary>
    /// <param name="instances">Ensures the instance the harness runs in.</param>
    /// <param name="selectionStore">Reads the preset the project selected.</param>
    /// <param name="presetStore">Loads the selected preset.</param>
    /// <param name="launcher">Builds the harness start information and starts it on the console.</param>
    /// <param name="environment">Reads the process environment and working directory.</param>
    /// <param name="options">How the harness process is found and started; the defaults use the real process.</param>
    /// <param name="output">Where the command writes.</param>
    /// <param name="paths">Resolves the project root to an absolute path.</param>
    public SquadRunCommand(
        ISquadInstanceService instances,
        ISquadModelPresetSelectionStore selectionStore,
        ISquadModelPresetStore presetStore,
        ISquadHarnessLauncher launcher,
        IProcessEnvironment environment,
        SquadRunOptions options,
        SquadConsole output,
        IPathResolver paths)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(selectionStore);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(paths);
        _instances = instances;
        _selectionStore = selectionStore;
        _presetStore = presetStore;
        _launcher = launcher;
        _environment = environment;
        _options = options;
        _output = output;
        _paths = paths;
        _runner = options.Runner;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadRunSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Harness))
        {
            _output.WriteClientInputError(
                "No target harness specified. Specify a harness target (e.g. claude, codex).");
            return 2;
        }

        string projectRoot = _paths.GetFullPath(_options.WorkingDirectory ?? _environment.CurrentDirectory, _environment.CurrentDirectory);

        SquadTarget target;
        try
        {
            IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([settings.Harness]);
            if (targets.Count != 1)
            {
                _output.WriteClientInputError(
                    $"Specify exactly one harness target. Received: '{settings.Harness}'.");
                return 2;
            }

            target = targets[0];
        }
        catch (ArgumentException ex)
        {
            _output.WriteClientInputError(ex.Message);
            return 2;
        }

        SquadModelPresetSelection? selection = _selectionStore.Read(projectRoot);
        if (selection is null || !selection.ModelPresets.TryGetValue(target, out string? presetName) ||
            string.IsNullOrWhiteSpace(presetName))
        {
            _output.WriteClientInputError(
                $"No preset selected for {settings.Harness} in this project. Run `kyber-weave squad preset use <name> --target {settings.Harness}`.");
            return 2;
        }

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
        if (row.Status != SquadInstanceStatus.Ready)
        {
            string reason = !string.IsNullOrWhiteSpace(row.Reason)
                ? row.Reason
                : $"Support for {settings.Harness} is pending investigation.";
            _output.WriteClientInputError(
                $"{settings.Harness} cannot run on a preset instance: '{reason}'.");
            return 2;
        }

        SquadInstanceEnsureResult ensureResult;
        try
        {
            SquadModelPreset? preset = null;
            try
            {
                preset = _presetStore.TryLoad(presetName);
            }
            catch (ArgumentException)
            {
            }

            if (preset is null)
            {
                string targetToken = SquadTargetCatalog.GetToken(target);
                _output.WriteLine(
                    $"[red]error:[/] Model preset '{Markup.Escape(presetName)}' for target '{Markup.Escape(targetToken)}' not found.");
                return 1;
            }

            ensureResult = _instances.EnsureAsync(target, presetName, cancellationToken).GetAwaiter()
                .GetResult();
        }
        catch (SquadLinkException ex)
        {
            _output.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (SquadDeploymentConflictException ex)
        {
            _output.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _output.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = _launcher.PrepareLaunch(
                row,
                ensureResult.InstanceRoot,
                settings.Args,
                _options.GetEnv ?? _environment.GetVariable,
                _options.FindOnPath ?? (name => _launcher.FindOnPath(name, _options.GetEnv)),
                OperatingSystem.IsWindows());
        }
        catch (FileNotFoundException ex)
        {
            _output.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (ArgumentException ex)
        {
            _output.WriteClientInputError(ex.Message);
            return 2;
        }

        startInfo.WorkingDirectory = projectRoot;

        Func<ProcessStartInfo, int> runner = _runner ?? _launcher.RunInteractive;
        return runner(startInfo);
    }
}

/// <summary>How <c>squad run</c> finds and starts the harness process.</summary>
/// <param name="Runner">Starts the process and returns its exit code; null runs it on the console.</param>
/// <param name="GetEnv">Reads environment variables; null reads the process environment.</param>
/// <param name="FindOnPath">Finds an executable on the path; null searches the process path.</param>
/// <param name="WorkingDirectory">The project root; null is the current directory.</param>
internal sealed record SquadRunOptions(
    Func<ProcessStartInfo, int>? Runner = null,
    Func<string, string?>? GetEnv = null,
    Func<string, string?>? FindOnPath = null,
    string? WorkingDirectory = null);
