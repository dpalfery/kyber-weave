using System.ComponentModel;
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
public sealed class DocsStandardsMigrateCommand : Command<DocsStandardsMigrateSettings>
{
    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsStandardsMigrateSettings settings,
        CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!DocsCommandComposition.TryResolveConfig(settings, report, out _, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(report, settings, "docs standards migrate", "Folder");
            return 1;
        }

        StandardsFolderMigration migration = new(settings.Path, ontology);
        StandardsFolderMigrationResult result = migration.Run(settings.DryRun);

        if (result.Changes.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to migrate.[/] Every standards folder already has its four files.");
            return 0;
        }

        AnsiConsole.MarkupLine(settings.DryRun
            ? "[bold]Standards folders (dry run: nothing written)[/]"
            : "[bold]Standards folders[/]");

        foreach (StandardsFolderChange change in result.Changes)
        {
            string target = Markup.Escape(change.To);
            AnsiConsole.MarkupLine(change.Kind == StandardsFolderChangeKind.Rename
                ? $"  [green]renamed[/] {Markup.Escape(change.From)} -> {target}"
                : $"  [green]added[/]   {target}");
        }

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Re-run without --dry-run to apply {result.Changes.Count} change(s).[/]");
        }

        return 0;
    }
}
