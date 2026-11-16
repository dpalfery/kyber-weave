using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
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
    private readonly SquadRunOptions _options;
    private readonly Func<ProcessStartInfo, int>? _runner;

    /// <summary>Creates the run command over the Squad services the host registers.</summary>
    /// <param name="instances">Ensures the instance the harness runs in.</param>
    /// <param name="selectionStore">Reads the preset the project selected.</param>
    /// <param name="presetStore">Loads the selected preset.</param>
    /// <param name="options">How the harness process is found and started; the defaults use the real process.</param>
    public SquadRunCommand(
        ISquadInstanceService instances,
        ISquadModelPresetSelectionStore selectionStore,
        ISquadModelPresetStore presetStore,
        SquadRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(selectionStore);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(options);
        _instances = instances;
        _selectionStore = selectionStore;
        _presetStore = presetStore;
        _options = options;
        _runner = options.Runner;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadRunSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Harness))
        {
            SquadCommandComposition.WriteClientInputError(
                "No target harness specified. Specify a harness target (e.g. claude, codex).");
            return 2;
        }

        string projectRoot = Path.GetFullPath(_options.WorkingDirectory ?? ".");

        SquadTarget target;
        try
        {
            IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([settings.Harness]);
            if (targets.Count != 1)
            {
                SquadCommandComposition.WriteClientInputError(
                    $"Specify exactly one harness target. Received: '{settings.Harness}'.");
                return 2;
            }

            target = targets[0];
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        SquadModelPresetSelection? selection = _selectionStore.Read(projectRoot);
        if (selection is null || !selection.ModelPresets.TryGetValue(target, out string? presetName) ||
            string.IsNullOrWhiteSpace(presetName))
        {
            SquadCommandComposition.WriteClientInputError(
                $"No preset selected for {settings.Harness} in this project. Run `kyber-weave squad preset use <name> --target {settings.Harness}`.");
            return 2;
        }

        SquadInstanceRow row = SquadInstanceCatalog.GetRow(target);
        if (row.Status != SquadInstanceStatus.Ready)
        {
            string reason = !string.IsNullOrWhiteSpace(row.Reason)
                ? row.Reason
                : $"Support for {settings.Harness} is pending investigation.";
            SquadCommandComposition.WriteClientInputError(
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
                SquadCommandComposition.WriteLine(
                    $"[red]error:[/] Model preset '{Markup.Escape(presetName)}' for target '{Markup.Escape(targetToken)}' not found.");
                return 1;
            }

            ensureResult = _instances.EnsureAsync(target, presetName, cancellationToken).GetAwaiter()
                .GetResult();
        }
        catch (SquadLinkException ex)
        {
            SquadCommandComposition.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (SquadDeploymentConflictException ex)
        {
            SquadCommandComposition.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SquadCommandComposition.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = SquadHarnessLauncher.BuildStartInfo(
                row,
                ensureResult.InstanceRoot,
                settings.Args,
                _options.GetEnv ?? Environment.GetEnvironmentVariable,
                _options.FindOnPath ?? (name => SquadHarnessLauncher.FindOnPath(name, _options.GetEnv)),
                OperatingSystem.IsWindows());
        }
        catch (FileNotFoundException ex)
        {
            SquadCommandComposition.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        startInfo.WorkingDirectory = projectRoot;

        Func<ProcessStartInfo, int> runner = _runner ?? ProcessRunner.RunInteractive;
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
