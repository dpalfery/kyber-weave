using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings common to the documentation commands.</summary>
public class DocsSettings : AnalysisSettings
{
    /// <summary>
    /// Overrides the configured roots entirely when supplied. Repeat the option for a
    /// repository that documents modules next to their code; the first wins where a single
    /// root is needed, matching <c>ontology.docs-root</c>.
    /// </summary>
    [CommandOption("--docs-root <DIR>")]
    [Description("Documentation root relative to the repository root. Repeat for several. Defaults to ontology config.")]
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds a repeated option only to an array; a " +
                        "collection type is resolved as a scalar and fails to convert.")]
    public string[] DocsRoots { get; set; } = [];
}
