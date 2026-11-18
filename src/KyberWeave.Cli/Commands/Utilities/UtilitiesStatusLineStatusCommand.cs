using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;
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
    private readonly IStatusLineDeploymentService _plans;
    private readonly IStatusLineHostService _host;
    private readonly IFileSystemPathSemantics _semantics;
    private readonly IAnsiConsole _console;

    /// <summary>Creates the command over the process environment its per-user roots are read from.</summary>
    /// <param name="environment">The process state the roots are read from.</param>
    /// <param name="plans">Reads the receipts.</param>
    /// <param name="host">Compares each owned file with its recorded digest.</param>
    /// <param name="semantics">The filesystem's containment rules.</param>
    /// <param name="console">Receives everything the operator reads.</param>
    public UtilitiesStatusLineStatusCommand(
        IProcessEnvironment environment,
        IStatusLineDeploymentService plans,
        IStatusLineHostService host,
        IFileSystemPathSemantics semantics,
        IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(console);

        _environment = environment;
        _plans = plans;
        _host = host;
        _semantics = semantics;
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

        _console.MarkupLine("[bold]Kyber Utilities status line — status[/]");

        bool anyReceipt = false;
        bool hasIssues = false;
        foreach (StatusLineTarget target in UtilitiesStatusLineTargetCatalog.All)
        {
            StatusLineReceipt? receipt;
            string stagingRoot = roots.ResolveStagingRoot(target);
            try
            {
                receipt = _plans.ReadReceipt(target, roots);
            }
            catch (Exception ex) when (
                ex is InvalidDataException or StatusLineDeploymentConflictException or
                    IOException or UnauthorizedAccessException)
            {
                _console.MarkupLine(
                    $"  [red]invalid[/] {Markup.Escape(UtilitiesStatusLineTargetCatalog.GetToken(target))}: " +
                    $"{Markup.Escape(ex.Message)}");
                hasIssues = true;
                continue;
            }

            if (receipt is null) continue;

            anyReceipt = true;
            string token = UtilitiesStatusLineTargetCatalog.GetToken(target);
            _console.MarkupLine($"[bold]{Markup.Escape(token)}[/] ({Markup.Escape(stagingRoot)})");
            foreach (StatusLineOwnedFile file in receipt.Files)
                if (!ReportOwnedFile(stagingRoot, file))
                    hasIssues = true;
        }

        _console.WriteLine();
        if (!anyReceipt)
        {
            _console.MarkupLine("[red]No Kyber Utilities status-line deployment found.[/]");
            _console.MarkupLine("Run [bold]kyber-weave utilities statusline deploy[/] to stage the artifacts.");
            return 1;
        }

        if (hasIssues)
        {
            _console.MarkupLine("[red]Drift or missing files detected in the Kyber Utilities deployment.[/]");
            return 1;
        }

        _console.MarkupLine("[green]All owned status-line files match the recorded receipt.[/]");
        return 0;
    }

    public int Execute(CommandContext context, UtilitiesStatusLineSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>Reports one owned file. Returns <see langword="false" /> when it is missing or drifted.</summary>
    private bool ReportOwnedFile(string stagingRoot, StatusLineOwnedFile file)
    {
        string physicalPath;
        try
        {
            physicalPath = UtilitiesStatusLineCommandComposition.ResolveOwnedPhysicalPath(
                stagingRoot,
                file.RelativePath,
                _semantics);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _console.MarkupLine(
                $"  [red]invalid[/] {Markup.Escape(file.RelativePath)} (outside the staging root): " +
                $"{Markup.Escape(ex.Message)}");
            return false;
        }

        UtilitiesStatusLineFileState state =
            _host.EvaluateOwnedFile(physicalPath, file.Sha256);
        switch (state)
        {
            case UtilitiesStatusLineFileState.Ok:
                _console.MarkupLine($"  [green]ok[/] {Markup.Escape(file.RelativePath)}");
                return true;
            case UtilitiesStatusLineFileState.Missing:
                _console.MarkupLine($"  [red]missing[/] {Markup.Escape(file.RelativePath)}");
                return false;
            case UtilitiesStatusLineFileState.Unreadable:
                _console.MarkupLine($"  [red]unreadable[/] {Markup.Escape(file.RelativePath)}");
                return false;
            default:
                _console.MarkupLine($"  [yellow]drift[/] {Markup.Escape(file.RelativePath)} (modified)");
                return false;
        }
    }
}
