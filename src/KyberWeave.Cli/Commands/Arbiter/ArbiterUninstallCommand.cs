using System.ComponentModel;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Removes Arbiter-owned files and hook blocks for one harness target. Only receipt-owned
/// artifacts go: standalone shim/plugin files and owned hook-block entries. The stored key
/// survives unless <c>--remove-key</c> is given; an unknown target exits 2 naming the roster;
/// a root with no deployment exits 0 as a no-op.
/// </summary>
public sealed class ArbiterUninstallCommand : Command<ArbiterUninstallSettings>
{
    private readonly IArbiterUninstaller? _uninstaller;

    /// <summary>Creates a new uninstall command using default collaborators.</summary>
    public ArbiterUninstallCommand()
    {
    }

    /// <summary>Creates a new uninstall command over an injected uninstaller.</summary>
    internal ArbiterUninstallCommand(IArbiterUninstaller? uninstaller)
    {
        _uninstaller = uninstaller;
    }

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        ArbiterUninstallSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!SquadTargetCatalog.TryResolve(settings.Target, out SquadTarget target))
        {
            string roster = string.Join(", ", SquadTargetCatalog.All.Select(SquadTargetCatalog.GetToken));
            AnsiConsole.MarkupLine(
                "[red]kyber-weave arbiter: error: " +
                $"Unknown target '{Markup.Escape(settings.Target ?? string.Empty)}'. " +
                $"Known targets: {Markup.Escape(roster)}, all.[/]");
            return 2;
        }

        string root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(settings.Path) ? "." : settings.Path);

        bool deployed;
        try
        {
            deployed = Directory.Exists(root) && ArbiterUninstaller.HasDeployment(root, target);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (!deployed)
        {
            AnsiConsole.MarkupLine(
                $"[grey]No Arbiter deployment found at [bold]{Markup.Escape(root)}[/] " +
                $"for target '[bold]{Markup.Escape(SquadTargetCatalog.GetToken(target))}[/]'. " +
                "Nothing to uninstall.[/]");
            if (settings.RemoveKey && !RemoveKey(root, settings))
                return 1;

            return 0;
        }

        try
        {
            IArbiterUninstaller uninstaller = _uninstaller ?? new ArbiterUninstaller(root);
            uninstaller.Uninstall(SquadTargetCatalog.GetToken(target));
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (settings.RemoveKey && !RemoveKey(root, settings))
            return 1;

        AnsiConsole.MarkupLine(
            $"[green]Removed Arbiter hooks for target '[bold]{Markup.Escape(SquadTargetCatalog.GetToken(target))}[/]' " +
            $"from [bold]{Markup.Escape(root)}[/].[/]");
        return 0;
    }

    /// <summary>Runs the command without cancellation; the entry-point tests use this.</summary>
    public int Execute(CommandContext context, ArbiterUninstallSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private static bool RemoveKey(string root, ArbiterUninstallSettings settings)
    {
        string endpoint = ArbiterSetupCommand.TypeSafeEndpoint;
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root, settings.Config);
        if (loaded.Success && !string.IsNullOrWhiteSpace(loaded.Config?.Arbiter.Provider.Endpoint))
            endpoint = loaded.Config.Arbiter.Provider.Endpoint;

        string origin = ArbiterKeyResolver.GetOrigin(endpoint) ?? ArbiterKeyResolver.TypeSafeOrigin;
        try
        {
            ArbiterCommandComposition.CredentialStore().Delete(origin);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or Win32Exception)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return false;
        }

        AnsiConsole.MarkupLine($"[grey]Removed the stored key for {Markup.Escape(origin)}.[/]");
        return true;
    }
}
