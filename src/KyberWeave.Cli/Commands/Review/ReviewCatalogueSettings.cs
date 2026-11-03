using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>Settings for <c>review catalogue</c>.</summary>
public sealed class ReviewCatalogueSettings : AnalysisSettings
{
    [CommandOption("--technology <SLUG>")]
    [Description("Technology slug to render catalogue gates for. Repeat for several. " +
                 "Defaults to the technologies declared in kyber-weave.yml.")]
    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] Technology { get; set; } = [];
}
