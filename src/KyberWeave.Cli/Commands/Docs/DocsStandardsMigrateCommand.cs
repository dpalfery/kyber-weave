using System.ComponentModel;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings for <c>kyber-weave docs standards migrate</c>.</summary>
public sealed class DocsStandardsMigrateSettings : DocsSettings
{
    [CommandOption("--dry-run")]
    [Description("Print the renames and additions without writing anything.")]
    public bool DryRun { get; set; }
}

/// <summary>
/// Renames each legacy standards <c>README.md</c> to the folder-prefixed standard and writes
/// the four files a technology or role folder holds.
/// </summary>
public sealed class DocsStandardsMigrateCommand(IKyberWeaveConfigRepository configs, IAnsiConsole console) : Command<DocsStandardsMigrateSettings>
{
    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsStandardsMigrateSettings settings,
        CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!DocsCommandComposition.TryResolveConfig(configs, settings, report, out _, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(console, report, settings, "docs standards migrate", "Folder");
            return 1;
        }

        StandardsFolderMigration migration = new(settings.Path, ontology);
        StandardsFolderMigrationResult result;
        try
        {
            result = migration.Run(settings.DryRun);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine(
                $"[red]The standards folders could not be read or written, so nothing was migrated.[/] " +
                Markup.Escape(exception.Message));
            return 1;
        }

        if (result.Changes.Count == 0)
        {
            console.MarkupLine(
                "[green]Nothing to migrate.[/] Every standards folder already has its four " +
                "files and the six headings in order.");
            return 0;
        }

        console.MarkupLine(settings.DryRun
            ? "[bold]Standards folders (dry run: nothing written)[/]"
            : "[bold]Standards folders[/]");

        foreach (StandardsFolderChange change in result.Changes)
        {
            string target = Markup.Escape(change.To);
            switch (change.Kind)
            {
                case StandardsFolderChangeKind.Rename:
                    console.MarkupLine(
                        $"  [green]renamed[/]  {Markup.Escape(change.From)} -> {target}");
                    break;

                case StandardsFolderChangeKind.Reshape:
                    console.MarkupLine($"  [green]reshaped[/] {target}");
                    break;

                default:
                    console.MarkupLine($"  [green]added[/]    {target}");
                    break;
            }
        }

        foreach (StandardSectionMove move in result.Moves)
        {
            console.MarkupLine(
                $"  [green]moved[/] '{Markup.Escape(move.Heading)}' under " +
                $"'## {Markup.Escape(move.Under)}' in {Markup.Escape(move.Standard)}");
        }

        if (settings.DryRun)
        {
            console.MarkupLine(
                $"[grey]Re-run without --dry-run to apply {result.Changes.Count} change(s).[/]");
        }

        return 0;
    }
}
