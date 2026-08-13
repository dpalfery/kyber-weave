using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

public sealed class DocsAnalyzeSettings : DocsSettings
{
    [CommandOption("--fail-on <SEVERITY>")]
    [Description("Finding severity that returns nonzero: none | warning | error.")]
    [DefaultValue("none")]
    public string FailOn { get; set; } = "none";
}

public sealed class DocsReviewExportSettings : DocsSettings
{
    [CommandOption("--out <PATH>")]
    [Description("Destination for the versioned review candidate JSON bundle.")]
    public string OutputPath { get; set; } = string.Empty;
}

public sealed class DocsReviewImportSettings : DocsSettings
{
    [CommandOption("--in <PATH>")]
    [Description("Versioned review verdict JSON bundle to validate and import atomically.")]
    public string InputPath { get; set; } = string.Empty;
}

public sealed class DocsGlossarySettings : DocsSettings
{
    [CommandOption("--write")]
    [Description("Merge proposed senses into the managed glossary. Without this flag, preview only.")]
    public bool Write { get; set; }
}
