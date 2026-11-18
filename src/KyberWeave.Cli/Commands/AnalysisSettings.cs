using System.ComponentModel;
using KyberWeave.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands;

/// <summary>Settings common to the analysis commands (validate/lint/scan).</summary>
public class AnalysisSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Repository or artifact path to inspect, including its documentation corpus. Defaults to current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("-f|--format <FORMAT>")]
    [Description("Output format: table | lines | json | sarif | markdown. Defaults to table in an interactive terminal and to lines, one finding per line, when output is redirected.")]
    public string? Format { get; set; }

    [CommandOption("--no-info")]
    [Description("Hide Info-level findings.")]
    public bool NoInfo { get; set; }

    [CommandOption("-c|--config <PATH>")]
    [Description("Path to kyber-weave.yml. Defaults to <path>/.kyber-weave/kyber-weave.yml (or legacy <path>/kyber-weave.yml) when present.")]
    public string? Config { get; set; }

    /// <summary>
    /// The directory a relative plan or path argument is read against; null means the process's current directory.
    /// Not a command-line option: it exists so a test can stand in a directory without changing the process-wide one.
    /// </summary>
    internal string? WorkingDirectory { get; set; }

    /// <summary>The format the command writes: the one <c>--format</c> names, or the one that suits <paramref name="console"/>.</summary>
    /// <param name="console">The console the output goes to; it decides the default when no format was named.</param>
#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
    public OutputFormat FormatFor(IAnsiConsole console) => Format?.ToLowerInvariant() switch
    {
        null or "" => ReportRenderer.IsInteractive(console) ? OutputFormat.Table : OutputFormat.Lines,
        "json" => OutputFormat.Json,
        "sarif" => OutputFormat.Sarif,
        "markdown" or "md" => OutputFormat.Markdown,
        "lines" or "plain" or "text" => OutputFormat.Lines,
        _ => OutputFormat.Table
    };
#pragma warning restore CA1308
}
