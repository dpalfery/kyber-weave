using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>Settings for <c>review gates</c>.</summary>
public sealed class ReviewGatesSettings : AnalysisSettings
{
    [CommandOption("-o|--out <PATH>")]
    [Description("Write the gate report to this path as JSON. Defaults to stdout only.")]
    public string? Out { get; set; }

    [CommandOption("--stop-on-failure")]
    [Description("Skip the remaining gates once a blocking gate fails.")]
    public bool StopOnFailure { get; set; }
}

/// <summary>Settings for <c>review duplicates</c>.</summary>
public sealed class ReviewDuplicatesSettings : AnalysisSettings
{
    [CommandOption("-o|--out <PATH>")]
    [Description("Write the duplicates report to this path as JSON. Defaults to stdout only.")]
    public string? Out { get; set; }
}

/// <summary>Settings for <c>review verdict</c>.</summary>
public sealed class ReviewVerdictSettings : AnalysisSettings
{
    [CommandOption("--findings <PATH>")]
    [Description("Path to the council's findings JSON document.")]
    public string? Findings { get; set; }

    [CommandOption("--gates <PATH>")]
    [Description("Path to the gate report JSON produced by 'review gates'.")]
    public string? Gates { get; set; }
}
