using System.Security.Cryptography;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Verifies the integrity of deployed Kyber-Squad files against the recorded ownership receipt.
/// </summary>
public sealed class SquadStatusCommand : Command<SquadStatusSettings>
{
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;

    /// <summary>Creates a new status command using default system paths.</summary>
    public SquadStatusCommand()
    {
    }

    /// <summary>Creates a new status command using injectable dependencies.</summary>
    internal SquadStatusCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadStatusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        string targetRoot = SquadCommandComposition.ResolveTargetRoot(settings.Path);
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        SquadReceipt? receipt = stateStore.ReadReceipt(targetRoot, scope);
        if (receipt is null)
        {
            AnsiConsole.MarkupLine($"[red]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/].[/]");
            AnsiConsole.MarkupLine("Run [bold]kyber-weave squad install[/] to deploy agents and skills.");
            return 1;
        }

        AnsiConsole.MarkupLine($"Kyber-Squad deployment at [bold]{Markup.Escape(targetRoot)}[/] ([grey]{(scope == SquadDeploymentScope.Global ? "global" : "project")}[/]):");

        bool hasIssues = false;
        foreach (SquadOwnedFile file in receipt.Files)
        {
            string fullPath;
            try
            {
                fullPath = SquadPathPolicy.ResolveFile(targetRoot, file.RelativePath);
            }
            catch (Exception)
            {
                AnsiConsole.MarkupLine($"  [red]invalid[/] {Markup.Escape(file.RelativePath)} (outside the deployment root)");
                hasIssues = true;
                continue;
            }

            if (!File.Exists(fullPath))
            {
                AnsiConsole.MarkupLine($"  [red]missing[/] {Markup.Escape(file.RelativePath)}");
                hasIssues = true;
                continue;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine($"  [yellow]drift[/]   {Markup.Escape(file.RelativePath)} (modified)");
                hasIssues = true;
                continue;
            }

            AnsiConsole.MarkupLine($"  [green]ok[/]      {Markup.Escape(file.RelativePath)}");
        }

        if (hasIssues)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Drift or missing files detected in Kyber-Squad deployment.[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]All deployed files match the recorded receipt.[/]");
        return 0;
    }
}
