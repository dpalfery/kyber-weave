using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
///     Removes a harness instance deployment and its recorded state without modifying main harness files.
/// </summary>
internal sealed partial class SquadInstanceRemoveCommand : Command<SquadInstanceRemoveSettings>
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex PresetNameRegex();
    private readonly ISquadInstanceService _instances;
    private readonly ISquadUserPaths _userPaths;
    private readonly ISquadFileAccess _files;
    private readonly SquadConsoleOptions _console;

    /// <summary>Creates the instance remove command over the Squad services the host registers.</summary>
    /// <param name="instances">Removes the instance.</param>
    /// <param name="userPaths">Names the home the instances live under.</param>
    /// <param name="files">Probes the instance directories.</param>
    /// <param name="console">How the command prompts; the defaults read the console.</param>
    public SquadInstanceRemoveCommand(
        ISquadInstanceService instances,
        ISquadUserPaths userPaths,
        ISquadFileAccess files,
        SquadConsoleOptions console)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(console);
        _instances = instances;
        _userPaths = userPaths;
        _files = files;
        _console = console;
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

        if (!PresetNameRegex().IsMatch(settings.Preset))
        {
            SquadCommandComposition.WriteClientInputError(
                $"Invalid preset name '{settings.Preset}'. Preset names must match '^[a-z0-9][a-z0-9-]{{0,63}}$'.");
            return 2;
        }

        ISquadUserPaths userPaths = _userPaths;
        string targetToken = SquadTargetCatalog.GetToken(target);
        string instanceRoot = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instances", targetToken,
            settings.Preset);
        string instanceStateDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instance-state",
            targetToken, settings.Preset);

        string expectedBase = Path.GetFullPath(Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instances", targetToken));
        string fullInstanceRoot = Path.GetFullPath(instanceRoot);
        string prefix = expectedBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullInstanceRoot.StartsWith(prefix, StringComparison.Ordinal) || fullInstanceRoot.Length <= prefix.Length)
        {
            SquadCommandComposition.WriteClientInputError(
                $"Instance path '{fullInstanceRoot}' escapes instance directory '{expectedBase}'.");
            return 2;
        }

        if (!_files.DirectoryExists(instanceRoot) && !_files.DirectoryExists(instanceStateDir))
        {
            SquadCommandComposition.WriteClientInputError($"Instance '{targetToken}/{settings.Preset}' not found.");
            return 2;
        }

        bool isInteractive = _console.IsInteractive ?? SquadCommandComposition.IsInteractiveConsole();
        if (isInteractive && !settings.Yes)
        {
            Func<string, bool> read = _console.ReadAnswer ?? SquadTargetRootConfirmation.ReadConsoleAnswer;
            if (!read($"Remove instance '{targetToken}/{settings.Preset}'?"))
            {
                SquadCommandComposition.WriteLine("[yellow]Declined by user.[/]");
                return 2;
            }
        }

        _instances.RemoveAsync(target, settings.Preset, cancellationToken).GetAwaiter().GetResult();

        SquadCommandComposition.WriteLine($"[green]Removed instance '{targetToken}/{settings.Preset}'.[/]");
        return 0;
    }

    /// <summary>Executes the command with default cancellation.</summary>
    public int Execute(CommandContext context, SquadInstanceRemoveSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }
}
