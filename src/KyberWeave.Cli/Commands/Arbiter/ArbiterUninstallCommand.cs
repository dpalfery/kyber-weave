using System.ComponentModel;
using System.Text.Json;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
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
public sealed class ArbiterUninstallCommand(
    IArbiterUninstaller uninstaller,
    ISquadStateStore stateStore,
    IProcessEnvironment environment,
    IUserHostConfigStore userConfig,
    IArbiterConfigResolver configs,
    ICredentialStore credentials,
    IFileSystem files,
    IPathResolver paths,
    IAnsiConsole console,
    IArbiterUserSettings userSettings) : Command<ArbiterUninstallSettings>
{
    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        ArbiterUninstallSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Project && settings.Global)
        {
            console.MarkupLine(
                "[red]kyber-weave arbiter: error: " +
                "--global and --project are mutually exclusive. " +
                $"[bold]{RuleValidator.InstallScopeConflict}[/] is the permanent rule id for this scope conflict.[/]");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(settings.PathOption) && !settings.Project)
        {
            console.MarkupLine(
                "[red]kyber-weave arbiter: error: --path is only valid with --project. " +
                "Use --project to target a project root, or omit --path for the default global scope.[/]");
            return 2;
        }

        if (!SquadTargetCatalog.TryResolve(settings.Target, out SquadTarget target))
        {
            string roster = string.Join(", ", SquadTargetCatalog.All.Select(SquadTargetCatalog.GetToken));
            console.MarkupLine(
                "[red]kyber-weave arbiter: error: " +
                $"Unknown target '{Markup.Escape(settings.Target ?? string.Empty)}'. " +
                $"Known targets: {Markup.Escape(roster)}, all.[/]");
            return 2;
        }

        SquadDeploymentScope scope;
        string root;
        if (settings.Project)
        {
            scope = SquadDeploymentScope.Project;
            root = FullPath(string.IsNullOrWhiteSpace(settings.PathOption)
                ? (string.IsNullOrWhiteSpace(settings.Path) ? "." : settings.Path)
                : settings.PathOption);
        }
        else if (settings.Global)
        {
            scope = SquadDeploymentScope.Global;
            root = FullPath(string.IsNullOrWhiteSpace(settings.Path) || string.Equals(settings.Path, ".", StringComparison.Ordinal)
                ? environment.HomeDirectory
                : settings.Path);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.Path) && !string.Equals(settings.Path, ".", StringComparison.Ordinal))
            {
                string candidate = FullPath(settings.Path);
                string home = FullPath(environment.HomeDirectory);
                if (string.Equals(candidate, home, StringComparison.Ordinal))
                {
                    scope = SquadDeploymentScope.Global;
                    root = candidate;
                }
                else
                {
                    bool hasGlobalReceipt;
                    try
                    {
                        hasGlobalReceipt = stateStore.ReadReceipt(candidate, SquadDeploymentScope.Global) is not null;
                    }
                    catch (Exception ex) when (ex is ArgumentException
                                                   or IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException
                                                   or InvalidDataException
                                                   or InvalidOperationException)
                    {
                        console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
                        return 1;
                    }

                    if (hasGlobalReceipt)
                    {
                        scope = SquadDeploymentScope.Global;
                        root = candidate;
                    }
                    else
                    {
                        scope = SquadDeploymentScope.Project;
                        root = candidate;
                    }
                }
            }
            else
            {
                scope = SquadDeploymentScope.Global;
                root = FullPath(environment.HomeDirectory);
            }
        }

        bool deployed;
        try
        {
            deployed = files.DirectoryExists(root)
                       && uninstaller.HasDeployment(root, target, scope);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException
                                       or InvalidOperationException)
        {
            console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (!deployed)
        {
            console.MarkupLine(
                $"[grey]No Arbiter deployment found at [bold]{Markup.Escape(root)}[/] " +
                $"for target '[bold]{Markup.Escape(SquadTargetCatalog.GetToken(target))}[/]'. " +
                "Nothing to uninstall.[/]");
            if (settings.RemoveKey && !RemoveKey(root, scope, settings, target))
                return 1;

            return 0;
        }

        try
        {
            uninstaller.Uninstall(root, scope, SquadTargetCatalog.GetToken(target));
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException
                                       or InvalidOperationException)
        {
            console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (scope == SquadDeploymentScope.Global)
        {
            try
            {
                userConfig.RemoveGlobalTarget(root, SquadTargetCatalog.GetToken(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                console.MarkupLine($"[yellow]warn:[/] Could not update user configuration: {Markup.Escape(ex.Message)}");
            }
        }

        if (settings.RemoveKey && !RemoveKey(root, scope, settings, target))
            return 1;

        console.MarkupLine(
            $"[green]Removed Arbiter hooks for target '[bold]{Markup.Escape(SquadTargetCatalog.GetToken(target))}[/]' " +
            $"from [bold]{Markup.Escape(root)}[/].[/]");
        return 0;
    }

    private string FullPath(string path) => paths.GetFullPath(path, environment.CurrentDirectory);

    /// <summary>Runs the command without cancellation; the entry-point tests use this.</summary>
    public int Execute(CommandContext context, ArbiterUninstallSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private bool RemoveKey(
        string root,
        SquadDeploymentScope scope,
        ArbiterUninstallSettings settings,
        SquadTarget target)
    {
        KyberWeaveConfigLoadResult loaded = configs.Load(root, settings.Config, scope == SquadDeploymentScope.Project);
        if (!loaded.Success)
        {
            console.MarkupLine(
                "[red]kyber-weave arbiter: error: the host configuration could not be loaded, so " +
                $"the stored key's origin is unknown: {Markup.Escape(loaded.Error ?? "unknown load failure")}[/]");
            return false;
        }

        ArbiterProviderConfig repositoryProvider = loaded.Config?.Arbiter.Provider ?? ArbiterProviderConfig.DefaultNone;
        ArbiterUserProviderResolution effective;
        try
        {
            effective = userSettings.Resolve(repositoryProvider, environment.HomeDirectory);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return false;
        }

        if (!ArbiterUserSettings.TryResolveEndpointOrigin(
                repositoryProvider,
                effective.Provider,
                effective.EndpointOverridden,
                environment.HomeDirectory,
                loaded.ConfigPath,
                out _,
                out string origin,
                out string? endpointError))
        {
            console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(endpointError)}[/]");
            return false;
        }

        if (uninstaller.AnotherTargetIsDeployed(root, target))
        {
            console.MarkupLine(
                $"[yellow]warn:[/] The key for {Markup.Escape(origin)} is stored per endpoint origin " +
                "and shared by every target that uses it. Another target is still deployed here, " +
                "so it will need the key re-stored.");
        }

        try
        {
            credentials.Delete(origin);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or Win32Exception)
        {
            console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(ex.Message)}[/]");
            return false;
        }

        console.MarkupLine($"[grey]Removed the stored key for {Markup.Escape(origin)}.[/]");
        return true;
    }
}
