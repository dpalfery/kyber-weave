using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
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
public sealed class ArbiterSectionsCommand(
    IFileTextFacts fileText,
    IArbiterRepositoryLocator locator,
    IFileStatReader fileStats,
    IArbiterSectionSelectionService sections,
    IPathResolver paths,
    IProcessEnvironment environment,
    IAnsiConsole console) : Command<ArbiterSectionsSettings>
{
    /// <summary>Prints each section's id, its line range and title. Exits 2 on a usage error, and 1 when the document is missing.</summary>
    public override int Execute(CommandContext context, ArbiterSectionsSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A missing or empty --path is a usage error, the same as a missing required option elsewhere in Arbiter.
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            return UsageError("--path is required.");
        }

        // The same containment and size cap as the served tool: a path outside the repository root, or a symbolic link
        // out of it, is refused the way the tool refuses it, and an oversized document is not read.
        string root = locator.FindGitRoot(paths.GetFullPath(".", environment.CurrentDirectory));
        string? full = fileText.ResolveContainedFile(root, settings.Path);
        if (full is null)
        {
            console.MarkupLine($"[red]The document '{Markup.Escape(settings.Path)}' was not found under the repository root.[/]");
            return 1;
        }

        if (fileStats.GetLength(full) > SectionSelector.MaxDocumentBytes)
        {
            console.MarkupLine($"[red]The document is larger than {SectionSelector.MaxDocumentBytes} bytes.[/]");
            return 1;
        }

        SectionSelection selection = sections
            .SelectAsync(full, settings.Task, new NoneProvider(), cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (selection.FailedSafe && selection.Selected.Count == 0)
        {
            console.MarkupLine($"[red]The document '{Markup.Escape(settings.Path)}' could not be read.[/]");
            return 1;
        }

        foreach (DocumentSection section in selection.Selected)
        {
            console.MarkupLine(
                $"{Markup.Escape(section.Id)}  {section.StartLine}-{section.EndLine}  {Markup.Escape(section.Title)}");
        }

        console.MarkupLine($"[grey]{selection.Selected.Count} section(s) listed.[/]");
        return 0;
    }

    /// <summary>Prints a usage error on one line, as the other Arbiter commands do, and returns the usage exit code, 2.</summary>
    private int UsageError(string message)
    {
        CommandHelpers.WriteUnfolded(console, () =>
            console.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(message)}[/]"));
        return 2;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSectionsSettings settings) =>
        Execute(context, settings, CancellationToken.None);
}
