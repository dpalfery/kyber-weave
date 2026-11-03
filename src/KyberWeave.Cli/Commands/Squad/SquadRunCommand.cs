using System.Diagnostics;
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
public sealed class SquadRunCommand : Command<SquadRunSettings>
{
    private readonly Func<SquadTarget, string, Task<SquadInstanceEnsureResult>>? _ensureOverride;
    private readonly Func<string, string?>? _findOnPath;
    private readonly Func<string, string?>? _getEnv;
    private readonly Func<ProcessStartInfo, int>? _runner;
    private readonly SquadModelPresetSelectionStore? _selectionStore;
    private readonly ISquadUserPaths? _userPaths;
    private readonly string? _workingDirectory;

    /// <summary>Creates a new squad run command using default runtime dependencies.</summary>
    public SquadRunCommand()
    {
    }

    /// <summary>Creates a new squad run command using injectable dependencies.</summary>
    public SquadRunCommand(
        ISquadUserPaths? userPaths = null,
        SquadModelPresetSelectionStore? selectionStore = null,
        Func<ProcessStartInfo, int>? runner = null,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? findOnPath = null,
        string? workingDirectory = null,
        Func<SquadTarget, string, Task<SquadInstanceEnsureResult>>? ensureOverride = null)
    {
        _userPaths = userPaths;
        _selectionStore = selectionStore;
        _runner = runner;
        _getEnv = getEnv;
        _findOnPath = findOnPath;
        _workingDirectory = workingDirectory;
        _ensureOverride = ensureOverride;
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

        string projectRoot = Path.GetFullPath(_workingDirectory ?? ".");

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

        ISquadUserPaths userPaths = _userPaths ?? SquadCommandComposition.ResolveUserPaths();
        SquadModelPresetSelectionStore
            selectionStore = _selectionStore ?? new SquadModelPresetSelectionStore(userPaths);

        SquadModelPresetSelection? selection = selectionStore.Read(projectRoot);
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
            if (_ensureOverride is not null)
            {
                ensureResult = _ensureOverride(target, presetName).GetAwaiter().GetResult();
            }
            else
            {
                SquadModelPresetStore presetStore = new(userPaths);
                SquadModelPreset? preset = null;
                try
                {
                    preset = presetStore.TryLoad(presetName);
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

                SquadInstanceService instanceService = SquadCommandComposition.CreateInstanceService(userPaths);
                ensureResult = instanceService.EnsureAsync(target, presetName, cancellationToken).GetAwaiter()
                    .GetResult();
            }
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
                _getEnv ?? Environment.GetEnvironmentVariable,
                _findOnPath ?? (name => SquadHarnessLauncher.FindOnPath(name, _getEnv)),
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
