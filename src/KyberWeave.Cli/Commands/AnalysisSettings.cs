using System.ComponentModel;
using KyberWeave.Cli.Rendering;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands;

/// <summary>Settings common to the analysis commands (validate/lint/scan).</summary>
public class AnalysisSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Repository or artifact path to inspect, including its documentation corpus. Defaults to current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("-f|--format <FORMAT>")]
    [Description("Output format: table | json | sarif | markdown.")]
    [DefaultValue("table")]
    public string Format { get; set; } = "table";

    [CommandOption("--no-info")]
    [Description("Hide Info-level findings.")]
    public bool NoInfo { get; set; }

    [CommandOption("-c|--config <PATH>")]
    [Description("Path to kyber-weave.yml. Defaults to <path>/.kyber-weave/kyber-weave.yml (or legacy <path>/kyber-weave.yml) when present.")]
    public string? Config { get; set; }

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
    public OutputFormat ParsedFormat => Format.ToLowerInvariant() switch
    {
        "json" => OutputFormat.Json,
        "sarif" => OutputFormat.Sarif,
        "markdown" or "md" => OutputFormat.Markdown,
        _ => OutputFormat.Table
    };
#pragma warning restore CA1308
}
