using System.ComponentModel;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings for <c>kyber-weave docs standards diff</c>.</summary>
public sealed class DocsStandardsDiffSettings : DocsSettings
{
    [CommandArgument(1, "[name]")]
    [Description("One declared technology or role folder; omit to compare every installed folder.")]
    public string? Name { get; set; }
}

/// <summary>
/// Prints how each installed standards folder differs from the current template, and writes
/// nothing.
/// </summary>
public sealed class DocsStandardsDiffCommand(IKyberWeaveConfigRepository configs, IAnsiConsole console) : Command<DocsStandardsDiffSettings>
{
    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsStandardsDiffSettings settings,
        CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!DocsCommandComposition.TryResolveConfig(configs, settings, report, out _, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(console, report, settings, "docs standards diff", "Folder");
            return 1;
        }

        StandardsDiffResult result;
        try
        {
            result = new StandardsTemplateDiff(settings.Path, ontology).Run(settings.Name);
        }
        catch (ArgumentException ex)
        {
            console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine(
                $"[red]The standards folders could not be read, so nothing was compared.[/] " +
                Markup.Escape(exception.Message));
            return 1;
        }

        if (result.Folders.Count == 0)
        {
            console.MarkupLine("[green]Nothing to compare.[/] No declared standards folder is installed.");
            return 0;
        }

        foreach (StandardsFolderDiff folder in result.Folders)
        {
            bool clean = folder.Missing.Count == 0
                && folder.HostOnly.Count == 0
                && folder.Changed.Count == 0;

            // Two docs roots can both hold a folder of the same name; naming the root keeps
            // them apart instead of printing the same label twice.
            string label = result.Folders.Count(other => other.Name == folder.Name) > 1
                ? $"{folder.Name} in {folder.Root}"
                : folder.Name;

            console.MarkupLine(clean
                ? $"[green]up to date[/] {Markup.Escape(label)}"
                : $"[bold]{Markup.Escape(label)}[/]");

            foreach (string file in folder.Missing)
            {
                console.MarkupLine($"  [yellow]missing[/]     {Markup.Escape(file)}");
            }

            foreach (string file in folder.Changed)
            {
                console.MarkupLine($"  [yellow]changed[/]     {Markup.Escape(file)}");
            }

            foreach (string file in folder.HostOnly)
            {
                console.MarkupLine($"  [grey]host only[/]   {Markup.Escape(file)}");
            }
        }

        return 0;
    }
}
