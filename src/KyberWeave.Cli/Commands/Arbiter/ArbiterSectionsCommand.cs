using KyberWeave.Core.Arbiter.Facts;
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
        // The same containment and size cap as the served tool: a path outside the repository root, or a symbolic link
        // out of it, is refused the way the tool refuses it, and an oversized document is not read.
        string root = ArbiterRepoRoot.Find(System.IO.Path.GetFullPath("."));
        string? full = FileTextFacts.ResolveContainedFile(root, settings.Path);
        if (full is null)
        {
            AnsiConsole.MarkupLine($"[red]The document '{Markup.Escape(settings.Path)}' was not found under the repository root.[/]");
            return 1;
        }

        if (new FileInfo(full).Length > SectionSelector.MaxDocumentBytes)
        {
            AnsiConsole.MarkupLine($"[red]The document is larger than {SectionSelector.MaxDocumentBytes} bytes.[/]");
            return 1;
        }

        SectionSelection selection = new SectionSelector(new NoneProvider())
            .SelectAsync(full, settings.Task, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (selection.FailedSafe && selection.Selected.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]The document '{Markup.Escape(settings.Path)}' could not be read.[/]");
            return 1;
        }

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
