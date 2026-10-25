using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
///     Removes a harness instance deployment and its recorded state without modifying main harness files.
/// </summary>
public sealed class SquadInstanceRemoveCommand : Command<SquadInstanceRemoveSettings>
{
    private readonly SquadInstanceService? _instanceService;
    private readonly bool? _isInteractive;
    private readonly Func<string, bool>? _readAnswer;
    private readonly ISquadUserPaths? _userPaths;

    /// <summary>Creates a new squad instance remove command with default dependencies.</summary>
    public SquadInstanceRemoveCommand()
    {
    }

    /// <summary>Creates a new squad instance remove command with injectable dependencies.</summary>
    public SquadInstanceRemoveCommand(
        ISquadUserPaths? userPaths = null,
        SquadInstanceService? instanceService = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null)
    {
        _userPaths = userPaths;
        _instanceService = instanceService;
        _isInteractive = isInteractive;
        _readAnswer = readAnswer;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadInstanceRemoveSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            SquadCommandComposition.WriteClientInputError(
                "No target specified. Specify a target using --target <target>.");
            return 2;
        }

        SquadTarget target;
        try
        {
            IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([settings.Target]);
            target = targets[0];
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.Preset))
        {
            SquadCommandComposition.WriteClientInputError(
                "Preset name is required. Specify a preset using --preset <name>.");
            return 2;
        }

        ISquadUserPaths userPaths = _userPaths ?? SquadCommandComposition.ResolveUserPaths();
        string targetToken = SquadTargetCatalog.GetToken(target);
        string instanceRoot = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instances", targetToken,
            settings.Preset);
        string instanceStateDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instance-state",
            targetToken, settings.Preset);

        if (!Directory.Exists(instanceRoot) && !Directory.Exists(instanceStateDir))
        {
            SquadCommandComposition.WriteClientInputError($"Instance '{targetToken}/{settings.Preset}' not found.");
            return 2;
        }

        bool isInteractive = _isInteractive ?? SquadCommandComposition.IsInteractiveConsole();
        if (isInteractive && !settings.Yes)
        {
            Func<string, bool> read = _readAnswer ?? SquadTargetRootConfirmation.ReadConsoleAnswer;
            if (!read($"Remove instance '{targetToken}/{settings.Preset}'?"))
            {
                SquadCommandComposition.WriteLine("[yellow]Declined by user.[/]");
                return 2;
            }
        }

        SquadInstanceService service = _instanceService ?? SquadCommandComposition.CreateInstanceService(userPaths);
        service.RemoveAsync(target, settings.Preset, cancellationToken).GetAwaiter().GetResult();

        SquadCommandComposition.WriteLine($"[green]Removed instance '{targetToken}/{settings.Preset}'.[/]");
        return 0;
    }

    /// <summary>Executes the command with default cancellation.</summary>
    public int Execute(CommandContext context, SquadInstanceRemoveSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }
}
