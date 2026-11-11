using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Sections;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Lists the sections of a markdown document that a task needs: one row per section with its id and line range.
/// </summary>
/// <remarks>
/// The command has no repository root to read configuration from, so it selects with <see cref="NoneProvider"/> and
/// lists every section; the served tool <c>arbiter_select_relevant_document_sections</c> uses the configured provider.
/// </remarks>
public sealed class ArbiterSectionsCommand : Command<ArbiterSectionsSettings>
{
    /// <summary>Prints each section's id, its line range and title. Exits 1 when the document is missing.</summary>
    public override int Execute(CommandContext context, ArbiterSectionsSettings settings, CancellationToken cancellationToken)
    {
        string full = System.IO.Path.GetFullPath(settings.Path);
        if (!File.Exists(full))
        {
            AnsiConsole.MarkupLine($"[red]The document '{Markup.Escape(settings.Path)}' was not found.[/]");
            return 1;
        }

        SectionSelection selection = new SectionSelector(new NoneProvider())
            .SelectAsync(full, settings.Task, cancellationToken)
            .GetAwaiter()
            .GetResult();
        foreach (DocumentSection section in selection.Selected)
        {
            AnsiConsole.MarkupLine(
                $"{Markup.Escape(section.Id)}  {section.StartLine}-{section.EndLine}  {Markup.Escape(section.Title)}");
        }

        AnsiConsole.MarkupLine($"[grey]{selection.Selected.Count} section(s) listed.[/]");
        return 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSectionsSettings settings) =>
        Execute(context, settings, CancellationToken.None);
}
