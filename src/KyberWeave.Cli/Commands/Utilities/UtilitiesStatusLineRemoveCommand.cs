using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.StatusLine;
using Spectre.Console;
using Spectre.Console.Cli;
using KyberWeave.Application.StatusLine;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>
///     Deletes the status-line files the Kyber Utilities receipt owns and that still match their
///     recorded digest. A locally edited file, and every file the receipt does not name, is left alone.
/// </summary>
/// <remarks>
///     The receipt is the only authority: a file is removed only when it is receipt-owned and its bytes
///     are still the ones a deploy wrote. An edited file stays in the receipt, so a later status still
///     reports the drift and a later remove can still see it. No harness settings file is ever touched.
/// </remarks>
public sealed class UtilitiesStatusLineRemoveCommand : Command<UtilitiesStatusLineSettings>
{
    private readonly IProcessEnvironment _environment;
    private readonly IStatusLineDeploymentService _plans;
    private readonly IAnsiConsole _console;

    /// <summary>Creates the command over the process environment its per-user roots are read from.</summary>
    /// <param name="environment">The process state the roots are read from.</param>
    /// <param name="plans">Plans and applies the removal.</param>
    /// <param name="console">Receives everything the operator reads.</param>
    public UtilitiesStatusLineRemoveCommand(
        IProcessEnvironment environment,
        IStatusLineDeploymentService plans,
        IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(console);

        _environment = environment;
        _plans = plans;
        _console = console;
    }

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        UtilitiesStatusLineSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        StatusLineTargetRoots roots;
        try
        {
            roots = UtilitiesStatusLineCommandComposition.ResolveRoots(_environment);
            foreach (StatusLineTarget target in UtilitiesStatusLineTargetCatalog.All)
            {
                _ = roots.ResolveStagingRoot(target);
            }
        }
        catch (ArgumentException ex)
        {
            UtilitiesStatusLineCommandComposition.WriteClientInputError(_console, ex.Message);
            return 2;
        }

        _console.MarkupLine("[bold]Kyber Utilities status line — remove[/]");

        bool anyReceipt = false;
        bool hasIssues = false;
        int removedTotal = 0;
        foreach (StatusLineTarget target in UtilitiesStatusLineTargetCatalog.All)
        {
            string token = UtilitiesStatusLineTargetCatalog.GetToken(target);

            StatusLineDeploymentPlan plan;
            try
            {
                if (_plans.ReadReceipt(target, roots) is null) continue;

                plan = _plans.CreateRemove(target, roots);
            }
            catch (Exception ex) when (
                ex is StatusLineDeploymentConflictException or InvalidDataException or
                    IOException or UnauthorizedAccessException or ArgumentException)
            {
                _console.MarkupLine(
                    $"  [red]invalid[/] {Markup.Escape(token)}: {Markup.Escape(ex.Message)}");
                hasIssues = true;
                continue;
            }

            anyReceipt = true;
            try
            {
                _plans.Apply(plan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _console.MarkupLine(
                    $"  [red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
                hasIssues = true;
                continue;
            }

            int removed = plan.PlannedFileChanges.Count;
            removedTotal += removed;
            _console.MarkupLine($"  [green]ok[/] {Markup.Escape(token)}: removed {removed} file(s)");

            foreach (StatusLineOwnedFile retained in plan.Receipt.Files)
                _console.MarkupLine(
                    $"  [yellow]kept[/] {Markup.Escape(token)}: {Markup.Escape(retained.RelativePath)} " +
                    "(modified locally)");
        }

        _console.WriteLine();
        if (!anyReceipt && !hasIssues)
        {
            _console.MarkupLine("[grey]No Kyber Utilities status-line deployment found. Nothing to remove.[/]");
            return 0;
        }

        if (hasIssues)
        {
            _console.MarkupLine("[red]Some owned files could not be removed.[/]");
            return 1;
        }

        _console.MarkupLine($"[green]Removed {removedTotal} owned status-line file(s).[/]");
        return 0;
    }

    public int Execute(CommandContext context, UtilitiesStatusLineSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }
}
