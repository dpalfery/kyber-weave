using System.ComponentModel;
using KyberWeave.Contracts.Paths;
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
/// Renames each legacy standards <c>README.md</c> to the folder-prefixed standard, writes
/// the four files a technology or role folder holds, and re-renders the Config Reg block
/// those renames make stale, so one run leaves the repository valid.
/// </summary>
public sealed class DocsStandardsMigrateCommand(
    IKyberWeaveConfigRepository configs,
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment,
    IIdGenerator ids,
    IAnsiConsole console) : Command<DocsStandardsMigrateSettings>
{
    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsStandardsMigrateSettings settings,
        CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!DocsCommandComposition.TryResolveConfig(
                configs, settings, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(console, report, settings, "docs standards migrate", "Folder");
            return 1;
        }

        StandardsFolderMigration migration = new(files, settings.Path, config, paths, environment, ids);
        StandardsFolderMigrationResult result;
        try
        {
            result = migration.Run(settings.DryRun);
        }
        catch (InvalidDataException exception)
        {
            console.MarkupLine(
                "[red]The Config Reg block in AGENTS.md could not be refreshed, so nothing was " +
                "migrated.[/] " + Markup.Escape(exception.Message));
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine(
                $"[red]The standards folders could not be read or written, so nothing was migrated.[/] " +
                Markup.Escape(exception.Message));
            return 1;
        }

        if (result.Changes.Count == 0 && !result.ConfigRegRefreshed)
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

        if (result.ConfigRegRefreshed)
        {
            console.MarkupLine(
                "  [green]refreshed[/] AGENTS.md (the generated Config Reg block only)");
        }

        if (settings.DryRun)
        {
            console.MarkupLine(
                $"[grey]Re-run without --dry-run to apply {result.Changes.Count} change(s).[/]");
        }

        return 0;
    }
}
