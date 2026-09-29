using KyberWeave.Core.Utilities.StatusLine;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>
///     Stages the Kyber-owned status-line artifacts at per-user locations and prints the manual
///     activation snippet for each harness. It never writes or reads a harness settings file.
/// </summary>
/// <remarks>
///     Building a <see cref="StatusLineDeploymentPlan" /> is the dry run and the preflight at once: it
///     reads the receipt and the target paths and writes nothing, so <c>--dry-run</c> and the first half
///     of a real deploy are the same code path. Only <see cref="StatusLineDeploymentPlan.Apply" />
///     changes the filesystem.
/// </remarks>
public sealed class UtilitiesStatusLineDeployCommand : Command<UtilitiesStatusLineDeploySettings>
{
    private readonly IUtilitiesStatusLineArtifactSource? _artifacts;
    private readonly StatusLineTargetRoots? _roots;

    /// <summary>Creates a deploy command using the default per-user roots and product artifact tree.</summary>
    public UtilitiesStatusLineDeployCommand()
    {
    }

    /// <summary>Creates a deploy command using injectable dependencies.</summary>
    internal UtilitiesStatusLineDeployCommand(
        StatusLineTargetRoots? roots = null,
        IUtilitiesStatusLineArtifactSource? artifacts = null)
    {
        _roots = roots;
        _artifacts = artifacts;
    }

    /// <inheritdoc />
    protected override int Execute(
        CommandContext context,
        UtilitiesStatusLineDeploySettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IReadOnlyList<StatusLineTarget> targets;
        try
        {
            targets = UtilitiesStatusLineTargetCatalog.ParseSelection(settings.Target);
        }
        catch (ArgumentException ex)
        {
            UtilitiesStatusLineCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        StatusLineTargetRoots roots;
        try
        {
            roots = _roots ?? UtilitiesStatusLineCommandComposition.ResolveRoots();
            foreach (StatusLineTarget target in targets)
            {
                _ = roots.ResolveStagingRoot(target);
            }
        }
        catch (ArgumentException ex)
        {
            UtilitiesStatusLineCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        IUtilitiesStatusLineArtifactSource artifacts;
        try
        {
            artifacts = _artifacts ?? UtilitiesStatusLineCommandComposition.ResolveArtifactSource();
        }
        catch (DirectoryNotFoundException ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        bool hasIssues = false;
        foreach (StatusLineTarget target in targets)
            if (!DeployOne(target, roots, artifacts, settings.DryRun))
                hasIssues = true;

        return hasIssues ? 1 : 0;
    }

    public int Execute(CommandContext context, UtilitiesStatusLineDeploySettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private static bool DeployOne(
        StatusLineTarget target,
        StatusLineTargetRoots roots,
        IUtilitiesStatusLineArtifactSource artifacts,
        bool dryRun)
    {
        string token = UtilitiesStatusLineTargetCatalog.GetToken(target);

        IReadOnlyList<StatusLineDeploymentFile> files;
        StatusLineDeploymentPlan plan;
        try
        {
            files = artifacts.Load(target);
            plan = StatusLineDeploymentPlan.CreateDeploy(target, roots, files);
        }
        catch (Exception ex) when (
            ex is StatusLineDeploymentConflictException or InvalidDataException or
                IOException or UnauthorizedAccessException or ArgumentException)
        {
            AnsiConsole.MarkupLine(
                $"[red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
            return false;
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(token)}[/]");

        if (dryRun)
        {
            AnsiConsole.MarkupLine(
                $"  [grey]dry-run[/] would stage {plan.PlannedFileChanges.Count} file(s) beneath " +
                $"{Markup.Escape(plan.StagingRoot)}");
        }
        else
        {
            try
            {
                plan.Apply();
                UtilitiesStatusLineCommandComposition.ApplyExecutableBit(plan.StagingRoot, files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine(
                    $"[red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
                return false;
            }

            AnsiConsole.MarkupLine(
                $"  [green]ok[/] staged {plan.PlannedFileChanges.Count} file(s) beneath " +
                $"{Markup.Escape(plan.StagingRoot)}");
        }

        string? commandPath = UtilitiesStatusLineOutput.ResolvePrimaryCommandPath(target, plan.StagingRoot, files);
        if (commandPath is null)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]warn[/] the {Markup.Escape(token)} artifact set has no " +
                $"'{Markup.Escape(UtilitiesStatusLineArtifacts.PrimaryFileName(target))}' to activate.");
            return true;
        }

        UtilitiesStatusLineOutput.PrintActivationGuidance(target, commandPath);
        return true;
    }
}
