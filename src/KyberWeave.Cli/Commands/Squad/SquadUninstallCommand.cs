using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Uninstalls Kyber-Squad from a project or global deployment directory using its ownership receipt.
/// </summary>
public sealed class SquadUninstallCommand : Command<SquadUninstallSettings>
{
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;
    private readonly SquadLifecycleService? _lifecycleService;

    /// <summary>Creates a new uninstall command using default user paths.</summary>
    public SquadUninstallCommand()
    {
    }

    /// <summary>Creates a new uninstall command using injectable dependencies.</summary>
    public SquadUninstallCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        SquadLifecycleService? lifecycleService = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _lifecycleService = lifecycleService;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadUninstallSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        string targetRoot = SquadCommandComposition.ResolveTargetRoot(settings.Path);
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        SquadLifecycleService lifecycleService = _lifecycleService ?? SquadCommandComposition.CreateLifecycleService(
            userPaths: _userPaths,
            stateStore: stateStore);

        SquadUninstallRequest uninstallRequest = new(
            TargetRoot: targetRoot,
            Scope: scope,
            DryRun: settings.DryRun);

        try
        {
            SquadLifecycleResult result = lifecycleService.UninstallAsync(uninstallRequest).GetAwaiter().GetResult();
            if (result.Success)
            {
                if (result.Plan is null)
                {
                    AnsiConsole.MarkupLine($"[grey]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/]. Nothing to uninstall.[/]");
                    return 0;
                }

                if (settings.DryRun)
                {
                    int fileCount = result.Plan.Receipt.Files.Count;
                    AnsiConsole.MarkupLine($"[bold]Dry-run:[/] would uninstall {fileCount} files from [bold]{Markup.Escape(targetRoot)}[/]:");
                    foreach (SquadOwnedFile file in result.Plan.Receipt.Files)
                    {
                        AnsiConsole.MarkupLine($"  [red]remove[/] {Markup.Escape(file.RelativePath)}");
                    }

                    return 0;
                }

                AnsiConsole.MarkupLine($"[green]Successfully uninstalled Kyber-Squad from [bold]{Markup.Escape(targetRoot)}[/].[/]");
                return 0;
            }

            if (result.Errors is { Count: > 0 })
            {
                foreach (string error in result.Errors)
                {
                    AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(error)}[/]");
                }
            }

            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
