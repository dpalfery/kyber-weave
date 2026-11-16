using KyberWeave.Contracts.Repositories;
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
    private readonly IProcessEnvironment _environment;
    private readonly IProductRootLocator _productRoots;

    /// <summary>Creates a deploy command over the process environment and the product-tree locator.</summary>
    /// <param name="environment">The process state the per-user roots and the checkout are read from.</param>
    /// <param name="productRoots">Finds the artifact tree in the checkout.</param>
    public UtilitiesStatusLineDeployCommand(IProcessEnvironment environment, IProductRootLocator productRoots)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(productRoots);

        _environment = environment;
        _productRoots = productRoots;
    }

    /// <inheritdoc />
    public override int Execute(
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
            roots = UtilitiesStatusLineCommandComposition.ResolveRoots(_environment);
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
            artifacts = UtilitiesStatusLineCommandComposition.ResolveArtifactSource(_environment, _productRoots);
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
