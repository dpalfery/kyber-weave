using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Utilities.StatusLine;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>
///     Reports ok, missing, or drift for every status-line file the Kyber Utilities receipt owns.
/// </summary>
/// <remarks>
///     The receipt decides what is checked, and the receipt lives in the per-user Kyber directory rather
///     than beneath the staging root, so status still reports after an operator deletes a staging root by
///     hand. It reads only Kyber-owned files; it never opens a harness settings file (D5).
/// </remarks>
public sealed class UtilitiesStatusLineStatusCommand : Command<UtilitiesStatusLineSettings>
{
    private readonly IProcessEnvironment _environment;

    /// <summary>Creates the command over the process environment its per-user roots are read from.</summary>
    /// <param name="environment">The process state the roots are read from.</param>
    public UtilitiesStatusLineStatusCommand(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _environment = environment;
    }

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        UtilitiesStatusLineSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        StatusLineTargetRoots roots = UtilitiesStatusLineCommandComposition.ResolveRoots(_environment);

        AnsiConsole.MarkupLine("[bold]Kyber Utilities status line — status[/]");

        bool anyReceipt = false;
        bool hasIssues = false;
        foreach (StatusLineTarget target in UtilitiesStatusLineTargetCatalog.All)
        {
            StatusLineReceipt? receipt;
            string stagingRoot = roots.ResolveStagingRoot(target);
            try
            {
                receipt = StatusLineDeploymentPlan.ReadReceipt(target, roots);
            }
            catch (Exception ex) when (ex is InvalidDataException or StatusLineDeploymentConflictException)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]invalid[/] {Markup.Escape(UtilitiesStatusLineTargetCatalog.GetToken(target))}: " +
                    $"{Markup.Escape(ex.Message)}");
                hasIssues = true;
                continue;
            }

            if (receipt is null) continue;

            anyReceipt = true;
            string token = UtilitiesStatusLineTargetCatalog.GetToken(target);
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(token)}[/] ({Markup.Escape(stagingRoot)})");
            foreach (StatusLineOwnedFile file in receipt.Files)
                if (!ReportOwnedFile(stagingRoot, file))
                    hasIssues = true;
        }

        AnsiConsole.WriteLine();
        if (!anyReceipt)
        {
            AnsiConsole.MarkupLine("[red]No Kyber Utilities status-line deployment found.[/]");
            AnsiConsole.MarkupLine("Run [bold]kyber-weave utilities statusline deploy[/] to stage the artifacts.");
            return 1;
        }

        if (hasIssues)
        {
            AnsiConsole.MarkupLine("[red]Drift or missing files detected in the Kyber Utilities deployment.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]All owned status-line files match the recorded receipt.[/]");
        return 0;
    }

    public int Execute(CommandContext context, UtilitiesStatusLineSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>Reports one owned file. Returns <see langword="false" /> when it is missing or drifted.</summary>
    private static bool ReportOwnedFile(string stagingRoot, StatusLineOwnedFile file)
    {
        string physicalPath;
        try
        {
            physicalPath = UtilitiesStatusLineCommandComposition.ResolveOwnedPhysicalPath(
                stagingRoot,
                file.RelativePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AnsiConsole.MarkupLine(
                $"  [red]invalid[/] {Markup.Escape(file.RelativePath)} (outside the staging root): " +
                $"{Markup.Escape(ex.Message)}");
            return false;
        }

        UtilitiesStatusLineFileState state =
            UtilitiesStatusLineFileCheck.Evaluate(physicalPath, file.Sha256);
        switch (state)
        {
            case UtilitiesStatusLineFileState.Ok:
                AnsiConsole.MarkupLine($"  [green]ok[/] {Markup.Escape(file.RelativePath)}");
                return true;
            case UtilitiesStatusLineFileState.Missing:
                AnsiConsole.MarkupLine($"  [red]missing[/] {Markup.Escape(file.RelativePath)}");
                return false;
            case UtilitiesStatusLineFileState.Unreadable:
                AnsiConsole.MarkupLine($"  [red]unreadable[/] {Markup.Escape(file.RelativePath)}");
                return false;
            default:
                AnsiConsole.MarkupLine($"  [yellow]drift[/] {Markup.Escape(file.RelativePath)} (modified)");
                return false;
        }
    }
}
