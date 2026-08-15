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

    /// <summary>Creates a new uninstall command using default user paths.</summary>
    public SquadUninstallCommand()
    {
    }

    /// <summary>Creates a new uninstall command using injectable dependencies.</summary>
    public SquadUninstallCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadUninstallSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        string targetRoot = SquadCommandComposition.ResolveTargetRoot(settings.Path);
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        SquadReceipt? receipt = stateStore.ReadReceipt(targetRoot, scope);
        if (receipt is null)
        {
            AnsiConsole.MarkupLine($"[grey]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/]. Nothing to uninstall.[/]");
            return 0;
        }

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[bold]Dry-run:[/] would uninstall {receipt.Files.Count} files from [bold]{Markup.Escape(targetRoot)}[/]:");
            foreach (SquadOwnedFile file in receipt.Files)
            {
                AnsiConsole.MarkupLine($"  [red]remove[/] {Markup.Escape(file.RelativePath)}");
            }
            return 0;
        }

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(targetRoot, scope, receipt);
        SquadTransaction transaction = SquadCommandComposition.ResolveTransaction(stateStore, _userPaths);
        transaction.Execute(plan);

        AnsiConsole.MarkupLine($"[green]Successfully uninstalled Kyber-Squad from [bold]{Markup.Escape(targetRoot)}[/].[/]");
        return 0;
    }
}
