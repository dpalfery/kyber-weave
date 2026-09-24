using System.Security.Cryptography;
using System.Threading;
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
    private readonly ISquadGlobalRootResolver? _globalRoots;

    /// <summary>Creates a new status command using default system paths.</summary>
    public SquadStatusCommand()
    {
    }

    /// <summary>Creates a new status command using injectable dependencies.</summary>
    internal SquadStatusCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _globalRoots = globalRoots;
    }

    /// <inheritdoc />
    protected override int Execute(CommandContext context, SquadStatusSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);

        // Coalesce the positional path with --path; invalid client input returns exit
        // code 2 before any resolution or work
        string? effectivePath;
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 2;
        }

        string targetRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);
        ISquadGlobalRootResolver globalRoots = _globalRoots ?? SquadCommandComposition.ResolveGlobalRoots();

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
                // A receipt's relative paths are target-relative: beneath the deployment root
                // for project scope, beneath each target's own global root otherwise. Joining
                // targetRoot directly would check a global deployment against the project
                // directory and report every file missing.
                fullPath = SquadDeploymentPlan.ResolveOwnedFilePath(scope, targetRoot, globalRoots, file);
            }
            catch (Exception)
            {
                AnsiConsole.MarkupLine($"  [red]invalid[/] {Markup.Escape(file.RelativePath)} (outside the deployment root)");
                hasIssues = true;
                continue;
            }

            if (!File.Exists(fullPath))
            {
                AnsiConsole.MarkupLine(StatusLine("red", "missing", file, scope));
                hasIssues = true;
                continue;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(StatusLine("yellow", "drift", file, scope) + " (modified)");
                hasIssues = true;
                continue;
            }

            AnsiConsole.MarkupLine(StatusLine("green", "ok", file, scope));
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

    /// <summary>
    /// Renders one status line. Global scope deploys the same relative path to several
    /// targets' roots, so the target token is part of the identity shown to the operator;
    /// project scope already encodes the target in the path itself.
    /// </summary>
    private static string StatusLine(string color, string state, SquadOwnedFile file, SquadDeploymentScope scope)
    {
        string suffix = scope == SquadDeploymentScope.Global ? $" ({file.Target})" : string.Empty;
        return $"  [{color}]{state.PadRight(7)}[/] {Markup.Escape(file.RelativePath)}{suffix}";
    }

    public int Execute(CommandContext context, SquadStatusSettings settings) => Execute(context, settings, CancellationToken.None);
}
