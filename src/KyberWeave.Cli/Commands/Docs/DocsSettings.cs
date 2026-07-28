using System.ComponentModel;
using Spectre.Console.Cli;
using KyberWeave.Cli.Commands;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings common to the documentation commands.</summary>
public class DocsSettings : AnalysisSettings
{
    [CommandOption("--docs-root <DIR>")]
    [Description("Documentation root relative to the repository root. Defaults to ontology config (6-Docs).")]
    [DefaultValue("6-Docs")]
    public string DocsRoot { get; set; } = "6-Docs";
}
