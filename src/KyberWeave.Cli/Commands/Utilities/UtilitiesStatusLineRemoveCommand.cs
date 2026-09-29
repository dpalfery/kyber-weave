using KyberWeave.Core.Utilities.StatusLine;
using Spectre.Console;
using Spectre.Console.Cli;

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
    private readonly StatusLineTargetRoots? _roots;

    /// <summary>Creates a remove command using the default per-user roots.</summary>
    public UtilitiesStatusLineRemoveCommand()
    {
    }

    /// <summary>Creates a remove command using injectable roots.</summary>
    internal UtilitiesStatusLineRemoveCommand(StatusLineTargetRoots? roots = null)
    {
        _roots = roots;
    }

    /// <inheritdoc />
    protected override int Execute(
        CommandContext context,
        UtilitiesStatusLineSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        StatusLineTargetRoots roots = _roots ?? UtilitiesStatusLineCommandComposition.ResolveRoots();

        AnsiConsole.MarkupLine("[bold]Kyber Utilities status line — remove[/]");

        bool anyReceipt = false;
        bool hasIssues = false;
        int removedTotal = 0;
        foreach (StatusLineTarget target in UtilitiesStatusLineTargetCatalog.All)
        {
            string token = UtilitiesStatusLineTargetCatalog.GetToken(target);

            StatusLineDeploymentPlan plan;
            try
            {
                if (StatusLineDeploymentPlan.ReadReceipt(target, roots) is null) continue;

                plan = StatusLineDeploymentPlan.CreateRemove(target, roots);
            }
            catch (Exception ex) when (
                ex is StatusLineDeploymentConflictException or InvalidDataException or
                    IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]invalid[/] {Markup.Escape(token)}: {Markup.Escape(ex.Message)}");
                hasIssues = true;
                continue;
            }

            anyReceipt = true;
            try
            {
                plan.Apply();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
                hasIssues = true;
                continue;
            }

            int removed = plan.PlannedFileChanges.Count;
            removedTotal += removed;
            AnsiConsole.MarkupLine($"  [green]ok[/] {Markup.Escape(token)}: removed {removed} file(s)");

            foreach (StatusLineOwnedFile retained in plan.Receipt.Files)
                AnsiConsole.MarkupLine(
                    $"  [yellow]kept[/] {Markup.Escape(token)}: {Markup.Escape(retained.RelativePath)} " +
                    "(modified locally or already gone)");
        }

        AnsiConsole.WriteLine();
        if (!anyReceipt)
        {
            AnsiConsole.MarkupLine("[grey]No Kyber Utilities status-line deployment found. Nothing to remove.[/]");
            return hasIssues ? 1 : 0;
        }

        if (hasIssues)
        {
            AnsiConsole.MarkupLine("[red]Some owned files could not be removed.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Removed {removedTotal} owned status-line file(s).[/]");
        return 0;
    }

    public int Execute(CommandContext context, UtilitiesStatusLineSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }
}
