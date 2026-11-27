using KyberWeave.Core.FileSystem;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Paths;
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
    private readonly SquadConsole _output;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;

    /// <summary>Creates the instance remove command over the Squad services the host registers.</summary>
    /// <param name="instances">Removes the instance.</param>
    /// <param name="userPaths">Names the home the instances live under.</param>
    /// <param name="files">Probes the instance directories.</param>
    /// <param name="console">How the command prompts; the defaults read the console.</param>
    /// <param name="output">Where the command writes and asks.</param>
    /// <param name="paths">Resolves the instance paths to the form the escape check compares.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    public SquadInstanceRemoveCommand(
        ISquadInstanceService instances,
        ISquadUserPaths userPaths,
        ISquadFileAccess files,
        SquadConsoleOptions console,
        SquadConsole output,
        IPathResolver paths,
        IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(userPaths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _instances = instances;
        _userPaths = userPaths;
        _files = files;
        _console = console;
        _output = output;
        _paths = paths;
        _environment = environment;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadInstanceRemoveSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            _output.WriteClientInputError(
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
            _output.WriteClientInputError(ex.Message);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.Preset))
        {
            _output.WriteClientInputError(
                "Preset name is required. Specify a preset using --preset <name>.");
            return 2;
        }

        if (!PresetNameRegex().IsMatch(settings.Preset))
        {
            _output.WriteClientInputError(
                $"Invalid preset name '{settings.Preset}'. Preset names must match '^[a-z0-9][a-z0-9-]{{0,63}}$'.");
            return 2;
        }

        ISquadUserPaths userPaths = _userPaths;
        string targetToken = SquadTargetCatalog.GetToken(target);
        string instanceRoot = Path.Combine(UserStatePaths.SquadInstancesDirectory(userPaths.HomeDirectory), targetToken,
            settings.Preset);
        string instanceStateDir = Path.Combine(UserStatePaths.SquadInstanceStateDirectory(userPaths.HomeDirectory),
            targetToken, settings.Preset);

        string expectedBase = FullPath(Path.Combine(UserStatePaths.SquadInstancesDirectory(userPaths.HomeDirectory), targetToken));
        string fullInstanceRoot = FullPath(instanceRoot);
        string prefix = expectedBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullInstanceRoot.StartsWith(prefix, StringComparison.Ordinal) || fullInstanceRoot.Length <= prefix.Length)
        {
            _output.WriteClientInputError(
                $"Instance path '{fullInstanceRoot}' escapes instance directory '{expectedBase}'.");
            return 2;
        }

        if (!_files.DirectoryExists(instanceRoot) && !_files.DirectoryExists(instanceStateDir))
        {
            _output.WriteClientInputError($"Instance '{targetToken}/{settings.Preset}' not found.");
            return 2;
        }

        bool isInteractive = _console.IsInteractive ?? _output.IsInteractive;
        if (isInteractive && !settings.Yes)
        {
            Func<string, bool> read = _console.ReadAnswer ?? _output.ReadAnswer;
            if (!read($"Remove instance '{targetToken}/{settings.Preset}'?"))
            {
                _output.WriteLine("[yellow]Declined by user.[/]");
                return 2;
            }
        }

        _instances.RemoveAsync(target, settings.Preset, cancellationToken).GetAwaiter().GetResult();

        _output.WriteLine($"[green]Removed instance '{targetToken}/{settings.Preset}'.[/]");
        return 0;
    }

    private string FullPath(string path) => _paths.GetFullPath(path, _environment.CurrentDirectory);

    /// <summary>Executes the command with default cancellation.</summary>
    public int Execute(CommandContext context, SquadInstanceRemoveSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }
}
