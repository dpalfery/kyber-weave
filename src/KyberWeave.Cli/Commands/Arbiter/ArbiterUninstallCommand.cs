using System.ComponentModel;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad;
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
    private readonly SquadStateStore? _stateStore;

    /// <summary>Creates a new uninstall command using default collaborators.</summary>
    public ArbiterUninstallCommand()
    {
    }

    /// <summary>Creates a new uninstall command over injected collaborators.</summary>
    internal ArbiterUninstallCommand(IArbiterUninstaller? uninstaller, SquadStateStore? stateStore = null)
    {
        _uninstaller = uninstaller;
        _stateStore = stateStore;
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

        SquadStateStore stateStore;
        bool deployed;
        try
        {
            stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(
                null,
                Environment.GetEnvironmentVariable);
            deployed = Directory.Exists(root)
                       && ArbiterUninstaller.HasDeployment(root, target, stateStore);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException
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
            if (settings.RemoveKey && !RemoveKey(root, settings, target, stateStore))
                return 1;

            return 0;
        }

        try
        {
            IArbiterUninstaller uninstaller = _uninstaller ?? new ArbiterUninstaller(root, stateStore);
            uninstaller.Uninstall(SquadTargetCatalog.GetToken(target));
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException
                                       or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (settings.RemoveKey && !RemoveKey(root, settings, target, stateStore))
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

    private static bool RemoveKey(
        string root,
        ArbiterUninstallSettings settings,
        SquadTarget target,
        SquadStateStore stateStore)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root, settings.Config);
        if (!loaded.Success)
        {
            AnsiConsole.MarkupLine(
                "[red]kyber-weave arbiter: error: the host configuration could not be loaded, so " +
                $"the stored key's origin is unknown: {Markup.Escape(loaded.Error ?? "unknown load failure")}[/]");
            return false;
        }

        string endpoint = string.IsNullOrWhiteSpace(loaded.Config?.Arbiter.Provider.Endpoint)
            ? ArbiterSetupCommand.TypeSafeEndpoint
            : loaded.Config.Arbiter.Provider.Endpoint;

        string origin = ArbiterKeyResolver.GetOrigin(endpoint) ?? ArbiterKeyResolver.TypeSafeOrigin;

        if (AnotherTargetIsDeployed(root, target, stateStore))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]warn:[/] The key for {Markup.Escape(origin)} is stored per endpoint origin " +
                "and shared by every target that uses it. Another target is still deployed here, " +
                "so it will need the key re-stored.");
        }

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

    /// <summary>
    /// Whether the receipt records another target in this root. The key is stored per endpoint
    /// origin, not per target, so deleting it while another target remains deployed removes that
    /// target's auth too.
    /// </summary>
    /// <remarks>
    /// The warning is best effort: an unreadable receipt says nothing about the other targets,
    /// and <c>--remove-key</c> is the operator's explicit request either way.
    /// </remarks>
    internal static bool AnotherTargetIsDeployed(
        string root,
        SquadTarget target,
        SquadStateStore stateStore)
    {
        SquadReceipt? receipt;
        try
        {
            receipt = stateStore.ReadReceipt(root, SquadDeploymentScope.Project);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException
                                       or InvalidOperationException)
        {
            return false;
        }

        if (receipt is null)
            return false;

        string token = SquadTargetCatalog.GetToken(target);
        return receipt.Files.Any(file => !string.Equals(file.Target, token, StringComparison.Ordinal))
               || receipt.Blocks.Any(block => !string.Equals(block.Target, token, StringComparison.Ordinal));
    }
}
