using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Governance view: doc-type coverage by component.</summary>
public sealed class DocsCatalogCommand : Command<DocsSettings>
{
    public override int Execute(CommandContext context, DocsSettings settings)
    {
        DiagnosticReport report = new DiagnosticReport();
        if (!DocsCommandComposition.TryCreateLoader(settings, report, out DocumentLoader? loader))
        {
            CommandHelpers.Finish(report, settings, "docs catalog", "Document");
            return 1;
        }

        DocumentSet set = loader!.Load();

        Table table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Component");
        table.AddColumn("Documents");
        table.AddColumn("Doc types");
        table.AddColumn("Missing frontmatter");

        foreach ((string? component, IReadOnlyList<DocumentModel>? documents) in set.ByComponent().OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            IOrderedEnumerable<string> types = documents
                .Where(d => d.DocType != DocType.Unknown)
                .Select(d => d.DocType.ToString().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal);

            int missing = documents.Count(d => !d.HasFrontmatter);

            table.AddRow(
                new Markup(Markup.Escape(component)),
                new Markup(documents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Markup(Markup.Escape(string.Join(", ", types))),
                new Markup(missing == 0
                    ? "[green]0[/]"
                    : $"[yellow]{missing}[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[grey]{set.Documents.Count} documents in scope; " +
            $"{set.Documents.Count(d => d.HasFrontmatter)} with frontmatter.[/]");
        return 0;
    }
}
