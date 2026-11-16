using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>
///     Checks the status-line deployment and its runtime prerequisites, and prints the activation
///     snippet. It never reads a harness settings file (D5).
/// </summary>
/// <remarks>
///     Doctor checks only what Kyber Utilities can see without touching the operator's settings: the
///     receipt checksums and the executable bit of every owned file, the declared runtime prerequisites,
///     each harness binary and its version, and — for <c>agy</c> — whether the optional KyberDash
///     recorder is present. A harness that is not installed is skipped rather than failed, because
///     requiring one harness of someone who runs another would make doctor useless to them.
/// </remarks>
public sealed class UtilitiesStatusLineDoctorCommand : Command<UtilitiesStatusLineSettings>
{
    private readonly IProcessEnvironment _environment;
    private readonly IStatusLineDeploymentService _plans;
    private readonly IStatusLineHostService _host;
    private readonly UtilitiesStatusLineProcessRunner _processRunner;

    /// <summary>Creates a doctor command over the process environment.</summary>
    /// <param name="environment">The process state the per-user roots and <c>PATH</c> are read from.</param>
    /// <param name="plans">Reads the receipts.</param>
    /// <param name="host">Probes <c>PATH</c>, the owned files' digests and their executable bits.</param>
    /// <param name="processRunner">Runs the harness version probe.</param>
    public UtilitiesStatusLineDoctorCommand(
        IProcessEnvironment environment,
        IStatusLineDeploymentService plans,
        IStatusLineHostService host,
        UtilitiesStatusLineProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(processRunner);

        _environment = environment;
        _plans = plans;
        _host = host;
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        UtilitiesStatusLineSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        StatusLineTargetRoots roots = UtilitiesStatusLineCommandComposition.ResolveRoots(_environment);

        AnsiConsole.MarkupLine("[bold]Kyber Utilities status line — doctor[/]");
        AnsiConsole.WriteLine();

        bool hasIssues = false;
        foreach (StatusLineTarget target in UtilitiesStatusLineTargetCatalog.All)
            if (!Diagnose(target, roots))
                hasIssues = true;

        AnsiConsole.WriteLine();
        if (hasIssues)
        {
            AnsiConsole.MarkupLine(
                "[red]Doctor found issues with the status-line deployment or its prerequisites.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]All checked status-line prerequisites and owned files are healthy.[/]");
        return 0;
    }

    public int Execute(CommandContext context, UtilitiesStatusLineSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>Diagnoses one harness. Returns <see langword="false" /> when it has an actionable issue.</summary>
    private bool Diagnose(
        StatusLineTarget target,
        StatusLineTargetRoots roots)
    {
        string token = UtilitiesStatusLineTargetCatalog.GetToken(target);
        string harnessExecutable = UtilitiesStatusLinePrerequisites.HarnessExecutable(target);
        if (!_host.IsOnPath(harnessExecutable))
        {
            AnsiConsole.MarkupLine(
                $"  [grey]skip[/] {Markup.Escape(token)}: '{Markup.Escape(harnessExecutable)}' " +
                "is not on PATH, so this harness is not installed.");
            return true;
        }

        bool healthy = true;
        string? version = UtilitiesStatusLineHarnessProbe.TryVersion(_processRunner, harnessExecutable);
        AnsiConsole.MarkupLine(
            $"  [green]ok[/] {Markup.Escape(token)}: harness '{Markup.Escape(harnessExecutable)}' " +
            $"({Markup.Escape(version ?? "version unknown")})");

        foreach (UtilitiesStatusLinePrerequisite prerequisite in UtilitiesStatusLinePrerequisites.For(target))
            if (_host.IsOnPath(prerequisite.Executable))
            {
                AnsiConsole.MarkupLine(
                    $"  [green]ok[/] {Markup.Escape(token)}: prerequisite '{Markup.Escape(prerequisite.Executable)}'");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"  [red]fail[/] {Markup.Escape(token)}: prerequisite " +
                    $"'{Markup.Escape(prerequisite.Executable)}' is missing — " +
                    Markup.Escape(prerequisite.Hint));
                healthy = false;
            }

        if (target == StatusLineTarget.Agy) healthy &= ReportKyberDash(token);

        healthy &= ReportOwnedFiles(target, roots, token);

        string stagingRoot = roots.ResolveStagingRoot(target);
        string commandPath = SquadPathPolicy.ResolveFile(
            stagingRoot,
            UtilitiesStatusLineArtifacts.PrimaryFileName(target));
        UtilitiesStatusLineOutput.PrintActivationGuidance(target, commandPath);

        return healthy;
    }

    /// <summary>
    ///     Reports the optional KyberDash recorder. Its absence is a warning, never a failure: the
    ///     <c>agy</c> hand-off is skipped silently when <c>kyberdash</c> is not on <c>PATH</c> (C8).
    /// </summary>
    private bool ReportKyberDash(string token)
    {
        if (_host.IsOnPath(UtilitiesStatusLinePrerequisites.KyberDashExecutable))
        {
            AnsiConsole.MarkupLine(
                $"  [green]ok[/] {Markup.Escape(token)}: optional recorder " +
                $"'{UtilitiesStatusLinePrerequisites.KyberDashExecutable}' is on PATH");
            return true;
        }

        AnsiConsole.MarkupLine(
            $"  [yellow]warn[/] {Markup.Escape(token)}: optional recorder " +
            $"'{UtilitiesStatusLinePrerequisites.KyberDashExecutable}' is not on PATH; the status line " +
            "still renders, but KyberDash will not receive agy status-line data (the hand-off is optional).");
        return true;
    }

    /// <summary>Checks every receipt-owned file's checksum and, where owed, its executable bit.</summary>
    private bool ReportOwnedFiles(StatusLineTarget target, StatusLineTargetRoots roots, string token)
    {
        StatusLineReceipt? receipt;
        try
        {
            receipt = _plans.ReadReceipt(target, roots);
        }
        catch (Exception ex) when (ex is InvalidDataException or StatusLineDeploymentConflictException)
        {
            AnsiConsole.MarkupLine(
                $"  [red]fail[/] {Markup.Escape(token)}: {Markup.Escape(ex.Message)}");
            return false;
        }

        if (receipt is null)
        {
            AnsiConsole.MarkupLine(
                $"  [grey]info[/] {Markup.Escape(token)}: not deployed. Run " +
                "'kyber-weave utilities statusline deploy'.");
            return true;
        }

        string stagingRoot = roots.ResolveStagingRoot(target);
        bool healthy = true;
        foreach (StatusLineOwnedFile file in receipt.Files)
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
                    $"  [red]fail[/] {Markup.Escape(token)}: {Markup.Escape(file.RelativePath)} " +
                    $"(outside the staging root): {Markup.Escape(ex.Message)}");
                healthy = false;
                continue;
            }

            UtilitiesStatusLineFileState state = _host.EvaluateOwnedFile(physicalPath, file.Sha256);
            if (state != UtilitiesStatusLineFileState.Ok)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]fail[/] {Markup.Escape(token)}: {Markup.Escape(file.RelativePath)} " +
                    $"({DescribeState(state)})");
                healthy = false;
                continue;
            }

            if (UtilitiesStatusLineArtifacts.RequiresExecutableBit(file.RelativePath) &&
                !_host.HasExecutableBit(physicalPath))
            {
                AnsiConsole.MarkupLine(
                    $"  [red]fail[/] {Markup.Escape(token)}: {Markup.Escape(file.RelativePath)} " +
                    "is not executable. Run 'kyber-weave utilities statusline deploy' again to restore the bit.");
                healthy = false;
                continue;
            }

            AnsiConsole.MarkupLine(
                $"  [green]ok[/] {Markup.Escape(token)}: {Markup.Escape(file.RelativePath)}");
        }

        return healthy;
    }

    private static string DescribeState(UtilitiesStatusLineFileState state)
    {
        return state switch
        {
            UtilitiesStatusLineFileState.Missing => "missing",
            UtilitiesStatusLineFileState.Unreadable => "unreadable",
            UtilitiesStatusLineFileState.Drift => "modified",
            _ => "ok"
        };
    }
}
