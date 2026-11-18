using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// One-command Arbiter install: ensures the host config, sets
/// <c>arbiter.enabled: true</c> non-destructively, renders the target through the
/// existing Squad install pipeline, applies the provider choice, stores the key in
/// the OS credential store, verifies, and prints the target's trust step.
/// </summary>
/// <remarks>
/// A second identical run is an idempotent no-op with a status line: no config
/// rewrite, no duplicate credential-store write, no re-render. The key is never
/// printed and never written to configuration, receipts, or deployed files; only
/// its presence in the store is reported. <c>--arbiter-key</c> is accepted with a
/// one-line argv/shell-history warning, while <c>--key-stdin</c> and
/// <c>TYPESAFE_API_KEY</c> remain the safer paths.
/// </remarks>
internal sealed class ArbiterInstallCommand(
    IArbiterInstallService installer,
    SquadConsoleOptions squadConsole,
    IAnsiConsole console,
    SquadConsole output,
    SquadTargetRootConfirmation confirmation) : Command<ArbiterInstallSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, ArbiterInstallSettings settings,
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

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            WriteError($"--target is required. Known targets: {Roster()}, all.");
            return 2;
        }

        string targetToken = settings.Target.Trim();
        if (!SquadTargetCatalog.TryResolve(targetToken, out _))
        {
            WriteError($"Unknown target '{targetToken}'. Known targets: {Roster()}, all.");
            return 2;
        }

        if (!IsSupportedProvider(settings.Provider))
        {
            WriteError($"--provider '{settings.Provider}' is not supported. Use none or systemone.");
            return 2;
        }

        ArbiterInstallPrepared prepared = installer.Prepare(new ArbiterInstallRequest(
            settings.Path,
            settings.PathOption,
            settings.Project,
            targetToken,
            settings.Config,
            settings.Provider,
            settings.ArbiterKey,
            settings.KeyStdin,
            settings.DryRun));

        Print(prepared.Events, plan: null);
        if (prepared.Plan is not { } plan)
            return prepared.ExitCode;

        if (!plan.DryRun && !confirmation.Confirm(
                plan.Root,
                plan.Scope,
                "install",
                squadConsole.IsInteractive ?? output.IsInteractive,
                settings.Yes,
                squadConsole.ReadAnswer))
        {
            console.MarkupLine("[yellow]Declined. No changes were made.[/]");
            return 2;
        }

        ArbiterInstallApplied applied = installer.Apply(plan, cancellationToken);
        Print(applied.Events, plan.Target);
        return applied.ExitCode;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use this.</summary>
    public int Execute(CommandContext context, ArbiterInstallSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private void Print(IReadOnlyList<ArbiterInstallEvent> events, SquadTarget? plan)
    {
        foreach (ArbiterInstallEvent item in events)
        {
            switch (item)
            {
                case ArbiterInstallProviderLine provider:
                    PrintProvider(provider);
                    break;
                case ArbiterInstallArgvKeyWarning:
                    console.MarkupLine(
                        "[yellow]warn:[/] --arbiter-key is visible in process argv and shell history; " +
                        $"prefer --key-stdin or {Markup.Escape(ArbiterKeyResolver.EnvVarName)}.");
                    break;
                case ArbiterInstallError error:
                    WriteError(error.Message);
                    break;
                case ArbiterInstallAlreadyInstalled done:
                    console.MarkupLine(
                        $"[grey]Arbiter already installed for target '[bold]{Markup.Escape(done.TargetToken)}[/]' " +
                        $"in '[bold]{Markup.Escape(done.Root)}[/]'. Nothing to do.[/]");
                    break;
                case ArbiterInstallStoredKey stored:
                    console.MarkupLine($"[green]ok[/] Stored a key for {Markup.Escape(stored.Origin)}.");
                    break;
                case ArbiterInstallDryRun dryRun:
                    CommandHelpers.WriteUnfolded(console, () =>
                        console.MarkupLine(
                            $"[bold]Dry-run:[/] planned {dryRun.PlannedFiles} deployed files for [bold]{Markup.Escape(dryRun.Root)}[/]."));
                    break;
                case ArbiterInstallDone done:
                    CommandHelpers.WriteUnfolded(console, () => console.MarkupLine(done.GlobalScope
                        ? $"[green]Successfully installed Arbiter for target '[bold]{Markup.Escape(done.TargetToken)}[/]' " +
                          $"in the global user-home scope '[bold]{Markup.Escape(done.Root)}[/]'.[/]"
                        : $"[green]Successfully installed Arbiter for target '[bold]{Markup.Escape(done.TargetToken)}[/]' " +
                          $"in '[bold]{Markup.Escape(done.Root)}[/]'.[/]"));
                    break;
                case ArbiterInstallTrustSteps trust:
                    SquadInstallCommand.WriteArbiterTrustSteps(output, plan is { } target ? [target] : [], trust.Wiring);
                    break;
                case ArbiterInstallWarnings warnings:
                    output.WriteTransactionWarnings(warnings.Warnings);
                    break;
                case ArbiterInstallScopeTable table:
                    PrintScopeTable(table);
                    break;
                case ArbiterInstallProbeWarning probe:
                    console.MarkupLine($"[yellow]warn:[/] {Markup.Escape(probe.Message)}");
                    break;
            }
        }
    }

    private void PrintProvider(ArbiterInstallProviderLine provider)
    {
        string note = provider.OverrideNote is null ? string.Empty : Markup.Escape(provider.OverrideNote);
        console.MarkupLine(provider switch
        {
            { Kept: true } =>
                $"Arbiter provider: [bold]{provider.KindName}[/] (kept; pass --provider to change it).{note}",
            { ChangedFrom: null } =>
                $"Arbiter provider: [bold]{provider.KindName}[/] (unchanged).{note}",
            _ =>
                $"Arbiter provider: [bold]{provider.KindName}[/] (changed from {provider.ChangedFrom}).{note}",
        });
    }

    private void PrintScopeTable(ArbiterInstallScopeTable table)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        console.MarkupLine("[bold]D13 global hook table[/]");
        console.MarkupLine("[bold]target[/] | [bold]tier[/] | [bold]path[/] | [bold]reason[/] | [bold]trust[/]");
        foreach (ArbiterInstallScopeRow row in table.Rows)
        {
            console.MarkupLine(
                $"  {Markup.Escape(row.Token)} | {Markup.Escape(row.Reason)} | {Markup.Escape(row.Path)} | " +
                $"{Markup.Escape(row.Reason)} | {Markup.Escape(row.Trust)}");
        }
    }

    private static bool IsSupportedProvider(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string trimmed = raw.Trim();
        return string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "systemone", StringComparison.OrdinalIgnoreCase);
    }

    private static string Roster()
    {
        return string.Join(", ", SquadTargetCatalog.All.Select(SquadTargetCatalog.GetToken));
    }

    private void WriteError(string message)
    {
        CommandHelpers.WriteUnfolded(console, () =>
            console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(message)}[/]"));
    }
}
